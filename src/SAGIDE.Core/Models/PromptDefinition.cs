namespace SAGIDE.Core.Models;

/// <summary>
/// Deserialized representation of a prompt YAML file.
/// Fields map 1:1 to the YAML schema defined in ProjectDirection.md.
/// </summary>
public class PromptDefinition
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Domain { get; set; } = string.Empty;

    /// <summary>Optional cron expression (e.g. "0 18 * * 1-5"). Null means manual-only.</summary>
    public string? Schedule { get; set; }

    /// <summary>Source tag stamped on tasks this prompt generates (e.g. "finance_daily").</summary>
    public string? SourceTag { get; set; }

    public string? Description { get; set; }

    public PromptModelPreference? ModelPreference { get; set; }

    public List<PromptDataSource> DataSources { get; set; } = [];

    public Dictionary<string, object> Variables { get; set; } = [];

    /// <summary>Optional pre-processing steps that gather external data before subtasks run.</summary>
    public PromptDataCollection? DataCollection { get; set; }

    public string? PromptTemplate { get; set; }

    public List<PromptSubtask> Subtasks { get; set; } = [];

    public PromptSynthesis? Synthesis { get; set; }

    public PromptOutput? Output { get; set; }

    /// <summary>Absolute path of the YAML file this was loaded from. Set by PromptRegistry.</summary>
    public string? FilePath { get; set; }
}

public class PromptModelPreference
{
    /// <summary>Legacy single-model field. Use Orchestrator + Subtasks for multi-model prompts.</summary>
    public string? Primary { get; set; }

    /// <summary>"local" | "cloud" | "ollama/model-id" — determines the orchestration host.</summary>
    public string? Orchestrator { get; set; }

    public string? Fallback { get; set; }

    /// <summary>Maps subtask name → model spec (e.g. "fundamental" → "ollama/deepseek-r1:14b@mini").</summary>
    public Dictionary<string, string> Subtasks { get; set; } = [];
}

public class PromptDataSource
{
    public string Type { get; set; } = string.Empty;   // web_api | web_search | local_file | rss
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? UrlTemplate { get; set; }
    public string? Path { get; set; }
    public string? Description { get; set; }
    public string? QueryTemplate { get; set; }
    public List<string> Queries { get; set; } = [];
}

// ── Data collection ────────────────────────────────────────────────────────────

public class PromptDataCollection
{
    public List<PromptDataCollectionStep> Steps { get; set; } = [];
}

public class PromptDataCollectionStep
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Step type: read_file | web_api | web_api_batch | filter | web_search_batch</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>URL or file path (may contain {{template}} expressions).</summary>
    public string? Source { get; set; }

    /// <summary>Query template for web_search steps.</summary>
    public string? Query { get; set; }

    /// <summary>Var reference for batch iteration (e.g. "{{watchlist.symbols}}").</summary>
    public string? IterateOver { get; set; }

    /// <summary>Input var reference for filter steps.</summary>
    public string? Input { get; set; }

    /// <summary>Filter condition expression (e.g. "pct_change &lt;= -5").</summary>
    public string? Condition { get; set; }

    /// <summary>Maximum items to include (template or integer string).</summary>
    public string? Limit { get; set; }

    /// <summary>Name of the variable to store this step's output in.</summary>
    public string OutputVar { get; set; } = string.Empty;
}

// ── Subtask ────────────────────────────────────────────────────────────────────

public class PromptSubtask
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Model spec string — may contain a template expression such as
    /// <c>{{model_preference.subtasks.fundamental}}</c> that resolves at runtime.
    /// Format: [provider/]model-id[@machine-name]
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Names of data-collection output vars to include in this subtask's context.
    /// If empty, all collected vars are passed.
    /// </summary>
    public List<string> InputVars { get; set; } = [];

    public string? PromptTemplate { get; set; }
}

// ── Synthesis + Output ────────────────────────────────────────────────────────

public class PromptSynthesis
{
    public string? PromptTemplate { get; set; }
}

public class PromptOutput
{
    public string Format { get; set; } = "markdown";

    /// <summary>File path to write the final output (supports {{date}} template).</summary>
    public string? Destination { get; set; }

    /// <summary>Whether to emit a notification after writing the output.</summary>
    public bool Notify { get; set; }
}
