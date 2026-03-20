using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Workflows;

/// <summary>
/// Handles convergence loop escalation for feedback-loop workflows.
/// Extracted from WorkflowEngine; responsible for escalating when max iterations are exceeded
/// or when contradiction detection fires.
/// </summary>
public sealed class WorkflowLoopController
{
    private readonly WorkflowInstanceStore _store;
    private readonly ILogger<WorkflowLoopController> _logger;

    public WorkflowLoopController(
        WorkflowInstanceStore store,
        ILogger<WorkflowLoopController> logger)
    {
        _store  = store;
        _logger = logger;
    }

    /// <summary>
    /// Handles HUMAN_APPROVAL, DLQ, and CANCEL escalation targets when a feedback loop
    /// exceeds its iteration cap or a contradiction is detected.
    /// </summary>
    public async Task EscalateLoopAsync(
        WorkflowInstance inst,
        WorkflowDefinition def,
        WorkflowStepDef loopTargetDef,
        WorkflowStepExecution loopTargetExec,
        AgentType agentType,
        string reason,
        string escalationTarget)
    {
        switch (escalationTarget)
        {
            case "HUMAN_APPROVAL":
                inst.IsPaused = true;
                inst.Status   = WorkflowStatus.Paused;
                loopTargetExec.Output = reason + " Human approval required to continue or abort.";
                WorkflowStepEvaluators.RecordAudit(loopTargetExec, WorkflowStepStatus.WaitingForApproval, reason);
                _logger.LogInformation(
                    "Workflow {Id} paused for human approval — reason: {Reason}",
                    inst.InstanceId, reason);
                _store.PublishApprovalNeeded(
                    inst.InstanceId, loopTargetDef.Id,
                    reason + " Please decide whether to continue or cancel the workflow.");
                break;

            case "DLQ":
                loopTargetExec.Error = reason + " Escalated to DLQ.";
                WorkflowStepEvaluators.RecordAudit(loopTargetExec, WorkflowStepStatus.Failed, loopTargetExec.Error);
                inst.Status      = WorkflowStatus.Failed;
                inst.CompletedAt = DateTime.UtcNow;
                WorkflowStepEvaluators.SkipDownstream(loopTargetDef.Id, inst, def);
                break;

            default: // CANCEL
                loopTargetExec.Error = reason +
                    $" Increase AgentLimits:{agentType}:MaxIterations " +
                    "or the step's max_iterations to allow more iterations.";
                WorkflowStepEvaluators.RecordAudit(loopTargetExec, WorkflowStepStatus.Failed, loopTargetExec.Error);
                inst.Status      = WorkflowStatus.Failed;
                inst.CompletedAt = DateTime.UtcNow;
                WorkflowStepEvaluators.SkipDownstream(loopTargetDef.Id, inst, def);
                break;
        }

        await _store.PersistAsync(inst);
        _store.BroadcastUpdate(inst);
    }
}
