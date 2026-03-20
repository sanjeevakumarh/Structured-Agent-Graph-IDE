using SAGIDE.Core.DTOs;
using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Public contract for the DAG-based workflow execution engine.
///
/// Consumers (MessageHandler, REST API, ServiceLifetime, tests) depend on this
/// interface rather than the concrete WorkflowEngine so the implementation can
/// move to its own assembly or process without breaking callers.
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>Start a new workflow instance from a definition ID and input context.</summary>
    Task<WorkflowInstance> StartAsync(StartWorkflowRequest req, CancellationToken ct);

    /// <summary>Recover running/paused instances from the database after a restart.</summary>
    Task RecoverRunningInstancesAsync(CancellationToken ct);

    /// <summary>
    /// Called by the orchestrator on every task update.
    /// Routes the update to the correct workflow step if the task belongs to one.
    /// </summary>
    Task OnTaskUpdateAsync(TaskStatusResponse status);

    Task PauseAsync(string instanceId, CancellationToken ct = default);
    Task ResumeAsync(string instanceId, CancellationToken ct = default);
    Task UpdateContextAsync(string instanceId, Dictionary<string, string> updates, CancellationToken ct = default);
    Task CancelAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Process a human approval or rejection for an approval-gate step.</summary>
    Task ApproveWorkflowStepAsync(
        string instanceId, string stepId, bool approved, string? comment,
        CancellationToken ct = default);

    List<WorkflowDefinition> GetAvailableDefinitions(string? workspacePath = null);
    WorkflowInstance? GetInstance(string instanceId);
    List<WorkflowInstance> GetAllInstances();

    /// <summary>Number of workflow instances currently active in memory.</summary>
    int ActiveInstanceCount { get; }
}
