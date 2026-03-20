namespace SAGIDE.Core.Models;

/// <summary>Per-agent-type iteration limits for the workflow engine.</summary>
public class AgentLimitsConfig
{
    public Dictionary<string, AgentLimitEntry> Agents { get; init; } = new()
    {
        ["CodeReview"]    = new(),
        ["TestGeneration"]= new(),
        ["Refactoring"]   = new() { MaxIterations = 5 },
        ["Debug"]         = new(),
        ["Documentation"] = new(),
        ["SecurityReview"]= new(),
    };

    public int GetMaxIterations(AgentType agentType)
        => Agents.TryGetValue(agentType.ToString(), out var entry) ? entry.MaxIterations : 5;
}

public class AgentLimitEntry
{
    public int MaxIterations { get; init; } = 5;
}
