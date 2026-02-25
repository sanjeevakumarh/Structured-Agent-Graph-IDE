namespace SAGIDE.Core.Models;

public enum AgentType
{
    CodeReview,
    TestGeneration,
    Refactoring,
    Debug,
    Documentation,
    SecurityReview,
    /// <summary>
    /// General-purpose agent for prompt-driven tasks (scheduler, subtask coordinator).
    /// The task description is passed directly to the model without wrapping.
    /// </summary>
    Generic,
}
