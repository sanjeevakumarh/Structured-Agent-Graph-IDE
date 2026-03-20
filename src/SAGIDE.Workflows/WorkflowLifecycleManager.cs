using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// Manages the lifecycle of workflow instances: start, pause, resume, cancel,
/// context update, and startup recovery.
/// Extracted from WorkflowEngine.
/// </summary>
public sealed class WorkflowLifecycleManager
{
    private readonly WorkflowInstanceStore _store;
    private readonly WorkflowStepDispatcher _dispatcher;
    private readonly WorkflowDefinitionLoader _loader;
    private readonly IWorkflowRepository? _repository;
    private readonly ITaskSubmissionService _orchestrator;
    private readonly ILogger<WorkflowLifecycleManager> _logger;

    public WorkflowLifecycleManager(
        WorkflowInstanceStore store,
        WorkflowStepDispatcher dispatcher,
        WorkflowDefinitionLoader loader,
        IWorkflowRepository? repository,
        ITaskSubmissionService orchestrator,
        ILogger<WorkflowLifecycleManager> logger)
    {
        _store        = store;
        _dispatcher   = dispatcher;
        _loader       = loader;
        _repository   = repository;
        _orchestrator = orchestrator;
        _logger       = logger;
    }

    // ── Start ──────────────────────────────────────────────────────────────────

    public async Task<WorkflowInstance> StartAsync(StartWorkflowRequest req, CancellationToken ct)
    {
        var def = FindDefinition(req.DefinitionId, req.WorkspacePath);
        if (def is null)
            throw new InvalidOperationException($"Workflow definition '{req.DefinitionId}' not found.");

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

        foreach (var param in def.Parameters)
        {
            if (!inst.InputContext.ContainsKey(param.Name) && param.Default is not null)
                inst.InputContext[param.Name] = param.Default;
        }

        foreach (var step in def.Steps)
            inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };

        _store.Add(inst, def);

        _logger.LogInformation(
            "Workflow '{Name}' started (instance {Id}, {StepCount} steps)",
            def.Name, inst.InstanceId, def.Steps.Count);

