namespace SAGIDE.Core.Models;

/// <summary>
/// Deserialized representation of a skill YAML file from the skills/ directory.
/// Skills are named, versioned, reusable bundles of data-collection step primitives
/// with typed input/output contracts. They are the "methods" in the OOP workflow model.
/// </summary>
public class SkillDefinition
{
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; } = 1;
    public string Domain { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Protocol names this skill satisfies (e.g. "Collectible", "Analyzable").</summary>
    public List<string> ProtocolImplements { get; set; } = [];

    /// <summary>Typed input parameter declarations. Each key is a parameter name.</summary>
    public Dictionary<string, object> Inputs { get; set; } = [];

    /// <summary>
    /// JSON Schema describing this skill's output. This is the stable interface contract —
    /// changing outputs_schema is a breaking change (bump version).
    /// Changing only the implementation prompt is not a breaking change.
    /// </summary>
    public Dictionary<string, object> OutputsSchema { get; set; } = [];

    /// <summary>
    /// Named capability slots resolved by routing config at runtime.
    /// Skills declare what they need; appsettings.json maps needs to actual model@machine specs.
    /// Referenced in implementation prompts via {{capability.slot_name}}.
    /// </summary>
    public Dictionary<string, SkillCapabilityRequirement> CapabilityRequirements { get; set; } = [];

    /// <summary>
    /// Default values for implementation template variables.
    /// Callers override via the parameters: block in their data_collection step reference.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = [];

    /// <summary>
    /// Ordered list of data_collection step primitives that implement this skill.
    /// Same step types as in prompt data_collection (read_file, web_search_batch, llm_queries, etc.).
    /// </summary>
    public List<PromptDataCollectionStep> Implementation { get; set; } = [];

    /// <summary>Absolute path of the YAML file this was loaded from. Set by SkillRegistry.</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// A named capability slot within a skill. The skill declares what model qualities it needs;
/// the routing infrastructure resolves which actual model@machine satisfies the requirement.
/// </summary>
public class SkillCapabilityRequirement
{
    /// <summary>Required model capabilities (e.g. "deep_reasoning", "long_context_understanding").</summary>
    public List<string> Needs { get; set; } = [];

    /// <summary>Minimum context window required (e.g. "8k", "32k").</summary>
    public string? ContextWindowMin { get; set; }
}
