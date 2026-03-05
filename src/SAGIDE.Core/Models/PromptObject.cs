namespace SAGIDE.Core.Models;

/// <summary>
/// Phase 5: A named instance of a skill declared in the <c>objects:</c> section of a workflow.
/// Analogous to a variable declaration in OOP: <c>market: WebResearchTrack(focus: "market sizing")</c>.
/// </summary>
public class PromptObject
{
    /// <summary>Local instance name used in the workflow sequence (e.g. "market", "analyst").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Skill reference — "domain/name" or just "name" (resolved by SkillRegistry).</summary>
    public string Skill { get; set; } = string.Empty;

    /// <summary>Constructor arguments that override the skill's default parameters.</summary>
    public Dictionary<string, object> Args { get; set; } = [];

    /// <summary>
    /// When true, an empty output from this object's collect/search step does NOT abort the run.
    /// Use for search tracks where zero results is a legitimate outcome (e.g. novel product ideas
    /// with no comparable companies). A <c>missing_data_summary</c> var is injected into the run
    /// context so downstream steps can acknowledge what is unavailable.
    /// </summary>
    public bool Optional { get; set; }
}

/// <summary>
/// Phase 5: A single step in the <c>workflow:</c> call sequence.
/// Either a single method call ("market.collect") or a parallel group.
/// </summary>
public class PromptWorkflowCall
{
    /// <summary>
    /// Single method call in "object.method" notation (e.g. "market.collect", "analyst.analyze").
    /// Mutually exclusive with <see cref="Parallel"/>.
    /// </summary>
    public string? Call { get; set; }

    /// <summary>
    /// List of method calls to execute in parallel (e.g. ["market.collect", "tech.collect"]).
    /// Mutually exclusive with <see cref="Call"/>.
    /// </summary>
    public List<string> Parallel { get; set; } = [];

    /// <summary>Runtime arguments passed to the method call (merged over object's args).</summary>
    public Dictionary<string, object> Args { get; set; } = [];
}
