using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Events;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// DAG-based workflow execution engine — thin coordinator.
///
/// Public API is preserved; all logic is delegated to:
///   <see cref="WorkflowLifecycleManager"/>  — start, pause, resume, cancel, recovery
///   <see cref="WorkflowStepDispatcher"/>    — DAG evaluation and step execution
///   <see cref="WorkflowApprovalGate"/>      — human_approval gate steps
///   <see cref="WorkflowLoopController"/>    — convergence loop escalation
///   <see cref="WorkflowStepEvaluators"/>    — pure static evaluation helpers
///   <see cref="WorkflowInstanceStore"/>     — shared runtime state
/// </summary>
public class WorkflowEngine
{
    private readonly WorkflowInstanceStore    _store;
    private readonly WorkflowStepDispatcher   _dispatcher;
    private readonly WorkflowLifecycleManager _lifecycle;
    private readonly WorkflowApprovalGate     _approvalGate;
    private readonly ILogger<WorkflowEngine>  _logger;

    public WorkflowEngine(
        ITaskSubmissionService orchestrator,
        WorkflowDefinitionLoader loader,
        AgentLimitsConfig agentLimitsConfig,
        TaskAffinitiesConfig taskAffinitiesConfig,
        WorkflowPolicyEngine policyEngine,
        GitService gitService,
        ILogger<WorkflowEngine> logger,
        IWorkflowRepository? workflowRepository = null,
        IEventBus? eventBus = null,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        var factory      = loggerFactory ?? NullLoggerFactory.Instance;
        var effectiveBus = eventBus ?? new NullEventBus();

        _store = new WorkflowInstanceStore(
            workflowRepository, gitService, effectiveBus, logger);

        var loopController = new WorkflowLoopController(
            _store, factory.CreateLogger<WorkflowLoopController>());

        _approvalGate = new WorkflowApprovalGate(
            _store, factory.CreateLogger<WorkflowApprovalGate>());

        _dispatcher = new WorkflowStepDispatcher(
            _store, orchestrator, policyEngine, taskAffinitiesConfig, agentLimitsConfig,
            loopController, _approvalGate, gitService,
            factory.CreateLogger<WorkflowStepDispatcher>());

        _lifecycle = new WorkflowLifecycleManager(
            _store, _dispatcher, loader, workflowRepository, orchestrator,
            factory.CreateLogger<WorkflowLifecycleManager>());
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public Task<WorkflowInstance> StartAsync(StartWorkflowRequest req, CancellationToken ct)
        => _lifecycle.StartAsync(req, ct);

    /// <summary>
    /// Recover running/paused workflow instances from the database after a service restart.
    /// Called from ServiceLifetime.StartAsync before the orchestrator starts processing.
    /// </summary>
    public Task RecoverRunningInstancesAsync(CancellationToken ct)
        => _lifecycle.RecoverRunningInstancesAsync(ct);

    /// <summary>
    /// Called by AgentOrchestrator whenever any task update is received.
    /// Only acts on tasks that belong to a workflow step.
    /// </summary>
    public async Task OnTaskUpdateAsync(TaskStatusResponse status)
    {
        if (!_store.TaskToStep.TryGetValue(status.TaskId, out var stepRef))
            return;

        var (instanceId, stepId) = stepRef;

        using var _scope = _logger.BeginScope(
            new { TaskId = status.TaskId, WorkflowInstanceId = instanceId, StepId = stepId });

        if (!_store.TryGet(instanceId, out var inst, out var def))
            return;

        var lk = _store.GetLock(instanceId);
        await lk.WaitAsync();
        try
        {
            await _dispatcher.OnTaskCompletedAsync(status, inst, def, stepId);
        }
        finally
        {
            lk.Release();
        }
    }

    // ── Pause / Resume / Context Update ────────────────────────────────────────

    public Task PauseAsync(string instanceId, CancellationToken ct = default)
        => _lifecycle.PauseAsync(instanceId, ct);

    public Task ResumeAsync(string instanceId, CancellationToken ct = default)
        => _lifecycle.ResumeAsync(instanceId, ct);

    /// <summary>
    /// Update one or more workflow context variables while the workflow is running.
    /// Changed values are immediately available for {{variable}} substitution in
    /// subsequently-submitted steps.
    /// </summary>
    public Task UpdateContextAsync(
        string instanceId, Dictionary<string, string> updates, CancellationToken ct = default)
        => _lifecycle.UpdateContextAsync(instanceId, updates, ct);

    // ── Cancel ─────────────────────────────────────────────────────────────────

    public Task CancelAsync(string instanceId, CancellationToken ct = default)
        => _lifecycle.CancelAsync(instanceId, ct);

    // ── Human approval gate API ────────────────────────────────────────────────

    /// <summary>
    /// Called when the user approves or rejects a human_approval gate step.
    /// On approval: the step is marked Completed and downstream DAG evaluation resumes.
    /// On rejection: the step is marked Rejected and downstream steps are skipped.
    /// </summary>
    public async Task ApproveWorkflowStepAsync(
        string instanceId, string stepId, bool approved, string? comment,
        CancellationToken ct = default)
    {
        if (!_store.TryGet(instanceId, out var inst, out var def))
        {
            _logger.LogWarning("ApproveWorkflowStep: instance '{Id}' not found", instanceId);
            return;
        }

        var lk = _store.GetLock(instanceId);
        await lk.WaitAsync(ct);
        try
        {
            var stepExec = inst.StepExecutions.GetValueOrDefault(stepId);
            if (stepExec?.Status != WorkflowStepStatus.WaitingForApproval)
            {
                _logger.LogWarning(
                    "ApproveWorkflowStep: step '{StepId}' not in WaitingForApproval state (current: {Status})",
                    stepId, stepExec?.Status);
                return;
            }

            var didApprove = await _approvalGate.HandleApprovalAsync(
                instanceId, stepId, approved, comment, def, ct);

            if (didApprove)
                await _dispatcher.EvaluateNextStepsAsync(inst, def, stepId);

            if (WorkflowStepEvaluators.IsInstanceDone(inst, def))
            {
                inst.Status = inst.StepExecutions.Values
                    .Any(s => s.Status is WorkflowStepStatus.Failed or WorkflowStepStatus.Rejected)
                    ? WorkflowStatus.Failed
                    : WorkflowStatus.Completed;
                inst.CompletedAt = DateTime.UtcNow;
            }

            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    public List<WorkflowDefinition> GetAvailableDefinitions(string? workspacePath = null)
        => _lifecycle.GetAvailableDefinitions(workspacePath);

    public WorkflowInstance? GetInstance(string instanceId)
        => _store.Active.TryGetValue(instanceId, out var e) ? e.Inst : null;

    public List<WorkflowInstance> GetAllInstances()
        => _store.Active.Values.Select(e => e.Inst).ToList();

    /// <summary>Number of workflow instances currently in memory (Running or Paused).</summary>
    public int ActiveInstanceCount => _store.Count;
}
