using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Renders prompt templates for workflow steps.
/// Abstracts <c>PromptTemplate.RenderWorkflowStep</c> so <c>SAGIDE.Workflows</c>
/// has no direct reference to <c>SAGIDE.Service.Prompts</c>.
/// </summary>
public interface IWorkflowStepRenderer
{
    /// <summary>
    /// Renders a Scriban template string for a workflow step, substituting
    /// <paramref name="inputContext"/> and step execution outputs.
    /// </summary>
    string RenderStep(
        string template,
        Dictionary<string, string> inputContext,
        Dictionary<string, WorkflowStepExecution> stepExecutions,
        int maxOutputChars);
}
