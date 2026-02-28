using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// DAG-based workflow execution engine.
///
/// Responsibilities:
///   - Sequential and parallel step submission via ITaskSubmissionService
///   - Conditional routing (router nodes evaluated synchronously)
///   - Feedback loops (next: back-edges) capped by both YAML max_iterations and
///     the global AgentLimits:MaxIterations configuration value
///   - Context passing via {{step_id.output}} template variables
///   - Per-step policy enforcement (protected files, blocked agent types)
///   - Smart Router: falls back to TaskAffinities when no model is explicitly specified
///   - Pause/resume without losing pending steps
///   - Live context variable updates while the workflow is running
///   - SQLite persistence: instances survive service restarts
///
/// Cancel behavior (Item 2):
///   CancelAsync() calls ITaskSubmissionService.CancelTaskAsync() for every task that has
///   been submitted (whether it is still Queued or actively Running in the orchestrator),
///   then marks all remaining Pending steps as Skipped.
/// </summary>
public class WorkflowEngine
{
    private readonly ITaskSubmissionService _orchestrator;
    private readonly WorkflowDefinitionLoader _loader;
    private readonly AgentLimitsConfig _agentLimitsConfig;
    private readonly TaskAffinitiesConfig _taskAffinitiesConfig;
    private readonly WorkflowPolicyEngine _policyEngine;
    private readonly GitService _gitService;
    private readonly IWorkflowRepository? _workflowRepository;
    private readonly ILogger<WorkflowEngine> _logger;

    // instanceId → (instance, definition)
    private readonly ConcurrentDictionary<string, (WorkflowInstance Inst, WorkflowDefinition Def)> _active = new();

    // taskId → (instanceId, stepId) — reverse lookup for OnTaskUpdateAsync
    private readonly ConcurrentDictionary<string, (string InstanceId, string StepId)> _taskToStep = new();

    // Per-instance semaphore to serialize DAG evaluation (prevents races when parallel steps complete)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    // Pre-computed reverse adjacency: instanceId → (dependencyStepId → list of dependent stepIds)
    // Built at instance start; used by SubmitReadyStepsAsync to limit DAG evaluation to successors
    // of the just-completed step instead of scanning all N steps (P1 — O(n) → O(k))
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _revDepsCache = new();

    public event Action<WorkflowInstance>? OnWorkflowUpdate;

    /// <summary>
    /// Raised when a human_approval step becomes active, or when the convergence
    /// policy escalates to HUMAN_APPROVAL after max iterations are exceeded.
    /// Parameters: (instanceId, stepId, promptText)
    /// </summary>
    public event Action<string, string, string>? OnApprovalNeeded;

    public WorkflowEngine(
        ITaskSubmissionService orchestrator,
        WorkflowDefinitionLoader loader,
        AgentLimitsConfig agentLimitsConfig,
        TaskAffinitiesConfig taskAffinitiesConfig,
        WorkflowPolicyEngine policyEngine,
        GitService gitService,
        ILogger<WorkflowEngine> logger,
        IWorkflowRepository? workflowRepository = null)
    {
        _orchestrator         = orchestrator;
        _loader               = loader;
        _agentLimitsConfig    = agentLimitsConfig;
        _taskAffinitiesConfig = taskAffinitiesConfig;
        _policyEngine         = policyEngine;
        _gitService           = gitService;
        _workflowRepository   = workflowRepository;
        _logger               = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task<WorkflowInstance> StartAsync(StartWorkflowRequest req, CancellationToken ct)
    {
        // Resolve definition
        var def = FindDefinition(req.DefinitionId, req.WorkspacePath);
        if (def is null)
            throw new InvalidOperationException($"Workflow definition '{req.DefinitionId}' not found.");

        // Build instance
        var inst = new WorkflowInstance
        {
            DefinitionId         = def.Id,
            DefinitionName       = def.Name,
            InputContext         = req.Inputs ?? [],
            FilePaths            = req.FilePaths ?? [],
            DefaultModelId       = req.DefaultModelId,
            DefaultModelProvider = req.DefaultModelProvider,
            ModelEndpoint        = req.ModelEndpoint,
            WorkspacePath        = req.WorkspacePath,
            StepModelOverrides   = req.StepModelOverrides ?? [],
        };

        // Apply parameter defaults for any missing inputs
        foreach (var param in def.Parameters)
        {
            if (!inst.InputContext.ContainsKey(param.Name) && param.Default is not null)
                inst.InputContext[param.Name] = param.Default;
        }

        // Initialize all step executions as Pending
        foreach (var step in def.Steps)
            inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };

        _active[inst.InstanceId]    = (inst, def);
        _locks[inst.InstanceId]     = new SemaphoreSlim(1, 1);
        _revDepsCache[inst.InstanceId] = BuildReverseDeps(def);

        _logger.LogInformation(
            "Workflow '{Name}' started (instance {Id}, {StepCount} steps)",
            def.Name, inst.InstanceId, def.Steps.Count);

        // Persist before submitting steps so we don't lose it on crash
        await PersistInstanceAsync(inst);

        // Submit all root steps (no dependencies) — handles agent, tool, and constraint types
        await SubmitReadyStepsAsync(inst, def, ct);

        BroadcastUpdate(inst);
        return inst;
    }

    /// <summary>
    /// Recover running/paused workflow instances from the database after a service restart.
    /// Called from ServiceLifetime.StartAsync before the orchestrator starts processing.
    /// </summary>
    public async Task RecoverRunningInstancesAsync(CancellationToken ct)
    {
        if (_workflowRepository is null) return;

        var instances = await _workflowRepository.LoadRunningInstancesAsync();
        if (instances.Count == 0) return;

        _logger.LogInformation("Recovering {Count} workflow instance(s) from database", instances.Count);

        foreach (var inst in instances)
        {
            var def = FindDefinition(inst.DefinitionId, inst.WorkspacePath);
            if (def is null)
            {
                _logger.LogWarning(
                    "Cannot recover workflow instance {Id}: definition '{DefId}' not found",
                    inst.InstanceId, inst.DefinitionId);
                continue;
            }

            // Ensure all steps defined in the definition have execution records
            foreach (var step in def.Steps)
            {
                if (!inst.StepExecutions.ContainsKey(step.Id))
                    inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };
            }

            _active[inst.InstanceId]       = (inst, def);
            _locks[inst.InstanceId]        = new SemaphoreSlim(1, 1);
            _revDepsCache[inst.InstanceId] = BuildReverseDeps(def);

            // Re-register reverse lookup for steps that have a TaskId
            foreach (var (stepId, stepExec) in inst.StepExecutions)
            {
                if (stepExec.TaskId is not null
                    && stepExec.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Pending)
                {
                    _taskToStep[stepExec.TaskId] = (inst.InstanceId, stepId);
                }
            }

            // Tool steps that were Running when the service died cannot be recovered
            // (the process is gone). Mark them Failed so the DAG doesn't stall.
            foreach (var step in def.Steps.Where(s => s.Type == "tool"))
            {
                var exec = inst.StepExecutions[step.Id];
                if (exec.Status == WorkflowStepStatus.Running)
                {
                    exec.Error = "Service restarted while tool step was running; process lost.";
                    RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
                    SkipDownstream(step.Id, inst, def);
                }
            }

            // Re-submit any steps that were Pending (the orchestrator may not have their tasks).
            // Steps that were Running had their tasks recovered by AgentOrchestrator.StartProcessingAsync.
            var pendingSteps = def.Steps
                .Where(s => inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d =>
                                inst.StepExecutions.TryGetValue(d, out var e)
                                && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            if (pendingSteps.Count > 0)
                await SubmitReadyStepsAsync(inst, def, ct);

            // Re-schedule SLA timeouts for WaitingForApproval steps whose deadline was persisted (C2)
            foreach (var (stepId, stepExec) in inst.StepExecutions)
            {
                if (stepExec.Status != WorkflowStepStatus.WaitingForApproval
                    || stepExec.SlaDeadline is null) continue;

                var step = def.Steps.FirstOrDefault(s => s.Id == stepId);
                if (step is null) continue;

                var remaining = stepExec.SlaDeadline.Value - DateTime.UtcNow;
                // If deadline already passed, fire almost immediately to fail the step
                if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(100);

                ScheduleApprovalTimeout(inst.InstanceId, stepId, remaining, step.TimeoutAction, ct);
                _logger.LogInformation(
                    "Re-scheduled SLA timeout for step '{StepId}' in instance {Id} (remaining: {Remaining:g})",
                    stepId, inst.InstanceId, remaining);
            }

            _logger.LogInformation(
                "Recovered workflow '{Name}' (instance {Id}, {Pending} step(s) re-submitted)",
                inst.DefinitionName, inst.InstanceId, pendingSteps.Count);
        }
    }

