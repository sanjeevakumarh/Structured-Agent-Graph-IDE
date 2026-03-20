// Config types promoted to SAGIDE.Core.Models; aliases for back-compat
global using WorkflowPolicyConfig  = SAGIDE.Core.Models.WorkflowPolicyConfig;
global using AgentLimitsConfig     = SAGIDE.Core.Models.AgentLimitsConfig;
global using AgentLimitEntry       = SAGIDE.Core.Models.AgentLimitEntry;
global using TaskAffinitiesConfig  = SAGIDE.Core.Models.TaskAffinitiesConfig;
global using TaskAffinityEntry     = SAGIDE.Core.Models.TaskAffinityEntry;

using SAGIDE.Core.Models;

namespace SAGIDE.Service.Resilience;

public class TimeoutConfig
{
    public int NamedPipeRequestMs { get; init; } = 300_000;
    public int HealthCheckMs { get; init; } = 5_000;
    public int PipeReconnectMs { get; init; } = 3_000;
    public int PipeReconnectMaxMs { get; init; } = 30_000;
    public int TaskExecutionMs { get; init; } = 300_000;

    /// <summary>
    /// If no streaming chunk is received within this window, the stream is cancelled.
    /// Protects against stalled connections that hold a slot indefinitely.
    /// </summary>
    public int StreamingIdleTimeoutMs { get; init; } = 300_000;

    public TimeSpan StreamingIdleTimeout => TimeSpan.FromMilliseconds(StreamingIdleTimeoutMs);

    public Dictionary<string, int> Providers { get; init; } = new()
    {
        ["Claude"] = 300_000,
        ["Codex"] = 300_000,
        ["Gemini"] = 300_000,
        ["Ollama"] = 1_800_000,   // local models can be slow; keep generous
    };

    public int GetProviderTimeoutMs(ModelProvider provider)
    {
        return Providers.TryGetValue(provider.ToString(), out var ms) ? ms : 300_000;
    }

    public TimeSpan TaskExecutionTimeout => TimeSpan.FromMilliseconds(TaskExecutionMs);
}

// AgentLimitsConfig, AgentLimitEntry, TaskAffinitiesConfig, TaskAffinityEntry, WorkflowPolicyConfig
// promoted to SAGIDE.Core.Models — global aliases above keep existing code compiling.
