namespace SAGIDE.Core.Models;

/// <summary>
/// Binds the <c>SAGIDE:Routing</c> config section.
/// Controls performance/quality-based routing hints and quality sampling behaviour.
/// </summary>
public class RoutingConfig
{
    /// <summary>Whether to dispatch idle-capacity quality probe tasks (default false).</summary>
    public bool QualitySamplingEnabled { get; set; }

    /// <summary>Fraction of eligible tasks that trigger a quality probe (0-1, default 0.1).</summary>
    public double SamplingRate { get; set; } = 0.1;

    /// <summary>
    /// Maximum total sampled tokens per hour across all probes.
    /// Guards against runaway cost. Default 20 000.
    /// </summary>
    public int MaxQualitySampleTokensPerHour { get; set; } = 20_000;

    /// <summary>
    /// When false (default), routing hints only reorder fallback candidates; the
    /// explicit model.Endpoint is never changed. When true, the highest-scoring
    /// candidate may override the preferred endpoint.
    /// </summary>
    public bool AllowOverride { get; set; }

    /// <summary>Days to keep perf samples (default 3).</summary>
    public int PerfRetentionDays { get; set; } = 3;

    /// <summary>Days to keep quality samples (default 7).</summary>
    public int QualityRetentionDays { get; set; } = 7;

    /// <summary>Maximum simultaneous quality probe tasks per server (default 1).</summary>
    public int MaxConcurrentProbes { get; set; } = 1;

    /// <summary>Ollama model IDs eligible as probe targets (e.g. "ollama/qwen2.5-coder:7b").</summary>
    public List<string> QualityProbeAllowlist { get; set; } = [];

    /// <summary>Routing hint weight configuration (must sum to approximately 1.0).</summary>
    public RoutingWeights Weights { get; set; } = new();
}

public class RoutingWeights
{
    /// <summary>Weight for the user's explicit model preference (default 0.6).</summary>
    public double UserChoice { get; set; } = 0.6;

    /// <summary>Weight for observed latency/error-rate performance (default 0.25).</summary>
    public double Performance { get; set; } = 0.25;

    /// <summary>Weight for LLM-scored output quality (default 0.15).</summary>
    public double Quality { get; set; } = 0.15;
}