    /// <summary>
    /// Called by AgentOrchestrator whenever any task update is received.
    /// Only acts on tasks that belong to a workflow step.
    /// </summary>
    public async Task OnTaskUpdateAsync(TaskStatusResponse status)
    {
        if (!_taskToStep.TryGetValue(status.TaskId, out var stepRef))
            return;

        var (instanceId, stepId) = stepRef;

        using var _scope = _logger.BeginScope(new { TaskId = status.TaskId, WorkflowInstanceId = instanceId, StepId = stepId });
        if (!_active.TryGetValue(instanceId, out var entry))
            return;

        var (inst, def) = entry;
        var lk = _locks[instanceId];
        await lk.WaitAsync();
        try
        {
            var stepExec = inst.StepExecutions[stepId];

            // Map task status → workflow step status
            switch (status.Status)
            {
                case AgentTaskStatus.Running:
                    RecordAudit(stepExec, WorkflowStepStatus.Running);
                    stepExec.StartedAt = status.StartedAt;
                    BroadcastUpdate(inst);
                    return; // more updates will come

                case AgentTaskStatus.Completed:
                    stepExec.Output      = status.Result?.Output;
                    stepExec.IssueCount  = status.Result?.Issues?.Count ?? 0;
                    stepExec.CompletedAt = status.CompletedAt;
                    RecordAudit(stepExec, WorkflowStepStatus.Completed);
                    break;

                case AgentTaskStatus.Failed:
                    stepExec.Error       = status.StatusMessage;
                    stepExec.CompletedAt = status.CompletedAt;
                    RecordAudit(stepExec, WorkflowStepStatus.Failed, status.StatusMessage);
                    SkipDownstream(stepId, inst, def);
                    break;

                case AgentTaskStatus.Cancelled:
                    RecordAudit(stepExec, WorkflowStepStatus.Skipped, "Task cancelled");
                    SkipDownstream(stepId, inst, def);
                    break;

                default:
                    return;
            }

            // Clean up reverse lookup for this task — it has reached a terminal state
            _taskToStep.TryRemove(status.TaskId, out _);

            _logger.LogDebug(
                "Workflow {InstanceId} step '{StepId}' → {Status}",
                instanceId, stepId, stepExec.Status);

            await EvaluateNextStepsAsync(inst, def, stepId);

            if (IsInstanceDone(inst, def))
            {
                inst.Status      = inst.StepExecutions.Values.Any(s => s.Status == WorkflowStepStatus.Failed)
                                     ? WorkflowStatus.Failed
                                     : WorkflowStatus.Completed;
                inst.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "Workflow '{Name}' {Status} (instance {Id})",
                    inst.DefinitionName, inst.Status, inst.InstanceId);
            }

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally
        {
            lk.Release();
        }
    }

    // ── Pause / Resume / Context Update (Item 5) ───────────────────────────────

