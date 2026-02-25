namespace SAGIDE.Core.Models;

/// <summary>
/// Typed inter-agent communication record (— MACP).
/// Every ModelExecutionNode MUST produce an IntentPackage as part of its output.
/// Stored per workflow step so that the reasoning chain is fully auditable.
/// </summary>
public class IntentPackage
{
    public string PackageId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    // ── Provenance ─────────────────────────────────────────────────────────────
    public string WorkflowInstanceId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Decision ───────────────────────────────────────────────────────────────

    /// <summary>ARCHITECTURAL | CODEGEN | REFACTOR | REVIEW | ESCALATION</summary>
    public string IntentType { get; set; } = string.Empty;

    /// <summary>What was decided — concise, declarative statement.</summary>
    public string Decision { get; set; } = string.Empty;

    /// <summary>Why this decision was made.</summary>
    public string Rationale { get; set; } = string.Empty;

    /// <summary>Self-reported model confidence [0, 1].</summary>
    public double Confidence { get; set; } = 1.0;

    // ── Context ────────────────────────────────────────────────────────────────

    /// <summary>Assumptions the agent made that, if violated, should trigger re-evaluation.</summary>
    public List<string> Assumptions { get; set; } = [];

    /// <summary>Active constraints that drove the decision.</summary>
    public List<string> ConstraintsInScope { get; set; } = [];

    /// <summary>Alternatives considered and the reason each was rejected.</summary>
    public List<AlternativeOption> AlternativesConsidered { get; set; } = [];

    // ── Downstream hints ───────────────────────────────────────────────────────

    /// <summary>
    /// Structured hints for downstream node types.
    /// Required for ArchitectureNode outputs ().
    /// </summary>
    public List<DownstreamHint> DownstreamHints { get; set; } = [];

    // ── Audit ──────────────────────────────────────────────────────────────────

    /// <summary>PackageIds that this package supersedes (e.g., after AssumptionViolation).</summary>
    public List<string> InvalidatedBy { get; set; } = [];
}

public class AlternativeOption
{
    public string Option { get; set; } = string.Empty;
    public string RejectedBecause { get; set; } = string.Empty;
}

public class DownstreamHint
{
    /// <summary>The node type this hint targets, e.g. "TestGenNode", "CodeGenNode".</summary>
    public string NodeType { get; set; } = string.Empty;

    /// <summary>Structured guidance for the downstream node.</summary>
    public string HintText { get; set; } = string.Empty;
}
