namespace SAGIDE.Core.Models;

/// <summary>
/// Prevents workflows from automating forbidden actions (e.g. modifying secrets).
/// Bind from <c>SAGIDE:WorkflowPolicy</c>.
/// </summary>
public class WorkflowPolicyConfig
{
    /// <summary>Set to false to disable all policy checks (not recommended in production).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Glob patterns for file paths the workflow must NOT operate on.
    /// Checked against each entry in WorkflowInstance.FilePaths.
    /// </summary>
    public List<string> ProtectedPathPatterns { get; init; } =
    [
        "**/.env",
        "**/.env.*",
        "**/appsettings*.json",
        "**/secrets/**",
        "**/*.pfx",
        "**/*.pem",
        "**/*.key",
        "**/*.p12",
    ];

    /// <summary>Agent names (as written in YAML) that are not allowed to run in any workflow.</summary>
    public List<string> BlockedAgentTypes { get; init; } = [];

    /// <summary>
    /// Maximum number of workflow steps allowed per instance (0 = no limit).
    /// Prevents runaway workflows from looping indefinitely via large DAGs.
    /// </summary>
    public int MaxStepsPerWorkflow { get; init; } = 50;
}
