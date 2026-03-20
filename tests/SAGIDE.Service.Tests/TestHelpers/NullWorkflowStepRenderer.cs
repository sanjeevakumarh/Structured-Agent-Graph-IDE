using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Tests;

/// <summary>
/// No-op <see cref="IWorkflowStepRenderer"/> for unit tests that don't
/// exercise the prompt-rendering code path.
/// Returns the template unchanged so test assertions can verify the raw template text.
/// </summary>
internal sealed class NullWorkflowStepRenderer : IWorkflowStepRenderer
{
    public string RenderStep(
        string template,
        Dictionary<string, string> inputContext,
        Dictionary<string, WorkflowStepExecution> stepExecutions,
        int maxOutputChars)
        => template;
}
