namespace SAGIDE.Core.Models;

/// <summary>Model affinities per agent type — WorkflowEngine fallback model selection.</summary>
public class TaskAffinityEntry
{
    public string LocalModel    { get; init; } = string.Empty;
    public string CloudProvider { get; init; } = string.Empty;
    public string CloudModel    { get; init; } = string.Empty;
}

public class TaskAffinitiesConfig
{
    public Dictionary<string, TaskAffinityEntry> Affinities { get; init; } = new();

    /// <summary>
    /// Returns (provider, modelId) for the given agent type.
    /// preferLocal=true selects the Ollama model; otherwise the cloud model is returned.
    /// </summary>
    public (string Provider, string ModelId) GetDefaultFor(AgentType agentType, bool preferLocal = false)
    {
        if (Affinities.TryGetValue(agentType.ToString(), out var entry))
        {
            if (preferLocal && !string.IsNullOrEmpty(entry.LocalModel))
                return ("Ollama", entry.LocalModel);
            if (!string.IsNullOrEmpty(entry.CloudModel))
                return (entry.CloudProvider, entry.CloudModel);
        }
        return (string.Empty, string.Empty);
    }
}
