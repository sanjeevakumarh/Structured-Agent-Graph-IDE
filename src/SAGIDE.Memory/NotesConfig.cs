namespace SAGIDE.Memory;

/// <summary>
/// Configuration for the Logseq notes indexing service.
/// Bound from <c>SAGIDE:Notes</c> in appsettings.json.
/// </summary>
public class NotesConfig
{
    public bool Enabled { get; set; }
    public string GraphPath { get; set; } = string.Empty;
    public string Schedule { get; set; } = "0 0 * * 0";
    public List<string> FilePatterns { get; set; } = ["*.md"];
    public List<string> ExcludeFolders { get; set; } = ["logseq", ".git", "assets", "node_modules"];
    public string SourceTag { get; set; } = "logseq_notes";
    public List<string> TaskMarkers { get; set; } = ["TODO", "DOING", "LATER", "NOW", "NEXT", "IN-PROGRESS"];

    /// <summary>
    /// Model spec for LLM-powered search summaries (e.g. "ollama/gpt-oss-120b@gmini").
    /// When empty, search returns results without a summary.
    /// </summary>
    public string SummaryModel { get; set; } = string.Empty;

    /// <summary>Fallback model when primary summary model is unavailable.</summary>
    public string SummaryModelFallback { get; set; } = string.Empty;
}
