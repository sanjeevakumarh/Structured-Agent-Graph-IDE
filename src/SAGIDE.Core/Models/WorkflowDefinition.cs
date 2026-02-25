namespace SAGIDE.Core.Models;

/// <summary>
/// Static definition of a workflow — parsed from YAML or built-in.
/// </summary>
public class WorkflowDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<WorkflowParameter> Parameters { get; set; } = [];
    public List<WorkflowStepDef> Steps { get; set; } = [];
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Required for workflows with feedback loops (next: back-edges).
    /// Defines the convergence contract: max iterations, escalation target, and optional causal memory.
    /// </summary>
    public ConvergencePolicy? ConvergencePolicy { get; set; }
}

public class WorkflowParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string? Default { get; set; }
}

public class WorkflowStepDef
{
    /// <summary>Unique step identifier within this workflow (e.g. "code_review").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"agent" (default) or "router" (conditional branching, no task submitted).</summary>
    public string Type { get; set; } = "agent";

    /// <summary>Agent name mapped to AgentType enum: Coder, Reviewer, Tester, Security, Documenter, Debug.</summary>
    public string? Agent { get; set; }

    /// <summary>Step IDs that must complete before this step runs.</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Prompt template; supports {{param_name}} and {{step_id.output}} substitution.</summary>
    public string? Prompt { get; set; }

    /// <summary>Override model ID for this step (e.g. "claude-sonnet-4-6").</summary>
    public string? ModelId { get; set; }

    /// <summary>Override model provider for this step (e.g. "claude", "ollama").</summary>
    public string? ModelProvider { get; set; }

    /// <summary>For feedback loops: step ID to re-run after this step completes (if issues found).</summary>
    public string? Next { get; set; }

    /// <summary>Maximum number of loop iterations when Next is set. Default 1 (no loop).</summary>
    public int MaxIterations { get; set; } = 1;

    /// <summary>Router configuration — only used when Type == "router".</summary>
    public RouterConfig? Router { get; set; }

    // ── Tool step fields (Type == "tool") ─────────────────────────────────────

    /// <summary>Shell command to run, e.g. "dotnet build" or "npm test".</summary>
    public string? Command { get; set; }

    /// <summary>Working directory for the command. Defaults to the workflow workspace path.</summary>
    public string? WorkingDir { get; set; }

    /// <summary>How non-zero exit codes are handled: FAIL_ON_NONZERO | WARN_ON_NONZERO | IGNORE.</summary>
    public string ExitCodePolicy { get; set; } = "FAIL_ON_NONZERO";

    /// <summary>
    /// Per-step wall-clock timeout in seconds for tool steps (BaseNode timeout_sec).
    /// 0 = no per-step timeout (global task execution timeout applies).
    /// </summary>
    public int TimeoutSec { get; set; } = 0;

    // ── Constraint step fields (Type == "constraint") ─────────────────────────

    /// <summary>
    /// Constraint expression to evaluate against prior step outputs. Examples:
    ///   exit_code(build) == 0
    ///   output(review).contains('PASS')
    ///   issue_count(review) == 0
    /// </summary>
    public string? ConstraintExpr { get; set; }

    /// <summary>What to do when the constraint fails: "fail" (default) or "warn".</summary>
    public string OnConstraintFail { get; set; } = "fail";

    // ── Context retrieval step fields (Type == "context_retrieval") ───────────

    /// <summary>
    /// Name of the input-context variable to set.
    /// The aggregated output text is stored in inst.InputContext[ContextVarName]
    /// so downstream prompts can reference it via {{context_var_name}}.
    /// </summary>
    public string? ContextVarName { get; set; }

    /// <summary>
    /// Step IDs whose outputs are aggregated into ContextVarName.
    /// Outputs are concatenated in declaration order, separated by a blank line.
    /// </summary>
    public List<string> SourceSteps { get; set; } = [];

    // ── Human approval step fields (Type == "human_approval") ─────────────────

    /// <summary>Hours before the SLA is considered breached. 0 = no timeout.</summary>
    public int SlaHours { get; set; } = 0;

    /// <summary>
    /// What to do on SLA timeout: "cancel" (default) or "dlq".
    /// </summary>
    public string TimeoutAction { get; set; } = "cancel";

    /// <summary>
    /// Human-readable prompt shown in the approval UI.
    /// Supports {{step_id.output}} template variables.
    /// </summary>
    public string? ApprovalPrompt { get; set; }

    // ── Shadow workspace step fields ───────────────────────────────────────────

    /// <summary>Git ref to snapshot when provisioning a shadow worktree. Default "HEAD".</summary>
    public string ShadowBranch { get; set; } = "HEAD";

    /// <summary>Action on workspace_teardown: "promote" (apply shadow diff to real workspace) or "destroy" (discard). Default "promote".</summary>
    public string ShadowAction { get; set; } = "promote";
}

/// <summary>
/// Convergence policy for constraint-loop workflows ().
/// Declare on any workflow that contains a feedback loop (next: back-edge).
/// </summary>
public class ConvergencePolicy
{
    /// <summary>Maximum REFACTOR→VALIDATE cycles before escalating. Required.</summary>
    public int MaxIterations { get; set; } = 3;

    /// <summary>
    /// What to do when max_iterations is exceeded.
    /// HUMAN_APPROVAL — pause and wait for human decision.
    /// DLQ — send workflow to dead-letter queue.
    /// CANCEL — cancel the workflow immediately (default).
    /// </summary>
    public string EscalationTarget { get; set; } = "CANCEL";

    /// <summary>FAILING_NODES_ONLY | FROM_CODEGEN | FULL_WORKFLOW</summary>
    public string PartialRetryScope { get; set; } = "FAILING_NODES_ONLY";

    /// <summary>When true, convergence hints from prior iterations are injected into refactor prompts.</summary>
    public bool ConvergenceHintMemory { get; set; } = false;

    /// <summary>Wall-clock timeout per iteration in seconds. 0 = unlimited.</summary>
    public int TimeoutPerIterationSec { get; set; } = 0;

    /// <summary>
    /// When true, immediately escalates to HUMAN_APPROVAL if issues fail to decrease between
    /// iterations — indicating mutually exclusive constraints (). Default: true.
    /// </summary>
    public bool ContradictionDetection { get; set; } = true;
}

public class RouterConfig
{
    /// <summary>Evaluated in order; first matching branch wins.</summary>
    public List<RouterBranch> Branches { get; set; } = [];
}

public class RouterBranch
{
    /// <summary>Condition expression: "hasIssues", "success", "failed", or "output.contains('X')".</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>Step ID to activate when this condition is true.</summary>
    public string Target { get; set; } = string.Empty;
}
