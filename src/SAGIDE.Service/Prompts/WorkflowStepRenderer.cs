using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Adapter implementing <see cref="IWorkflowStepRenderer"/> via <see cref="PromptTemplate.RenderWorkflowStep"/>.
/// Registered in DI so <c>SAGIDE.Workflows</c> can receive it without referencing <c>SAGIDE.Service.Prompts</c>.
/// </summary>
public sealed class WorkflowStepRenderer : IWorkflowStepRenderer
{
    public string RenderStep(
        string template,
        Dictionary<string, string> inputContext,
        Dictionary<string, WorkflowStepExecution> stepExecutions,
        int maxOutputChars)
        => PromptTemplate.RenderWorkflowStep(template, inputContext, stepExecutions, maxOutputChars);
}
