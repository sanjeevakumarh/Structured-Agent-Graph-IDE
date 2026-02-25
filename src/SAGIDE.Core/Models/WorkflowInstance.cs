using SAGIDE.Core.DTOs;

namespace SAGIDE.Core.Models;

/// <summary>
/// Runtime state of a running (or completed) workflow instance.
/// Persisted to SQLite so state survives service restarts.
/// </summary>
public class WorkflowInstance
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DefinitionId { get; set; } = string.Empty;
    public string DefinitionName { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Running;

    /// <summary>User-supplied parameter values (maps {{param_name}} → value).</summary>
    public Dictionary<string, string> InputContext { get; set; } = [];

    /// <summary>Per-step execution state, keyed by step ID.</summary>
    public Dictionary<string, WorkflowStepExecution> StepExecutions { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public string DefaultModelId { get; set; } = string.Empty;
    public string DefaultModelProvider { get; set; } = string.Empty;
    public string? ModelEndpoint { get; set; }

    /// <summary>
    /// When true, running tasks are allowed to complete but no new steps are submitted.
    /// Call WorkflowEngine.ResumeAsync to re-evaluate pending steps and continue.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>Workspace path stored so the instance can be recovered from DB after restart.</summary>
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Per-step model overrides captured at launch time (keyed by step ID).
    /// Checked after the YAML-baked step model but before the instance default and affinities.
    /// </summary>
    public Dictionary<string, StepModelOverride> StepModelOverrides { get; set; } = [];

    /// <summary>
    /// Absolute path to the active shadow worktree ().
    /// Set by workspace_provision, cleared by workspace_teardown or auto-destroy on failure/cancel.
    /// Null when no shadow is provisioned.
    /// </summary>
    public string? ShadowWorkspacePath { get; set; }
}

public class AuditEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public WorkflowStepStatus FromStatus { get; set; }
    public WorkflowStepStatus ToStatus { get; set; }
    public string? Reason { get; set; }
}

public class WorkflowStepExecution
{
    public string StepId { get; set; } = string.Empty;

    /// <summary>The AgentTask ID submitted for this step (null if not yet started).</summary>
    public string? TaskId { get; set; }

    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;

    /// <summary>State-transition log written on every status change (BaseNode contract).</summary>
    public List<AuditEntry> AuditLog { get; set; } = [];

    /// <summary>Raw LLM output from the completed step.</summary>
    public string? Output { get; set; }

    public int IssueCount { get; set; }

    /// <summary>1-based iteration count for feedback-loop steps.</summary>
    public int Iteration { get; set; } = 1;

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Exit code from a tool step execution. Null for agent / router / constraint steps.</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Issue count from the prior loop iteration, used for contradiction detection ().
    /// Set just before a new iteration is started; compared against the new iteration's result.
    /// </summary>
    public int PreviousIssueCount { get; set; }

    /// <summary>
    /// MACP IntentPackage produced by this step ().
    /// Populated by agent steps when the model response includes a structured intent block.
    /// Null for tool, constraint, router, and human_approval steps.
    /// </summary>
    public IntentPackage? IntentPackage { get; set; }
}

public enum WorkflowStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Paused,
}

public enum WorkflowStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    /// <summary>Step is a human_approval gate; waiting for user to approve or reject.</summary>
    WaitingForApproval,
    /// <summary>Human rejected the approval gate; downstream steps are skipped.</summary>
    Rejected,
}
