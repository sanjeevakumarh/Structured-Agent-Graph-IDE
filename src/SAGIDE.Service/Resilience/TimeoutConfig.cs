using SAGIDE.Core.Models;

namespace SAGIDE.Service.Resilience;

public class TimeoutConfig
{
    public int NamedPipeRequestMs { get; init; } = 7_200_000;
    public int HealthCheckMs { get; init; } = 7_200_000;
    public int PipeReconnectMs { get; init; } = 3_000;
    public int PipeReconnectMaxMs { get; init; } = 7_200_000;
    public int TaskExecutionMs { get; init; } = 7_200_000;

    /// <summary>
    /// If no streaming chunk is received within this window, the stream is cancelled.
    /// Protects against stalled connections that hold a slot indefinitely.
    /// </summary>
    public int StreamingIdleTimeoutMs { get; init; } = 7_200_000;

    public TimeSpan StreamingIdleTimeout => TimeSpan.FromMilliseconds(StreamingIdleTimeoutMs);

    public Dictionary<string, int> Providers { get; init; } = new()
    {
        ["Claude"] = 7_200_000,
        ["Codex"] = 7_200_000,
        ["Gemini"] = 7_200_000,
        ["Ollama"] = 7_200_000
    };

    public int GetProviderTimeoutMs(ModelProvider provider)
    {
        return Providers.TryGetValue(provider.ToString(), out var ms) ? ms : 7_200_000;
    }

    public TimeSpan TaskExecutionTimeout => TimeSpan.FromMilliseconds(TaskExecutionMs);
}

public class AgentLimitsConfig
{
    public Dictionary<string, AgentLimitEntry> Agents { get; init; } = new()
    {
        ["CodeReview"]    = new(),
        ["TestGeneration"]= new(),
        ["Refactoring"]   = new() { MaxIterations = 5 },
        ["Debug"]         = new(),
        ["Documentation"] = new(),
        ["SecurityReview"]= new(),
    };

    public int GetMaxIterations(AgentType agentType)
    {
        return Agents.TryGetValue(agentType.ToString(), out var entry)
            ? entry.MaxIterations
            : 5;
    }
}

public class AgentLimitEntry
{
    public int MaxIterations { get; init; } = 5;
}

// ── Task Affinity config ──────────────────────────────────────────────────────
// Model affinities per agent type.  Populated from appsettings.json if present;
// WorkflowEngine falls back to this when a step has no explicit model.

public class TaskAffinityEntry
{
    public string LocalModel    { get; init; } = string.Empty;
    public string CloudProvider { get; init; } = string.Empty;
    public string CloudModel    { get; init; } = string.Empty;
}

public class TaskAffinitiesConfig
{
    public Dictionary<string, TaskAffinityEntry> Affinities { get; init; } = new();

    /// <summary>
    /// Returns (provider, modelId) for the given agent type.
    /// preferLocal=true selects the Ollama model; otherwise the cloud model is returned.
    /// Returns (string.Empty, string.Empty) when no affinity is configured.
    /// </summary>
    public (string Provider, string ModelId) GetDefaultFor(AgentType agentType, bool preferLocal = false)
    {
        if (Affinities.TryGetValue(agentType.ToString(), out var entry))
        {
            if (preferLocal && !string.IsNullOrEmpty(entry.LocalModel))
                return ("Ollama", entry.LocalModel);
            if (!string.IsNullOrEmpty(entry.CloudModel))
                return (entry.CloudProvider, entry.CloudModel);
        }
        return (string.Empty, string.Empty);
    }
}

// ── Workflow Policy config ────────────────────────────────────────────────────
// Prevents workflows from automating forbidden actions (e.g. modifying secrets).

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
