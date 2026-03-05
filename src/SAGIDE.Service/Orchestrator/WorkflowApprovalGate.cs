using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Handles human_approval gate steps and SLA timeout scheduling.
/// Extracted from WorkflowEngine.
/// </summary>
internal sealed class WorkflowApprovalGate
{
    private readonly WorkflowInstanceStore _store;
    private readonly ILogger<WorkflowApprovalGate> _logger;

    internal WorkflowApprovalGate(
        WorkflowInstanceStore store,
        ILogger<WorkflowApprovalGate> logger)
    {
        _store  = store;
        _logger = logger;
    }

    // ── Activate an approval gate step ────────────────────────────────────────

    /// <summary>
    /// Transitions a pending human_approval step to WaitingForApproval, fires the
    /// approval-needed event, and schedules the SLA timeout if configured.
    /// Called from WorkflowStepDispatcher.SubmitReadyStepsAsync.
    /// </summary>
    internal async Task ActivateApprovalStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, CancellationToken ct)
    {
        var stepExec = inst.StepExecutions[stepDef.Id];
        var prompt   = stepDef.ApprovalPrompt is not null
            ? PromptTemplate.RenderWorkflowStep(
                stepDef.ApprovalPrompt, inst.InputContext, inst.StepExecutions, PromptTemplate.MaxOutputChars)
            : $"Please review the workflow '{inst.DefinitionName}' and approve or reject step '{stepDef.Id}'.";

        stepExec.StartedAt = DateTime.UtcNow;
        stepExec.Output    = prompt;
        WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.WaitingForApproval, "Awaiting human decision");

        _logger.LogInformation(
            "Workflow {Id} step '{StepId}' is waiting for human approval", inst.InstanceId, stepDef.Id);

        if (stepDef.SlaHours > 0)
        {
            var slaDelay = TimeSpan.FromHours(stepDef.SlaHours);
            stepExec.SlaDeadline = DateTime.UtcNow.Add(slaDelay);
            ScheduleApprovalTimeout(inst.InstanceId, stepDef.Id, slaDelay, stepDef.TimeoutAction, ct);
        }

        await _store.PersistAsync(inst);
        _store.BroadcastUpdate(inst);
        _store.PublishApprovalNeeded(inst.InstanceId, stepDef.Id, prompt);
    }

    // ── Process approve / reject ───────────────────────────────────────────────

    /// <summary>
    /// Called via WorkflowEngine.ApproveWorkflowStepAsync. Marks the step Completed or Rejected
    /// and returns whether the DAG should advance (approved) or skip downstream (rejected).
    /// </summary>
    internal Task<bool> HandleApprovalAsync(
        string instanceId, string stepId, bool approved, string? comment,
        WorkflowDefinition def, CancellationToken ct)
    {
        if (!_store.TryGet(instanceId, out var inst, out _))
        {
            _logger.LogWarning("ApproveWorkflowStep: instance '{Id}' not found", instanceId);
            return Task.FromResult(false);
        }

        var stepExec = inst.StepExecutions.GetValueOrDefault(stepId);
        if (stepExec is null || stepExec.Status != WorkflowStepStatus.WaitingForApproval)
        {
            _logger.LogWarning(
                "ApproveWorkflowStep: step '{StepId}' not in WaitingForApproval state (current: {Status})",
                stepId, stepExec?.Status);
            return Task.FromResult(false);
        }

        stepExec.CompletedAt = DateTime.UtcNow;

        if (approved)
        {
            stepExec.Output = string.IsNullOrWhiteSpace(comment) ? "Approved" : $"Approved: {comment}";
            WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Completed, stepExec.Output);
            _logger.LogInformation(
                "Workflow {Id} step '{StepId}' approved by user", instanceId, stepId);

            if (inst.IsPaused)
            {
                inst.IsPaused = false;
                inst.Status   = WorkflowStatus.Running;
            }
        }
        else
        {
            var rejectReason = string.IsNullOrWhiteSpace(comment) ? "Rejected by user" : $"Rejected: {comment}";
            stepExec.Error = rejectReason;
            WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Rejected, rejectReason);
            _logger.LogInformation(
                "Workflow {Id} step '{StepId}' rejected by user", instanceId, stepId);
            WorkflowStepEvaluators.SkipDownstream(stepId, inst, def);
        }

        return Task.FromResult(approved);
    }

    // ── SLA timeout scheduling ─────────────────────────────────────────────────

    internal void ScheduleApprovalTimeout(
        string instanceId, string stepId, TimeSpan delay, string timeoutAction, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                if (!_store.TryGet(instanceId, out var inst, out var def)) return;

                var lk = _store.GetLock(instanceId);
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
                    WorkflowStepEvaluators.RecordAudit(stepExec, WorkflowStepStatus.Failed, slaReason);
                    WorkflowStepEvaluators.SkipDownstream(stepId, inst, def);

                    inst.Status      = WorkflowStatus.Failed;
                    inst.CompletedAt = DateTime.UtcNow;

                    await _store.PersistAsync(inst);
                    _store.BroadcastUpdate(inst);
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
}
