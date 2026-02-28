using SAGIDE.Core.DTOs;
using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Abstracts task submission, cancellation, and status queries so callers (WorkflowEngine,
/// SchedulerService, SubtaskCoordinator) depend on this interface instead of the concrete
/// AgentOrchestrator, enabling independent testing.
/// </summary>
public interface ITaskSubmissionService
{
    /// <summary>Enqueues a task for agent execution and returns the assigned task ID.</summary>
    Task<string> SubmitTaskAsync(AgentTask task, CancellationToken ct);

    /// <summary>Requests cancellation of an in-flight or queued task.</summary>
    Task CancelTaskAsync(string taskId, CancellationToken ct);

    /// <summary>Returns the current status of a task, or null if the task ID is unknown.</summary>
    TaskStatusResponse? GetTaskStatus(string taskId);
}