    public async Task PauseAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, _) = entry;

        if (inst.Status != WorkflowStatus.Running) return;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = true;
            inst.Status   = WorkflowStatus.Paused;
            _logger.LogInformation(
                "Workflow '{Name}' paused (instance {Id}) — running tasks will complete but no new tasks submitted",
                inst.DefinitionName, instanceId);
            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    public async Task ResumeAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, def) = entry;

        if (!inst.IsPaused) return;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = false;
            inst.Status   = WorkflowStatus.Running;
            _logger.LogInformation(
                "Workflow '{Name}' resumed (instance {Id})", inst.DefinitionName, instanceId);

            // Re-evaluate: submit any steps whose dependencies are all done
            await SubmitReadyStepsAsync(inst, def, ct);

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    /// <summary>
    /// Update one or more workflow context variables while the workflow is running.
    /// Changed values are immediately available for {{variable}} substitution in
    /// subsequently-submitted steps.
    /// </summary>
    public async Task UpdateContextAsync(
        string instanceId,
        Dictionary<string, string> updates,
        CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, _) = entry;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            foreach (var (key, value) in updates)
                inst.InputContext[key] = value;

            _logger.LogInformation(
                "Workflow {Id}: context updated — keys: {Keys}", instanceId,
                string.Join(", ", updates.Keys));

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

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
        if (!_active.TryGetValue(instanceId, out var entry))
        {
            _logger.LogWarning("ApproveWorkflowStep: instance '{Id}' not found", instanceId);
            return;
        }

        var (inst, def) = entry;
        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            var stepExec = inst.StepExecutions.GetValueOrDefault(stepId);
            if (stepExec is null || stepExec.Status != WorkflowStepStatus.WaitingForApproval)
            {
                _logger.LogWarning(
                    "ApproveWorkflowStep: step '{StepId}' not in WaitingForApproval state (current: {Status})",
                    stepId, stepExec?.Status);
                return;
            }

            stepExec.CompletedAt = DateTime.UtcNow;

            if (approved)
            {
                stepExec.Output = string.IsNullOrWhiteSpace(comment) ? "Approved" : $"Approved: {comment}";
                RecordAudit(stepExec, WorkflowStepStatus.Completed, stepExec.Output);
                _logger.LogInformation(
                    "Workflow {Id} step '{StepId}' approved by user", instanceId, stepId);

                // Resume the DAG from this step
                if (inst.IsPaused)
                {
                    inst.IsPaused = false;
                    inst.Status   = WorkflowStatus.Running;
                }
                await EvaluateNextStepsAsync(inst, def, stepId);
            }
            else
            {
                var rejectReason = string.IsNullOrWhiteSpace(comment) ? "Rejected by user" : $"Rejected: {comment}";
                stepExec.Error = rejectReason;
                RecordAudit(stepExec, WorkflowStepStatus.Rejected, rejectReason);
                _logger.LogInformation(
                    "Workflow {Id} step '{StepId}' rejected by user", instanceId, stepId);
                SkipDownstream(stepId, inst, def);
            }

            if (IsInstanceDone(inst, def))
            {
                inst.Status      = inst.StepExecutions.Values.Any(s => s.Status is WorkflowStepStatus.Failed or WorkflowStepStatus.Rejected)
                                     ? WorkflowStatus.Failed
                                     : WorkflowStatus.Completed;
                inst.CompletedAt = DateTime.UtcNow;
            }

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    private void ScheduleApprovalTimeout(
        string instanceId, string stepId, TimeSpan delay, string timeoutAction, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                if (!_active.TryGetValue(instanceId, out var entry)) return;

                var (inst, def) = entry;
                var lk = _locks[instanceId];
                await lk.WaitAsync(ct);
                try
                {
                    var stepExec = inst.StepExecutions.GetValueOrDefault(stepId);
                    if (stepExec?.Status != WorkflowStepStatus.WaitingForApproval) return;

                    _logger.LogWarning(
                        "Workflow {Id} step '{StepId}' SLA ({Hours:F1}h) exceeded — action: {Action}",
                        instanceId, stepId, delay.TotalHours, timeoutAction);

                    var slaReason = $"SLA of {delay.TotalHours:F1} hour(s) exceeded with no human response.";
                    stepExec.Error       = slaReason;
                    stepExec.CompletedAt = DateTime.UtcNow;
                    RecordAudit(stepExec, WorkflowStepStatus.Failed, slaReason);
                    SkipDownstream(stepId, inst, def);

                    inst.Status      = WorkflowStatus.Failed;
                    inst.CompletedAt = DateTime.UtcNow;

                    await PersistInstanceAsync(inst);
                    BroadcastUpdate(inst);
                }
                finally { lk.Release(); }
            }
            catch (OperationCanceledException) { /* service shutdown or instance cancelled — expected */ }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error in approval SLA timeout handler for instance {Id} step '{StepId}'",
                    instanceId, stepId);
            }
        }, ct);
    }

    // ── Cancel (Item 2 — explicit task cancellation) ───────────────────────────

    public async Task CancelAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;

        var (inst, _) = entry;
        inst.Status      = WorkflowStatus.Cancelled;
        inst.IsPaused    = false;
        inst.CompletedAt = DateTime.UtcNow;

        // Cancel every submitted task — both queued-but-not-started (Queued in orchestrator)
        // and actively running ones. AgentOrchestrator.CancelTaskAsync handles both states.
        var submittedSteps = inst.StepExecutions.Values
            .Where(s => s.TaskId is not null
                     && s.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Pending)
            .ToList();

        _logger.LogInformation(
            "Workflow '{Name}' cancelled (instance {Id}) — cancelling {N} active task(s): {TaskIds}",
            inst.DefinitionName, instanceId, submittedSteps.Count,
            string.Join(", ", submittedSteps.Select(s => s.TaskId is { } tid ? tid[..Math.Min(8, tid.Length)] : "?")));

        foreach (var stepExec in submittedSteps)
            await _orchestrator.CancelTaskAsync(stepExec.TaskId!, ct);

        // Skip all steps that haven't started yet; resolve approval gates
        foreach (var stepExec in inst.StepExecutions.Values
                     .Where(s => s.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.WaitingForApproval))
            RecordAudit(stepExec, WorkflowStepStatus.Skipped, "Workflow cancelled");

        await PersistInstanceAsync(inst);
        BroadcastUpdate(inst);
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    public List<WorkflowDefinition> GetAvailableDefinitions(string? workspacePath = null)
    {
        var defs = _loader.GetBuiltInDefinitions();
        if (!string.IsNullOrEmpty(workspacePath))
        {
            try { defs.AddRange(_loader.LoadFromWorkspace(workspacePath)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workspace workflows from '{Path}'", workspacePath);
            }
        }
        return defs;
    }

    public WorkflowInstance? GetInstance(string instanceId)
        => _active.TryGetValue(instanceId, out var e) ? e.Inst : null;

    public List<WorkflowInstance> GetAllInstances()
        => _active.Values.Select(e => e.Inst).ToList();

    /// <summary>Number of workflow instances currently in memory (Running or Paused).</summary>
    public int ActiveInstanceCount => _active.Count;

    // ── Private: DAG evaluation ────────────────────────────────────────────────

    private async Task EvaluateNextStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, string completedStepId)
    {
        var completedStep = def.Steps.FirstOrDefault(s => s.Id == completedStepId);
        if (completedStep is null) return;

        var completedExec = inst.StepExecutions[completedStepId];

        // 1. Handle feedback loop: step has a Next back-edge and found issues
        if (completedStep.Next is not null
            && completedExec.Status == WorkflowStepStatus.Completed
            && completedExec.IssueCount > 0)
        {
            var loopTargetDef = def.Steps.FirstOrDefault(s => s.Id == completedStep.Next);
            if (loopTargetDef is not null)
            {
                var loopTargetExec = inst.StepExecutions[loopTargetDef.Id];
                var agentType      = WorkflowDefinitionLoader.MapAgentName(loopTargetDef.Agent ?? loopTargetDef.Id);
                var policy         = def.ConvergencePolicy;

                var yamlMax      = completedStep.MaxIterations;
                var globalMax    = _agentLimitsConfig.GetMaxIterations(agentType);
                var effectiveMax = Math.Min(yamlMax, globalMax);
                var escalationTarget = (policy?.EscalationTarget ?? "CANCEL").ToUpperInvariant();

                // ── Early-escalation checks (run before iteration cap) ─────

                // timeout_per_iteration_sec: wall-clock elapsed since the loop target last started
                if (policy?.TimeoutPerIterationSec > 0 && loopTargetExec.StartedAt.HasValue)
                {
                    var elapsed = (DateTime.UtcNow - loopTargetExec.StartedAt.Value).TotalSeconds;
                    if (elapsed > policy.TimeoutPerIterationSec)
                    {
                        var msg = $"Iteration {loopTargetExec.Iteration} exceeded the " +
                            $"{policy.TimeoutPerIterationSec}s per-iteration timeout ({elapsed:F0}s elapsed).";
                        _logger.LogWarning(
                            "Workflow {Id} step '{Target}' per-iteration timeout — escalating",
                            inst.InstanceId, loopTargetDef.Id);
                        await EscalateLoopAsync(inst, def, loopTargetDef, loopTargetExec,
                            agentType, msg, escalationTarget);
                        return;
                    }
                }

                // contradiction_detection: issues not decreasing → mutually exclusive constraints
                if (policy?.ContradictionDetection == true
                    && loopTargetExec.Iteration > 1
                    && completedExec.IssueCount > 0
                    && completedExec.IssueCount >= loopTargetExec.PreviousIssueCount)
                {
                    var msg = $"Contradiction detected at iteration {loopTargetExec.Iteration}: " +
                        $"{completedExec.IssueCount} issue(s) ≥ prior {loopTargetExec.PreviousIssueCount} — " +
                        "constraints may be mutually exclusive.";
                    _logger.LogWarning(
                        "Workflow {Id} step '{Target}' contradiction detected — escalating to HUMAN_APPROVAL",
                        inst.InstanceId, loopTargetDef.Id);
                    // Spec contradiction always escalates to HUMAN_APPROVAL regardless of escalation_target
                    await EscalateLoopAsync(inst, def, loopTargetDef, loopTargetExec,
                        agentType, msg, "HUMAN_APPROVAL");
                    return;
                }

                // ── Normal iteration cap check ──────────────────────────────────

                if (loopTargetExec.Iteration < effectiveMax)
                {
                    // Save current issue count before advancing so the next cycle can detect contradiction
                    loopTargetExec.PreviousIssueCount = completedExec.IssueCount;

                    loopTargetExec.Iteration++;
                    loopTargetExec.Status = WorkflowStepStatus.Pending;
                    loopTargetExec.Output = null;
                    loopTargetExec.TaskId = null;

                    _logger.LogInformation(
                        "Workflow {Id} feedback loop: re-running '{Target}' (iteration {N}/{Max}; global cap {Global})",
                        inst.InstanceId, loopTargetDef.Id,
                        loopTargetExec.Iteration, yamlMax, globalMax);

                    // partial_retry_scope: reset additional steps depending on scope
                    var scope = (policy?.PartialRetryScope ?? "FAILING_NODES_ONLY").ToUpperInvariant();
                    if (scope == "FULL_WORKFLOW")
                        ResetAllStepsForNewIteration(loopTargetDef.Id, inst, def);
                    else if (scope == "FROM_CODEGEN")
                        ResetLoopBodyForNewIteration(loopTargetDef.Id, inst, def);
                    // FAILING_NODES_ONLY: only the loop target was reset above (default behavior)

                    // ConvergenceHintMemory — inject causal context from the prior iteration
                    if (policy?.ConvergenceHintMemory == true)
                        InjectConvergenceHints(completedStep, completedExec, loopTargetExec, inst);

                    await SubmitStepAsync(loopTargetDef, inst, def, CancellationToken.None);
                    return; // wait for loop target to complete before advancing downstream
                }
                else
                {
                    var capMessage =
                        $"Max iterations reached ({effectiveMax}): step '{loopTargetDef.Id}' " +
                        $"exceeded the configured limit (YAML: {yamlMax}, global: {globalMax}).";
                    _logger.LogWarning(
                        "Workflow {Id} step '{Target}' hit max iterations (YAML: {Yaml}, Global: {Global}) — escalating",
                        inst.InstanceId, loopTargetDef.Id, yamlMax, globalMax);
                    await EscalateLoopAsync(inst, def, loopTargetDef, loopTargetExec,
                        agentType, capMessage, escalationTarget);
                    return;
                }
            }
        }

        // Don't submit new steps while paused (Item 5)
        if (inst.IsPaused) return;

        await SubmitReadyStepsAsync(inst, def, CancellationToken.None, completedStepId);
    }

    /// <summary>
    /// Evaluate routers and constraint steps (synchronous), then submit all ready
    /// agent and tool steps (async). Loops until no more synchronous steps are ready
    /// so that chains of constraints/routers resolve in a single call.
    /// <para>
    /// When <paramref name="triggerStepId"/> is provided, the initial candidate set is limited
    /// to the direct successors of that step (P1 — avoids O(n) scan per completion event).
    /// The candidate set is expanded as synchronous steps complete, so cascades are handled
    /// correctly. When null (startup, resume, recovery) all steps are considered.
    /// </para>
    /// </summary>
    private async Task SubmitReadyStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct,
        string? triggerStepId = null)
    {
        // Build initial candidate set: successors of the trigger step (if known) or all steps
        HashSet<string>? candidates = null;
        if (triggerStepId is not null
            && _revDepsCache.TryGetValue(inst.InstanceId, out var revDeps)
            && revDeps.TryGetValue(triggerStepId, out var directSuccessors))
        {
            candidates = new HashSet<string>(directSuccessors, StringComparer.Ordinal);
        }

        // ── Pass 1: drain synchronous steps (routers + constraints) ───────────
        // Loop until no more synchronous steps become ready; this handles chains
        // where a constraint's completion immediately unlocks another constraint.
        bool anySync;
        do
        {
            anySync = false;

            // Routers
            var readyRouters = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "router"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var router in readyRouters)
            {
                RecordAudit(inst.StepExecutions[router.Id], WorkflowStepStatus.Completed, "Router evaluated");
                var targetId = EvaluateRouter(router, inst);
                if (targetId is not null)
                {
                    var targetDef = def.Steps.FirstOrDefault(s => s.Id == targetId);
                    if (targetDef is not null && inst.StepExecutions[targetId].Status == WorkflowStepStatus.Pending)
                        await SubmitStepAsync(targetDef, inst, def, ct);
                }
                anySync = true;
                ExpandCandidates(router.Id);
            }

            // Constraints (evaluate synchronously, no I/O)
            var readyConstraints = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "constraint"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var constraint in readyConstraints)
            {
                ExecuteConstraintStep(constraint, inst, def);
                anySync = true;
                ExpandCandidates(constraint.Id);
            }

            // Context retrieval (synchronous aggregation, no I/O)
            var readyCtxRetrieval = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "context_retrieval"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var ctxStep in readyCtxRetrieval)
            {
                ExecuteContextRetrievalStep(ctxStep, inst);
                anySync = true;
                ExpandCandidates(ctxStep.Id);
            }

            // workspace_provision — creates isolated shadow worktree ()
            var readyProvisions = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "workspace_provision"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();
            foreach (var ps in readyProvisions)
            {
                await ExecuteWorkspaceProvisionStepAsync(ps, inst, def, ct);
                anySync = true;
                ExpandCandidates(ps.Id);
            }

            // workspace_teardown — promotes or destroys shadow worktree ()
            var readyTeardowns = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
                .Where(s => s.Type == "workspace_teardown"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();
            foreach (var ts in readyTeardowns)
            {
                await ExecuteWorkspaceTeardownStepAsync(ts, inst, def, ct);
                anySync = true;
                ExpandCandidates(ts.Id);
            }
        }
        while (anySync);

        // ── Pass 1b: activate human_approval gates ───────────────────────────
        // When all dependencies are complete the gate transitions to WaitingForApproval
        // and the workflow is effectively paused for that branch until approved/rejected.
        var readyApprovals = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "human_approval"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        foreach (var approvalStep in readyApprovals)
        {
            var stepExec = inst.StepExecutions[approvalStep.Id];
            var prompt   = approvalStep.ApprovalPrompt is not null
                ? PromptTemplate.RenderWorkflowStep(approvalStep.ApprovalPrompt, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars)
                : $"Please review the workflow '{inst.DefinitionName}' and approve or reject step '{approvalStep.Id}'.";

            stepExec.StartedAt = DateTime.UtcNow;
            stepExec.Output    = prompt;  // prompt visible in the UI
            RecordAudit(stepExec, WorkflowStepStatus.WaitingForApproval, "Awaiting human decision");

            _logger.LogInformation(
                "Workflow {Id} step '{StepId}' is waiting for human approval", inst.InstanceId, approvalStep.Id);

            // Schedule SLA timeout if configured; persist deadline so the timer survives restart (C2)
            if (approvalStep.SlaHours > 0)
            {
                var slaDelay = TimeSpan.FromHours(approvalStep.SlaHours);
                stepExec.SlaDeadline = DateTime.UtcNow.Add(slaDelay);
                ScheduleApprovalTimeout(inst.InstanceId, approvalStep.Id, slaDelay, approvalStep.TimeoutAction, ct);
            }

            await PersistInstanceAsync(inst);
            OnWorkflowUpdate?.Invoke(inst);
            OnApprovalNeeded?.Invoke(inst.InstanceId, approvalStep.Id, prompt);
        }

        // ── Pass 2: submit async steps (tools + agents) ───────────────────────

        var readyTools = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "tool"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        foreach (var tool in readyTools)
            ExecuteToolStepInBackground(tool, inst, def);

        var readyAgents = (candidates is null ? def.Steps : def.Steps.Where(s => candidates.Contains(s.Id)))
            .Where(s => s.Type == "agent"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        if (readyAgents.Count > 0)
            await Task.WhenAll(readyAgents.Select(s => SubmitStepAsync(s, inst, def, ct)));

        void ExpandCandidates(string completedId)
        {
            if (candidates is null || !_revDepsCache.TryGetValue(inst.InstanceId, out var rv)) return;
            if (rv.TryGetValue(completedId, out var successors))
                foreach (var s in successors) candidates.Add(s);
        }
    }

    private async Task SubmitStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        if (inst.StepExecutions[stepDef.Id].Status == WorkflowStepStatus.Running)
            return; // already submitted

        // Item 3: Policy check before submitting
        var policyResult = _policyEngine.Check(stepDef, inst);
        if (!policyResult.IsAllowed)
        {
            var stepExec = inst.StepExecutions[stepDef.Id];
            stepExec.Error = $"[Policy] {policyResult.DenyReason}";
            RecordAudit(stepExec, WorkflowStepStatus.Failed, stepExec.Error);
            SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} step '{StepId}' blocked by policy: {Reason}",
                inst.InstanceId, stepDef.Id, policyResult.DenyReason);
            // Persist updated state and notify the UI so the blocked step is visible immediately.
            await PersistInstanceAsync(inst);
            OnWorkflowUpdate?.Invoke(inst);
            return;
        }

        // Resolve prompt template
        var basePrompt    = stepDef.Prompt ?? $"Process the following with a {stepDef.Agent ?? stepDef.Id} agent.";
        var resolvedPrompt = PromptTemplate.RenderWorkflowStep(basePrompt, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);

        // Model resolution chain:
        //   1. YAML-baked step model (author explicit override — highest priority)
        //   2. Launch-time per-step override (user chose at workflow launch)
        //   3. Instance default model (the "global" pick at launch)
        //   4. TaskAffinities config (lowest — configured default per agent type)
        var agentType = WorkflowDefinitionLoader.MapAgentName(stepDef.Agent ?? stepDef.Id);

        // Start from YAML values (may be null/empty — filled by lower-priority tiers below)
        var modelProvider = stepDef.ModelProvider;
        var modelId       = stepDef.ModelId;
        string? modelEndpoint = null;

        // Tier 2: launch-time step override (only applied when YAML didn't lock the model)
        if (string.IsNullOrEmpty(modelId) &&
            inst.StepModelOverrides.TryGetValue(stepDef.Id, out var stepOverride) &&
            !string.IsNullOrEmpty(stepOverride.ModelId))
        {
            modelProvider = stepOverride.Provider;
            modelId       = stepOverride.ModelId;
            modelEndpoint = stepOverride.Endpoint;
        }

        // Tier 3: instance default
        if (string.IsNullOrEmpty(modelProvider)) modelProvider = inst.DefaultModelProvider;
        if (string.IsNullOrEmpty(modelId))       modelId       = inst.DefaultModelId;

        // Tier 4: TaskAffinities
        if (string.IsNullOrEmpty(modelProvider) || string.IsNullOrEmpty(modelId))
        {
            var (affinityProvider, affinityModel) = _taskAffinitiesConfig.GetDefaultFor(agentType);
            if (string.IsNullOrEmpty(modelProvider)) modelProvider = affinityProvider;
            if (string.IsNullOrEmpty(modelId))       modelId       = affinityModel;

            _logger.LogDebug(
                "Workflow {Id} step '{StepId}': no model specified, using affinity → {Provider}/{Model}",
                inst.InstanceId, stepDef.Id, modelProvider, modelId);
        }

        if (!Enum.TryParse<ModelProvider>(modelProvider, ignoreCase: true, out var mp))
            mp = ModelProvider.Claude;

        var task = new AgentTask
        {
            AgentType     = agentType,
            ModelProvider = mp,
            ModelId       = modelId,
            Description   = resolvedPrompt,
            FilePaths     = inst.FilePaths,
            Metadata      = new Dictionary<string, string>
            {
                ["workflowInstanceId"] = inst.InstanceId,
                ["workflowStepId"]     = stepDef.Id,
                ["workflowStepLabel"]  = stepDef.Id,
            }
        };

        // Endpoint priority: step override endpoint → instance endpoint
        var effectiveEndpoint = modelEndpoint ?? inst.ModelEndpoint;
        if (!string.IsNullOrEmpty(effectiveEndpoint))
            task.Metadata["modelEndpoint"] = effectiveEndpoint;

        var taskId = await _orchestrator.SubmitTaskAsync(task, ct);

        // Register reverse lookup
        _taskToStep[taskId] = (inst.InstanceId, stepDef.Id);

        // Update step execution state
        var exec     = inst.StepExecutions[stepDef.Id];
        exec.TaskId    = taskId;
        exec.StartedAt = DateTime.UtcNow;
        RecordAudit(exec, WorkflowStepStatus.Running, $"Submitted as task {taskId[..Math.Min(8, taskId.Length)]}");

        _logger.LogInformation(
            "Workflow {InstanceId} submitted step '{StepId}' as task {TaskId} ({Agent} via {Provider}/{Model})",
            inst.InstanceId, stepDef.Id, taskId[..Math.Min(8, taskId.Length)],
            agentType, mp, modelId);
    }

    // ── Router evaluation ──────────────────────────────────────────────────────

    private string? EvaluateRouter(WorkflowStepDef routerStep, WorkflowInstance inst)
    {
        if (routerStep.Router is null) return null;

        var depExecs = routerStep.DependsOn
            .Select(id => inst.StepExecutions.GetValueOrDefault(id))
            .Where(e => e is not null)
            .Cast<WorkflowStepExecution>()
            .ToList();

        // Use dep with the highest issue count (most relevant for routing decisions)
        var primaryDep = depExecs.OrderByDescending(e => e.IssueCount).FirstOrDefault()
                      ?? depExecs.FirstOrDefault();

        if (primaryDep is null) return null;

        foreach (var branch in routerStep.Router.Branches)
        {
            if (EvaluateCondition(branch.Condition, primaryDep))
            {
                _logger.LogDebug(
                    "Router '{RouterId}' matched condition '{Cond}' → '{Target}'",
                    routerStep.Id, branch.Condition, branch.Target);
                return branch.Target;
            }
        }

        _logger.LogWarning("Router '{RouterId}' had no matching condition — skipping downstream", routerStep.Id);
        return null;
    }

    private static bool EvaluateCondition(string condition, WorkflowStepExecution dep)
    {
        var c = condition.Trim().ToLowerInvariant();
        return c switch
        {
            "hasissues" or "has_issues"    => dep.IssueCount > 0,
            "success"   or "approved"      => dep.Status == WorkflowStepStatus.Completed && dep.IssueCount == 0,
            "failed"    or "error"         => dep.Status == WorkflowStepStatus.Failed,
            _ when c.StartsWith("output.contains(") => EvaluateContains(c, dep.Output ?? ""),
            _ => false,
        };
    }

    private static bool EvaluateContains(string condition, string output)
    {
        var start = condition.IndexOf('(') + 1;
        var end   = condition.LastIndexOf(')');
        if (start >= end) return false;
        var arg = condition[start..end].Trim('\'', '"', ' ');
        return output.Contains(arg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Constraint step execution ──────────────────────────────────────────────

    private void ExecuteConstraintStep(WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        RecordAudit(exec, WorkflowStepStatus.Running);
        exec.StartedAt = DateTime.UtcNow;

        var (passed, reason) = EvaluateConstraintExpr(stepDef.ConstraintExpr ?? "", inst);
        exec.Output      = reason;
        exec.CompletedAt = DateTime.UtcNow;

        if (passed)
        {
            RecordAudit(exec, WorkflowStepStatus.Completed, reason);
            _logger.LogInformation(
                "Workflow {Id} constraint '{StepId}' passed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else if (stepDef.OnConstraintFail.Equals("warn", StringComparison.OrdinalIgnoreCase))
        {
            exec.IssueCount = 1;
            RecordAudit(exec, WorkflowStepStatus.Completed, $"warn: {reason}");
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed (warn): {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else
        {
            exec.Error = $"Constraint failed: {reason}";
            RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
    }

    // ── Context-retrieval step execution ──────────────────────────────────────

    /// <summary>
    /// ContextRetrievalNode: aggregates outputs from source_steps and injects
    /// the concatenated text into inst.InputContext[context_var_name].
    /// Runs synchronously in the constraint drain loop — no I/O required.
    /// Downstream prompts can reference the result via {{context_var_name}}.
    /// </summary>
    private void ExecuteContextRetrievalStep(WorkflowStepDef stepDef, WorkflowInstance inst)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        RecordAudit(exec, WorkflowStepStatus.Running);
        exec.StartedAt = DateTime.UtcNow;

        var sb = new System.Text.StringBuilder();
        var found = new List<string>();
        var missing = new List<string>();

        foreach (var srcId in stepDef.SourceSteps)
        {
            if (inst.StepExecutions.TryGetValue(srcId, out var srcExec) &&
                !string.IsNullOrEmpty(srcExec.Output))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"=== {srcId} ===");
                sb.Append(srcExec.Output);
                found.Add(srcId);
            }
            else
            {
                missing.Add(srcId);
            }
        }

        var varName  = stepDef.ContextVarName!;
        var combined = sb.ToString();

        inst.InputContext[varName] = combined;
        exec.Output      = combined;
        exec.CompletedAt = DateTime.UtcNow;

        var summary = $"context_retrieval: '{varName}' populated from [{string.Join(", ", found)}]" +
            (missing.Count > 0 ? $"; missing/empty: [{string.Join(", ", missing)}]" : "");

        RecordAudit(exec, WorkflowStepStatus.Completed, summary);
        _logger.LogInformation("Workflow {Id} context_retrieval '{StepId}': {Summary}",
            inst.InstanceId, stepDef.Id, summary);
    }

    // ── Shadow workspace step handlers () ─────────────────────────────────

    private async Task ExecuteWorkspaceProvisionStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        RecordAudit(exec, WorkflowStepStatus.Running);

        try
        {
            if (string.IsNullOrEmpty(inst.WorkspacePath) || !_gitService.IsGitRepo(inst.WorkspacePath))
            {
                exec.Output      = "Shadow workspace skipped: not a git repository.";
                exec.CompletedAt = DateTime.UtcNow;
                RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                _logger.LogWarning("Workflow {Id} workspace_provision skipped: {WorkspacePath} is not a git repo",
                    inst.InstanceId, inst.WorkspacePath ?? "(none)");
                await PersistInstanceAsync(inst);
                BroadcastUpdate(inst);
                return;
            }

            var shadowPath = await _gitService.ProvisionShadowAsync(
                inst.WorkspacePath, inst.InstanceId, stepDef.ShadowBranch, ct);

            if (shadowPath is null)
            {
                exec.Output      = "Shadow workspace skipped: git not available.";
                exec.CompletedAt = DateTime.UtcNow;
                RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                await PersistInstanceAsync(inst);
                BroadcastUpdate(inst);
                return;
            }

            inst.ShadowWorkspacePath = shadowPath;
            inst.InputContext["shadow_path"] = shadowPath;

            exec.Output      = $"Shadow worktree provisioned at: {shadowPath}";
            exec.CompletedAt = DateTime.UtcNow;
            RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
            _logger.LogInformation("Workflow {Id} shadow workspace provisioned: {Path}", inst.InstanceId, shadowPath);

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        catch (Exception ex)
        {
            exec.Error       = $"workspace_provision failed: {ex.Message}";
            exec.CompletedAt = DateTime.UtcNow;
            RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            _logger.LogError(ex, "Workflow {Id} workspace_provision threw", inst.InstanceId);
            SkipDownstream(stepDef.Id, inst, def);
            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
    }

    private async Task ExecuteWorkspaceTeardownStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        RecordAudit(exec, WorkflowStepStatus.Running);

        try
        {
            if (string.IsNullOrEmpty(inst.ShadowWorkspacePath))
            {
                exec.Output      = "No shadow workspace to tear down.";
                exec.CompletedAt = DateTime.UtcNow;
                RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
                await PersistInstanceAsync(inst);
                BroadcastUpdate(inst);
                return;
            }

            var shadowPath = inst.ShadowWorkspacePath;

            if (stepDef.ShadowAction == "promote")
            {
                var (success, summary) = await _gitService.PromoteShadowAsync(
                    inst.WorkspacePath!, shadowPath, ct);

                if (!success)
                {
                    exec.Error       = $"workspace_teardown promote failed: {summary}";
                    exec.CompletedAt = DateTime.UtcNow;
                    RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
                    _logger.LogError("Workflow {Id} workspace_teardown promote failed: {Err}", inst.InstanceId, summary);
                    SkipDownstream(stepDef.Id, inst, def);
                    inst.ShadowWorkspacePath = null;
                    inst.InputContext.Remove("shadow_path");
                    await PersistInstanceAsync(inst);
                    BroadcastUpdate(inst);
                    return;
                }

                exec.Output      = $"Shadow changes promoted to workspace.\n{summary}";
            }
            else
            {
                // destroy
                await _gitService.DestroyShadowAsync(inst.WorkspacePath ?? "", shadowPath, ct);
                exec.Output = "Shadow workspace destroyed.";
            }

            inst.ShadowWorkspacePath = null;
            inst.InputContext.Remove("shadow_path");
            exec.CompletedAt = DateTime.UtcNow;
            RecordAudit(exec, WorkflowStepStatus.Completed, exec.Output);
            _logger.LogInformation("Workflow {Id} workspace_teardown completed (action: {Action})",
                inst.InstanceId, stepDef.ShadowAction);

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        catch (Exception ex)
        {
            exec.Error       = $"workspace_teardown failed: {ex.Message}";
            exec.CompletedAt = DateTime.UtcNow;
            RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
            _logger.LogError(ex, "Workflow {Id} workspace_teardown threw", inst.InstanceId);
            inst.ShadowWorkspacePath = null;
            inst.InputContext.Remove("shadow_path");
            SkipDownstream(stepDef.Id, inst, def);
            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
    }

    private (bool Passed, string Reason) EvaluateConstraintExpr(string expr, WorkflowInstance inst)
    {
        expr = expr.Trim();

        // exit_code(step_id) OP N  — supports ==, !=, >=, <=, >, <
        var m = Regex.Match(expr, @"exit_code\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = int.Parse(m.Groups[3].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e) && e.ExitCode.HasValue)
                return (CompareInts(e.ExitCode.Value, op, expected),
                    $"exit_code({stepId}) {op} {expected} — actual={e.ExitCode.Value}");
            return (false, $"Step '{stepId}' has no exit code recorded.");
        }

        // output(step_id).contains('text')
        m = Regex.Match(expr, @"output\((\w+)\)\.contains\('(.+?)'\)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId = m.Groups[1].Value;
            var text   = m.Groups[2].Value;
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var contains = (e.Output ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);
                return (contains, $"output({stepId}).contains('{text}') = {contains}");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // issue_count(step_id) OP N  — supports ==, !=, >=, <=, >, <
        m = Regex.Match(expr, @"issue_count\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = int.Parse(m.Groups[3].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
                return (CompareInts(e.IssueCount, op, expected),
                    $"issue_count({stepId}) {op} {expected} — actual={e.IssueCount}");
            return (false, $"Step '{stepId}' not found.");
        }

        // output_value(step_id) OP N  — extracts first numeric value from output text
        // Enables TEST_COVERAGE (>= 0.85), PERFORMANCE (< 2.0) constraint types.
        m = Regex.Match(expr, @"output_value\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var numMatch = Regex.Match(e.Output ?? "", @"-?\d+(?:\.\d+)?");
                if (numMatch.Success &&
                    double.TryParse(numMatch.Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var actual))
                    return (CompareDoubles(actual, op, expected),
                        $"output_value({stepId}) {op} {expected} — actual={actual}");
                return (false, $"Step '{stepId}' output contains no numeric value.");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // output_value(step_id, 'metric_name') OP N — named metric extraction.
        // Searches step output for "metric_name: value" or "metric_name=value".
        // Enables labelled numeric metrics (e.g. output_value(scan, 'high_cves') == 0).
        m = Regex.Match(expr,
            @"output_value\((\w+),\s*'([^']+)'\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId     = m.Groups[1].Value;
            var metricName = m.Groups[2].Value;
            var op         = m.Groups[3].Value;
            var expected   = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                // Match "metric_name: 3.14" or "metric_name=3.14" (case-insensitive)
                var pattern  = Regex.Escape(metricName) + @"[:\s=]+(-?\d+(?:\.\d+)?)";
                var numMatch = Regex.Match(e.Output ?? "", pattern, RegexOptions.IgnoreCase);
                if (numMatch.Success &&
                    double.TryParse(numMatch.Groups[1].Value, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var actual))
                    return (CompareDoubles(actual, op, expected),
                        $"output_value({stepId}, '{metricName}') {op} {expected} — actual={actual}");
                return (false, $"Step '{stepId}' output does not contain metric '{metricName}'.");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // delta_issues(current_step, baseline_step) OP N — SECURITY_REGRESSION constraint type.
        // Computes issue_count(current) - issue_count(baseline) and compares against threshold.
        m = Regex.Match(expr,
            @"delta_issues\((\w+),\s*(\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var currentId  = m.Groups[1].Value;
            var baselineId = m.Groups[2].Value;
            var op         = m.Groups[3].Value;
            var expected   = int.Parse(m.Groups[4].Value);
            var hasCurrentStep  = inst.StepExecutions.TryGetValue(currentId, out var current);
            var hasBaselineStep = inst.StepExecutions.TryGetValue(baselineId, out var baseline);
            if (!hasCurrentStep)  return (false, $"Step '{currentId}' not found.");
            if (!hasBaselineStep) return (false, $"Step '{baselineId}' not found.");
            var delta = current!.IssueCount - baseline!.IssueCount;
            return (CompareInts(delta, op, expected),
                $"delta_issues({currentId}, {baselineId}) {op} {expected} — delta={delta} ({current.IssueCount}-{baseline.IssueCount})");
        }

        // confidence(step_id) OP N — REVIEW_CONFIDENCE constraint type.
        // Reads IntentPackage.Confidence (0.0–1.0) produced by the agent step.
        m = Regex.Match(expr,
            @"confidence\((\w+)\)\s*(==|!=|>=|<=|>|<)\s*(-?\d+(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var op       = m.Groups[2].Value;
            var expected = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var conf = e.IntentPackage?.Confidence;
                if (conf.HasValue)
                    return (CompareDoubles(conf.Value, op, expected),
                        $"confidence({stepId}) {op} {expected} — actual={conf.Value:F3}");
                return (false, $"Step '{stepId}' has no IntentPackage.Confidence (agent step required).");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        return (false, $"Unrecognized constraint expression: '{expr}'");
    }

    private static bool CompareInts(int actual, string op, int expected) => op switch
    {
        "==" => actual == expected,
        "!=" => actual != expected,
        ">=" => actual >= expected,
        "<=" => actual <= expected,
        ">"  => actual >  expected,
        "<"  => actual <  expected,
        _    => false,
    };

    private static bool CompareDoubles(double actual, string op, double expected) => op switch
    {
        "==" => Math.Abs(actual - expected) < 1e-9,
        "!=" => Math.Abs(actual - expected) >= 1e-9,
        ">=" => actual >= expected,
        "<=" => actual <= expected,
        ">"  => actual >  expected,
        "<"  => actual <  expected,
        _    => false,
    };

    // ── Tool step execution ────────────────────────────────────────────────────

    /// <summary>
    /// Marks the step Running immediately (under whatever lock the caller holds),
    /// then fires a background task that runs the process and re-acquires the lock
    /// to update state and advance the DAG when done.
    /// </summary>
    private void ExecuteToolStepInBackground(WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.StartedAt = DateTime.UtcNow;
        RecordAudit(exec, WorkflowStepStatus.Running, $"Command: {stepDef.Command}");

        _logger.LogInformation(
            "Workflow {InstanceId} starting tool step '{StepId}': {Command}",
            inst.InstanceId, stepDef.Id, stepDef.Command);

        BroadcastUpdate(inst);

        // Capture state needed by the background task
        var instanceId = inst.InstanceId;
        var command    = PromptTemplate.RenderWorkflowStep(
            stepDef.Command ?? "", inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);
        var workingDir = string.IsNullOrWhiteSpace(stepDef.WorkingDir)
            ? inst.WorkspacePath ?? Directory.GetCurrentDirectory()
            : PromptTemplate.RenderWorkflowStep(stepDef.WorkingDir, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars);
        var policy     = stepDef.ExitCodePolicy.ToUpperInvariant();
        var timeoutSec = stepDef.TimeoutSec;

        _ = Task.Run(async () =>
        {
            string output;
            int    exitCode;
            bool   timedOut = false;

            using var toolCts = timeoutSec > 0
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
                : new CancellationTokenSource();

            try
            {
                (output, exitCode) = await RunProcessAsync(command, workingDir, toolCts.Token);
            }
            catch (OperationCanceledException) when (toolCts.IsCancellationRequested && timeoutSec > 0)
            {
                output   = $"Tool step timed out after {timeoutSec}s.";
                exitCode = -2;
                timedOut = true;
                _logger.LogWarning(
                    "Workflow {Id} tool step '{StepId}' timed out after {Sec}s",
                    instanceId, stepDef.Id, timeoutSec);
            }
            catch (Exception ex)
            {
                output   = ex.Message;
                exitCode = -1;
                _logger.LogError(ex,
                    "Workflow {Id} tool step '{StepId}' threw an exception", instanceId, stepDef.Id);
            }

            if (!_active.TryGetValue(instanceId, out var entry)) return;
            var (liveInst, liveDef) = entry;
            var lk = _locks[instanceId];
            await lk.WaitAsync();
            try
            {
                var liveExec         = liveInst.StepExecutions[stepDef.Id];
                liveExec.ExitCode    = exitCode;
                liveExec.Output      = output;
                liveExec.CompletedAt = DateTime.UtcNow;

                if (timedOut)
                {
                    liveExec.Error = output; // timeout message
                    RecordAudit(liveExec, WorkflowStepStatus.Failed, liveExec.Error);
                    SkipDownstream(stepDef.Id, liveInst, liveDef);
                }
                else if (exitCode == 0 || policy is "IGNORE")
                {
                    RecordAudit(liveExec, WorkflowStepStatus.Completed, $"exit code {exitCode}");
                }
                else if (policy == "WARN_ON_NONZERO")
                {
                    liveExec.IssueCount = 1;
                    RecordAudit(liveExec, WorkflowStepStatus.Completed, $"exit code {exitCode} (WARN_ON_NONZERO)");
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' exited {Code} (WARN_ON_NONZERO)",
                        instanceId, stepDef.Id, exitCode);
                }
                else // FAIL_ON_NONZERO
                {
                    liveExec.Error = $"Command exited with code {exitCode}.";
                    RecordAudit(liveExec, WorkflowStepStatus.Failed, liveExec.Error);
                    SkipDownstream(stepDef.Id, liveInst, liveDef);
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' failed (exit code {Code})",
                        instanceId, stepDef.Id, exitCode);
                }

                await EvaluateNextStepsAsync(liveInst, liveDef, stepDef.Id);

                if (IsInstanceDone(liveInst, liveDef))
                {
                    liveInst.Status = liveInst.StepExecutions.Values.Any(
                        s => s.Status == WorkflowStepStatus.Failed)
                        ? WorkflowStatus.Failed
                        : WorkflowStatus.Completed;
                    liveInst.CompletedAt = DateTime.UtcNow;
                    _logger.LogInformation(
                        "Workflow '{Name}' {Status} (instance {Id})",
                        liveInst.DefinitionName, liveInst.Status, instanceId);
                }

                await PersistInstanceAsync(liveInst);
                BroadcastUpdate(liveInst);
            }
            finally { lk.Release(); }
        });
    }

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string command, string workingDir, CancellationToken ct = default)
    {
        // Split "dotnet build --nologo" into executable + arguments
        var parts = command.Trim().Split(' ', 2);
        var exe   = parts[0];
        var args  = parts.Length > 1 ? parts[1] : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : stdout + "\n--- stderr ---\n" + stderr;

        return (combined.Trim(), proc.ExitCode);
    }

    // ── Helper methods ─────────────────────────────────────────────────────────

    private static void SkipDownstream(string failedStepId, WorkflowInstance inst, WorkflowDefinition def)
    {
        var queue   = new Queue<string>([failedStepId]);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var step in def.Steps.Where(s => s.DependsOn.Contains(id)))
            {
                if (inst.StepExecutions.TryGetValue(step.Id, out var e)
                    && e.Status == WorkflowStepStatus.Pending)
                {
                    RecordAudit(e, WorkflowStepStatus.Skipped, $"Upstream step '{failedStepId}' failed/skipped");
                    queue.Enqueue(step.Id);
                }
            }
        }
    }

    private static bool IsInstanceDone(WorkflowInstance inst, WorkflowDefinition def)
        => def.Steps
              .Where(s => s.Type is not "router")   // routers are structural, not tracked as done
              .All(s => inst.StepExecutions.TryGetValue(s.Id, out var e)
                        && e.Status is WorkflowStepStatus.Completed
                                    or WorkflowStepStatus.Failed
                                    or WorkflowStepStatus.Skipped
                                    or WorkflowStepStatus.Rejected);
              // WaitingForApproval is NOT terminal — the workflow is blocked until the user responds

    private WorkflowDefinition? FindDefinition(string id, string? workspacePath)
    {
        var all = GetAvailableDefinitions(workspacePath);
        return all.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistInstanceAsync(WorkflowInstance inst)
    {
        if (_workflowRepository is null) return;
        try
        {
            // Auto-destroy shadow on terminal failure/cancel (fire-and-forget)
            if (inst.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled
                && !string.IsNullOrEmpty(inst.ShadowWorkspacePath))
            {
                var shadow = inst.ShadowWorkspacePath;
                var wsPath = inst.WorkspacePath ?? "";
                inst.ShadowWorkspacePath = null;
                inst.InputContext.Remove("shadow_path");
                _ = Task.Run(() => _gitService.DestroyShadowAsync(wsPath, shadow, CancellationToken.None));
            }

            await _workflowRepository.SaveWorkflowInstanceAsync(inst);

            // Schedule deferred in-memory cleanup so the UI can still query
            // the instance for ~30 s after it reaches a terminal state.
            if (inst.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                ScheduleInstanceCleanup(inst.InstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist workflow instance {Id}", inst.InstanceId);
        }
    }

    /// <summary>
    /// Removes a terminal workflow instance from the in-memory active set after a
    /// short grace period (so the UI can still query it post-completion).
    /// The SemaphoreSlim in _locks is NOT disposed here — another thread may have already
    /// retrieved a reference to it via GetOrAdd and be about to call WaitAsync.
    /// SemaphoreSlim has no unmanaged resources when AvailableWaitHandle is never accessed
    /// (we only use WaitAsync), so GC handles reclamation once references drop to zero.
    /// </summary>
    private void ScheduleInstanceCleanup(string instanceId)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            // Remove _active first: any thread that gets the semaphore after this point
            // will enter EvaluateDagAsync, find no active entry, and return immediately.
            // Also purge any remaining _taskToStep entries for this instance's steps.
            if (_active.TryRemove(instanceId, out var removed))
            {
                foreach (var exec in removed.Inst.StepExecutions.Values)
                    if (exec.TaskId is not null)
                        _taskToStep.TryRemove(exec.TaskId, out _);
            }
            // Remove the semaphore entry to bound _locks size, but do NOT dispose it.
            _locks.TryRemove(instanceId, out _);
            _revDepsCache.TryRemove(instanceId, out _);
            _logger.LogDebug("Cleaned up completed instance {Id} from active set", instanceId);
        });
    }

    private void BroadcastUpdate(WorkflowInstance inst) => OnWorkflowUpdate?.Invoke(inst);

    /// <summary>
    /// Builds a reverse adjacency map for a workflow definition.
    /// Returns: dependencyStepId → list of stepIds that list it in their DependsOn.
    /// Used by SubmitReadyStepsAsync to limit DAG evaluation to the successors of a
    /// just-completed step rather than scanning all steps (P1 — avoids O(n) scan per event).
    /// </summary>
    private static Dictionary<string, List<string>> BuildReverseDeps(WorkflowDefinition def)
    {
        var revDeps = new Dictionary<string, List<string>>(def.Steps.Count, StringComparer.Ordinal);
        foreach (var step in def.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                if (!revDeps.TryGetValue(dep, out var list))
                    revDeps[dep] = list = new List<string>(4);
                list.Add(step.Id);
            }
        }
        return revDeps;
    }

    // ── Audit helper (BaseNode contract) ─────────────────────────────────

    /// <summary>
    /// Records a state transition in the step's AuditLog and updates Status atomically.
    /// Call this instead of setting exec.Status directly.
    /// </summary>
    private static void RecordAudit(
        WorkflowStepExecution exec, WorkflowStepStatus newStatus, string? reason = null)
    {
        exec.AuditLog.Add(new AuditEntry
        {
            FromStatus = exec.Status,
            ToStatus   = newStatus,
            Reason     = reason,
        });
        exec.Status = newStatus;
    }

    // ── Convergence loop helpers ───────────────────────────────────────────────

    /// <summary>
    /// Shared escalation handler for convergence loop failures ().
    /// Handles HUMAN_APPROVAL, DLQ, and CANCEL escalation targets.
    /// </summary>
    private async Task EscalateLoopAsync(
        WorkflowInstance inst, WorkflowDefinition def,
        WorkflowStepDef loopTargetDef, WorkflowStepExecution loopTargetExec,
        AgentType agentType, string reason, string escalationTarget)
    {
        switch (escalationTarget)
        {
            case "HUMAN_APPROVAL":
                inst.IsPaused = true;
                inst.Status   = WorkflowStatus.Paused;
                loopTargetExec.Output = reason + " Human approval required to continue or abort.";
                RecordAudit(loopTargetExec, WorkflowStepStatus.WaitingForApproval, reason);
                _logger.LogInformation(
                    "Workflow {Id} paused for human approval — reason: {Reason}",
                    inst.InstanceId, reason);
                OnApprovalNeeded?.Invoke(
                    inst.InstanceId, loopTargetDef.Id,
                    reason + " Please decide whether to continue or cancel the workflow.");
                break;

            case "DLQ":
                loopTargetExec.Error = reason + " Escalated to DLQ.";
                RecordAudit(loopTargetExec, WorkflowStepStatus.Failed, loopTargetExec.Error);
                inst.Status      = WorkflowStatus.Failed;
                inst.CompletedAt = DateTime.UtcNow;
                SkipDownstream(loopTargetDef.Id, inst, def);
                break;

            default: // CANCEL
                loopTargetExec.Error = reason +
                    $" Increase AgentLimits:{agentType}:MaxIterations " +
                    "or the step's max_iterations to allow more iterations.";
                RecordAudit(loopTargetExec, WorkflowStepStatus.Failed, loopTargetExec.Error);
                inst.Status      = WorkflowStatus.Failed;
                inst.CompletedAt = DateTime.UtcNow;
                SkipDownstream(loopTargetDef.Id, inst, def);
                break;
        }

        await PersistInstanceAsync(inst);
        BroadcastUpdate(inst);
    }

    /// <summary>
    /// partial_retry_scope = FULL_WORKFLOW: reset every non-terminal step to Pending
    /// so the entire workflow re-runs from the root. The loop target itself is excluded
    /// because it was already reset by the caller.
    /// </summary>
    private static void ResetAllStepsForNewIteration(
        string loopTargetId, WorkflowInstance inst, WorkflowDefinition def)
    {
        foreach (var s in def.Steps.Where(s => s.Id != loopTargetId))
        {
            var se = inst.StepExecutions[s.Id];
            if (se.Status is WorkflowStepStatus.WaitingForApproval or WorkflowStepStatus.Rejected)
                continue; // leave active approval gates undisturbed
            se.Status     = WorkflowStepStatus.Pending;
            se.Output     = null;
            se.TaskId     = null;
            se.Error      = null;
            se.ExitCode   = null;
            se.IssueCount = 0;
        }
    }

    /// <summary>
    /// partial_retry_scope = FROM_CODEGEN: reset all steps that are downstream
    /// (transitively dependent) of the loop target — the "loop body" — so the full
    /// loop body re-runs, not just the immediate target.
    /// The loop target itself is excluded because the caller already reset it.
    /// </summary>
    private static void ResetLoopBodyForNewIteration(
        string loopTargetId, WorkflowInstance inst, WorkflowDefinition def)
    {
        foreach (var stepId in GetDescendantStepIds(loopTargetId, def))
        {
            var se = inst.StepExecutions[stepId];
            if (se.Status is WorkflowStepStatus.WaitingForApproval or WorkflowStepStatus.Rejected)
                continue;
            se.Status     = WorkflowStepStatus.Pending;
            se.Output     = null;
            se.TaskId     = null;
            se.Error      = null;
            se.ExitCode   = null;
            se.IssueCount = 0;
        }
    }

    /// <summary>
    /// BFS forward from rootId following DependsOn edges, returning all reachable step IDs
    /// (excluding rootId itself). Used to identify the loop body for FROM_CODEGEN scope.
    /// </summary>
    private static HashSet<string> GetDescendantStepIds(string rootId, WorkflowDefinition def)
    {
        var result = new HashSet<string>();
        var queue  = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var s in def.Steps.Where(s => s.DependsOn.Contains(current)))
            {
                if (result.Add(s.Id))
                    queue.Enqueue(s.Id);
            }
        }
        return result;
    }

    /// <summary>
    /// ConvergenceHintMemory: builds a structured context variable from the prior
    /// iteration's failure data and injects it into the workflow's InputContext as
    /// "convergence_hints". The refactor step's prompt template can reference {{convergence_hints}}.
    /// </summary>
    private static void InjectConvergenceHints(
        WorkflowStepDef validationStep,
        WorkflowStepExecution validationExec,
        WorkflowStepExecution refactorExec,
        WorkflowInstance inst)
    {
        // Collect failure signals from the validation/constraint step that triggered the loop
        var failureLines = new System.Text.StringBuilder();
        failureLines.AppendLine($"[Iteration {refactorExec.Iteration} causal memory]");
        failureLines.AppendLine($"Step '{validationStep.Id}' reported {validationExec.IssueCount} issue(s).");

        if (!string.IsNullOrWhiteSpace(validationExec.Output))
        {
            // Trim to ~800 chars to avoid bloating the prompt
            var trimmed = validationExec.Output.Length > 800
                ? validationExec.Output[..800] + "…"
                : validationExec.Output;
            failureLines.AppendLine($"Constraint output: {trimmed}");
        }

        if (!string.IsNullOrWhiteSpace(validationExec.Error))
            failureLines.AppendLine($"Error: {validationExec.Error}");

        inst.InputContext["convergence_hints"] = failureLines.ToString().Trim();
    }
}
