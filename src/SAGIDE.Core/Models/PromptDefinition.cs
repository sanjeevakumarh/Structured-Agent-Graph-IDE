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

    /// <summary>
    /// Optional additional output files. Each entry may specify a <c>source</c> variable
    /// (e.g. a subtask result) to write independently of the main synthesised output.
    /// </summary>
    public List<PromptOutput> Outputs { get; set; } = [];

    /// <summary>
    /// Phase 5: Named skill instances declared for this workflow.
    /// Expanded by WorkflowExpander into data_collection steps before execution.
    /// </summary>
    public List<PromptObject> Objects { get; set; } = [];

    /// <summary>
    /// Phase 5: Ordered call sequence that composes skill instances.
    /// Expanded by WorkflowExpander into data_collection steps + subtasks before execution.
    /// </summary>
    public List<PromptWorkflowCall> Workflow { get; set; } = [];

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

    /// <summary>Step type: read_file | web_api | web_api_batch | filter | web_search_batch | llm_queries | llm_per_section</summary>
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

    /// <summary>
    /// When true, an empty <see cref="OutputVar"/> does NOT abort the run.
    /// Use for search tracks where zero results is a legitimate outcome (e.g. niche topics
    /// with no comparable companies). A <c>missing_data_summary</c> var is injected into
    /// the run context so downstream prompts can acknowledge what is unavailable.
    /// </summary>
    public bool OptionalOutput { get; set; }

    /// <summary>
    /// Reference to a named skill in the skills/ library (e.g. "research/web-research-track"
    /// or just "web-research-track" to search all domains). When set, the step's
    /// <see cref="Type"/> may be omitted — the skill's implementation is expanded at runtime.
    /// </summary>
    public string? Skill { get; set; }

    /// <summary>
    /// Constructor arguments passed to a skill reference. Merged over the skill's default
    /// <c>parameters</c> at expansion time; values support Scriban template expressions.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = [];

    /// <summary>
    /// Prompt template for <c>llm_queries</c> steps.
    /// The LLM receives this rendered text and must return a JSON array of search query strings.
    /// Those queries are then executed via the web search adapter and results are combined.
    /// </summary>
    public string? PlanningPrompt { get; set; }

    /// <summary>
    /// Model spec for the planning/analysis LLM call
    /// (e.g. "ollama/qwen2.5:14b@workstation").
    /// Falls back to <c>model_preference.orchestrator</c> when omitted.
    /// Supports <c>{{template}}</c> expressions (e.g. "{{model_preference.subtasks.analyst}}").
    /// </summary>
    public string? Model { get; set; }

    // ── llm_per_section fields ────────────────────────────────────────────────

    /// <summary>
    /// Per-section analysis prompt for <c>llm_per_section</c> steps.
    /// Available template variables: <c>{{section_name}}</c>, <c>{{search_results}}</c>,
    /// plus all regular prompt vars (topic, context, date, etc.).
    /// </summary>
    public string? SectionAnalysisPrompt { get; set; }

    /// <summary>
    /// Name of the var that holds search results to pass into each section analysis.
    /// Defaults to <c>all_search_results</c>.
    /// </summary>
    public string? SearchResultsVar { get; set; }

    /// <summary>
    /// Maximum number of sections to generate (template or integer string). Default 5.
    /// </summary>
    public string? MaxSections { get; set; }

    /// <summary>
    /// When set on an <c>llm_per_section</c> step with <c>max_sections: "1"</c>, the
    /// planning LLM call is skipped entirely and this value is used as the single section
    /// name. Eliminates a wasted LLM round-trip for skills that always produce one section.
    /// </summary>
    public string? SectionTitle { get; set; }

    // ── llm step fields ───────────────────────────────────────────────────────

    /// <summary>
    /// Prompt text for a <c>type: llm</c> step. Supports Scriban template expressions.
    /// The rendered text is submitted as a single LLM task; the result is stored in
    /// <see cref="OutputVar"/>. Runs synchronously so later steps can reference it.
    /// </summary>
    public string? PromptTemplate { get; set; }

    /// <summary>
    /// Optional list of var names to include in the prompt context for a <c>type: llm</c>
    /// step. When empty all current vars are available via template expressions.
    /// </summary>
    public List<string> InputVars { get; set; } = [];
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

    /// <summary>
    /// Names of other subtasks that must complete before this one is dispatched.
    /// Completed subtask results are injected into the template vars as <c>{name}_result</c>.
    /// When empty (default), the subtask runs in the first parallel wave with no prerequisites.
    /// </summary>
    public List<string> DependsOn { get; set; } = [];

    public string? PromptTemplate { get; set; }

    /// <summary>
    /// Variable name under which this subtask's result is stored in the shared vars dict.
    /// Set from the skill's <c>output_var</c> parameter by WorkflowExpander.
    /// Falls back to <c>{Name}_result</c> when not set.
    /// </summary>
    public string? OutputVar { get; set; }

    /// <summary>
    /// Skill parameters merged by WorkflowExpander (skill defaults ← object args).
    /// Injected as <c>parameters</c> into the Scriban rendering context so that
    /// skill prompt templates can reference <c>{{parameters.required_phases}}</c> etc.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = [];
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

    /// <summary>
    /// For use in <c>outputs:</c> list only. Names a subtask result variable to write
    /// (e.g. "evaluator_result"). When null, the synthesised output is written instead.
    /// </summary>
    public string? Source { get; set; }
}