        await _store.PersistAsync(inst);
        await _dispatcher.SubmitReadyStepsAsync(inst, def, ct);
        _store.BroadcastUpdate(inst);
        return inst;
    }

    // ── Recovery ───────────────────────────────────────────────────────────────

    public async Task RecoverRunningInstancesAsync(CancellationToken ct)
    {
        if (_repository is null) return;

        var instances = await _repository.LoadRunningInstancesAsync();
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

            foreach (var step in def.Steps)
            {
                if (!inst.StepExecutions.ContainsKey(step.Id))
                    inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };
            }

            _store.Add(inst, def);

            // Re-register reverse lookup for steps with a TaskId
            foreach (var (stepId, stepExec) in inst.StepExecutions)
            {
                if (stepExec.TaskId is not null
                    && stepExec.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Pending)
                {
                    _store.TaskToStep[stepExec.TaskId] = (inst.InstanceId, stepId);
                }
            }

            // Tool steps running when the service died can't be recovered — mark Failed
            foreach (var step in def.Steps.Where(s => s.Type == "tool"))
            {
                var exec = inst.StepExecutions[step.Id];
                if (exec.Status == WorkflowStepStatus.Running)
                {
                    exec.Error = "Service restarted while tool step was running; process lost.";
                    WorkflowStepEvaluators.RecordAudit(exec, WorkflowStepStatus.Failed, exec.Error);
                    WorkflowStepEvaluators.SkipDownstream(step.Id, inst, def);
                }
            }

            // Re-submit Pending steps whose dependencies are all done
            var pendingSteps = def.Steps
                .Where(s => inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d =>
                                inst.StepExecutions.TryGetValue(d, out var e)
                                && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            if (pendingSteps.Count > 0)
                await _dispatcher.SubmitReadyStepsAsync(inst, def, ct);

            // Re-schedule SLA timeouts for WaitingForApproval steps
            foreach (var (stepId, stepExec) in inst.StepExecutions)
            {
                if (stepExec.Status != WorkflowStepStatus.WaitingForApproval
                    || stepExec.SlaDeadline is null) continue;

                var step = def.Steps.FirstOrDefault(s => s.Id == stepId);
                if (step is null) continue;

                var remaining = stepExec.SlaDeadline.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(100);

                // Approval gate schedules the timeout — create one directly here for recovery
                _ = ScheduleRecoveryApprovalTimeout(inst, def, step, stepExec, remaining, ct);

                _logger.LogInformation(
                    "Re-scheduled SLA timeout for step '{StepId}' in instance {Id} (remaining: {Remaining:g})",
                    stepId, inst.InstanceId, remaining);
            }

            _logger.LogInformation(
                "Recovered workflow '{Name}' (instance {Id}, {Pending} step(s) re-submitted)",
                inst.DefinitionName, inst.InstanceId, pendingSteps.Count);
        }
    }

    // ── Pause / Resume ─────────────────────────────────────────────────────────

    public async Task PauseAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_store.TryGet(instanceId, out var inst, out _)) return;
        if (inst.Status != WorkflowStatus.Running) return;

        var lk = _store.GetLock(instanceId);
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = true;
            inst.Status   = WorkflowStatus.Paused;
            _logger.LogInformation(
                "Workflow '{Name}' paused (instance {Id}) — running tasks will complete but no new tasks submitted",
                inst.DefinitionName, instanceId);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    public async Task ResumeAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_store.TryGet(instanceId, out var inst, out var def)) return;
        if (!inst.IsPaused) return;

        var lk = _store.GetLock(instanceId);
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = false;
            inst.Status   = WorkflowStatus.Running;
            _logger.LogInformation(
                "Workflow '{Name}' resumed (instance {Id})", inst.DefinitionName, instanceId);

            await _dispatcher.SubmitReadyStepsAsync(inst, def, ct);
            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    // ── Cancel ─────────────────────────────────────────────────────────────────

    public async Task CancelAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_store.TryGet(instanceId, out var inst, out _)) return;

        inst.Status      = WorkflowStatus.Cancelled;
        inst.IsPaused    = false;
        inst.CompletedAt = DateTime.UtcNow;

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

        foreach (var stepExec in inst.StepExecutions.Values
                     .Where(s => s.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.WaitingForApproval))
            WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Skipped, "Workflow cancelled");

        await _store.PersistAsync(inst);
        _store.BroadcastUpdate(inst);
    }

    // ── Context update ─────────────────────────────────────────────────────────

    public async Task UpdateContextAsync(
        string instanceId, Dictionary<string, string> updates, CancellationToken ct = default)
    {
        if (!_store.TryGet(instanceId, out var inst, out _)) return;

        var lk = _store.GetLock(instanceId);
        await lk.WaitAsync(ct);
        try
        {
            foreach (var (key, value) in updates)
                inst.InputContext[key] = value;

            _logger.LogInformation(
                "Workflow {Id}: context updated — keys: {Keys}", instanceId,
                string.Join(", ", updates.Keys));

            await _store.PersistAsync(inst);
            _store.BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    // ── Query API ──────────────────────────────────────────────────────────────

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

    // ── Private helpers ────────────────────────────────────────────────────────

    private WorkflowDefinition? FindDefinition(string id, string? workspacePath)
    {
        var all = GetAvailableDefinitions(workspacePath);
        return all.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Re-creates the SLA approval timeout during recovery without going through
    /// WorkflowApprovalGate (which would re-stamp the deadline).
    /// </summary>
    private async Task ScheduleRecoveryApprovalTimeout(
        WorkflowInstance inst, WorkflowDefinition def,
        WorkflowStepDef step, WorkflowStepExecution stepExec,
        TimeSpan remaining, CancellationToken ct)
    {
        await Task.Delay(remaining, ct);
        if (!_store.TryGet(inst.InstanceId, out var liveInst, out var liveDef)) return;

        var lk = _store.GetLock(inst.InstanceId);
        await lk.WaitAsync(ct);
        try
        {
            var liveExec = liveInst.StepExecutions.GetValueOrDefault(step.Id);
            if (liveExec?.Status != WorkflowStepStatus.WaitingForApproval) return;

            var slaReason = $"SLA of {remaining.TotalHours:F1} hour(s) exceeded with no human response.";
            liveExec.Error       = slaReason;
            liveExec.CompletedAt = DateTime.UtcNow;
            WorkflowStepEvaluators.RecordAudit(liveExec, WorkflowStepStatus.Failed, slaReason);
            WorkflowStepEvaluators.SkipDownstream(step.Id, liveInst, liveDef);

            liveInst.Status      = WorkflowStatus.Failed;
            liveInst.CompletedAt = DateTime.UtcNow;

            await _store.PersistAsync(liveInst);
            _store.BroadcastUpdate(liveInst);
        }
        finally { lk.Release(); }
    }
}
