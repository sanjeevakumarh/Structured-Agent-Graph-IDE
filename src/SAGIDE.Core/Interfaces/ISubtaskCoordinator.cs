using SAGIDE.Contracts;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Public contract for multi-model prompt orchestration.
///
/// Callers (SchedulerService, PromptEndpoints, SkillsEndpoints) depend on this
/// interface so the SubtaskCoordinator implementation can move to its own assembly
/// without touching the composition root.
/// </summary>
public interface ISubtaskCoordinator
{
    /// <summary>
    /// Execute a multi-model prompt end-to-end:
    /// data collection → parallel subtask dispatch → result synthesis → output.
    /// </summary>
    Task RunAsync(
        PromptDefinition prompt,
        Dictionary<string, string>? variableOverrides,
        CancellationToken ct);
}
