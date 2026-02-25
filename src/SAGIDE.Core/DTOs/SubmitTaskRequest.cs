using SAGIDE.Core.Models;

namespace SAGIDE.Core.DTOs;

public class SubmitTaskRequest
{
    public AgentType AgentType { get; set; }
    public ModelProvider ModelProvider { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> FilePaths { get; set; } = [];
    public int Priority { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
    public DateTime? ScheduledFor { get; set; }
    public string? ComparisonGroupId { get; set; }
    public string? ModelEndpoint { get; set; }

    /// <summary>
    /// Identifies the submitting frontend or pipeline (e.g. "vscode", "finance_daily", "cli").
    /// Defaults to null; the named-pipe handler sets this to "vscode" automatically.
    /// </summary>
    public string? SourceTag { get; set; }
}
