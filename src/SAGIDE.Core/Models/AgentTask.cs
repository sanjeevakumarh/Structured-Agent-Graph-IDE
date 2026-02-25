namespace SAGIDE.Core.Models;

public class AgentTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public AgentType AgentType { get; set; }
    public ModelProvider ModelProvider { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = [];
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Queued;
    public int Progress { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Priority { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTime? ScheduledFor { get; set; }
    public string? ComparisonGroupId { get; set; }

    /// <summary>
    /// Identifies which frontend or pipeline submitted this task (e.g. "vscode", "finance_daily", "cli").
    /// Used by the REST API to filter results per-consumer without cross-contamination.
    /// </summary>
    public string? SourceTag { get; set; }

    /// <summary>
    /// Replay: when true, bypasses the SHA-256 output cache and forces a fresh model call.
    /// Runtime-only; not persisted to SQLite.
    /// </summary>
    public bool ForceRerun { get; set; }
}
