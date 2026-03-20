namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Git operations needed by the workflow engine (shadow workspace management).
/// Abstracts <c>GitService</c> so <c>SAGIDE.Workflows</c> has no reference to
/// <c>SAGIDE.Service.Infrastructure</c>.
/// </summary>
public interface IWorkflowGitService
{
    /// <summary>Returns true when the given path is a git repository root.</summary>
    bool IsGitRepo(string workspacePath);

    /// <summary>Creates an isolated shadow workspace for the workflow step to operate in.</summary>
    Task<string?> ProvisionShadowAsync(
        string workspacePath, string taskId, string agentType,
        CancellationToken ct = default);

    /// <summary>
    /// Promotes the shadow workspace changes back to the main workspace.
    /// Returns (success, summary) describing what was merged.
    /// </summary>
    Task<(bool Success, string Summary)> PromoteShadowAsync(
        string workspacePath, string shadowPath,
        CancellationToken ct = default);

    /// <summary>Destroys the shadow workspace, cleaning up temp directories.</summary>
    Task DestroyShadowAsync(
        string workspacePath, string shadowPath,
        CancellationToken ct = default);
}
