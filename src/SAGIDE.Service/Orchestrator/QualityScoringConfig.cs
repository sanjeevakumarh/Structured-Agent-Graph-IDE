namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Configuration for automatic quality scoring of LLM responses.
/// Mode: "workflow" (default) scores the final synthesized output once per workflow.
///        "step" scores every individual LLM call (subtask + data collection).
///        "off" disables scoring entirely (same as Enabled=false).
/// </summary>
public sealed class QualityScoringConfig
{
    public bool Enabled { get; set; }

    /// <summary>"workflow" | "step" | "off"</summary>
    public string Mode { get; set; } = "workflow";

    /// <summary>Model spec for the scoring LLM (e.g. "ollama/qwen3.5:4b@edge").</summary>
    public string ScoringModel { get; set; } = "";

    /// <summary>Fallback if the primary scoring model is unavailable.</summary>
    public string FallbackScoringModel { get; set; } = "";

    public bool IsWorkflowMode => Enabled && Mode.Equals("workflow", StringComparison.OrdinalIgnoreCase);
    public bool IsStepMode => Enabled && Mode.Equals("step", StringComparison.OrdinalIgnoreCase);
}
