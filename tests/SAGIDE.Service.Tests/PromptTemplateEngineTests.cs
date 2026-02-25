using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

public class PromptTemplateEngineTests
{
    // ── Context variable resolution ───────────────────────────────────────────

    [Fact]
    public void Resolve_ContextVariable_Substituted()
    {
        var result = Resolve("Hello {{name}}!", context: new() { ["name"] = "world" });
        Assert.Equal("Hello world!", result);
    }

    [Fact]
    public void Resolve_MultipleContextVariables_AllSubstituted()
    {
        var result = Resolve(
            "Model: {{model}}, Task: {{task}}",
            context: new() { ["model"] = "llama3.2", ["task"] = "review" });
        Assert.Equal("Model: llama3.2, Task: review", result);
    }

    [Fact]
    public void Resolve_UnsetContextVariable_PlaceholderInserted()
    {
        var result = Resolve("Hello {{missing}}!", context: []);
        Assert.Equal("Hello [missing: not set]!", result);
    }

    // ── Step output resolution ────────────────────────────────────────────────

    [Fact]
    public void Resolve_StepOutput_Substituted()
    {
        var steps = Steps("step1", output: "the result");
        var result = Resolve("Summary: {{step1.output}}", steps: steps);
        Assert.Equal("Summary: the result", result);
    }

    [Fact]
    public void Resolve_StepOutput_NullOutput_EmptyString()
    {
        var step = new WorkflowStepExecution { StepId = "step1", Output = null };
        var result = Resolve("{{step1.output}}", steps: new() { ["step1"] = step });
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Resolve_StepOutput_LongOutput_Truncated()
    {
        var longOutput = new string('x', 5000);
        var steps = Steps("s1", output: longOutput);
        var result = Resolve("{{s1.output}}", steps: steps);

        Assert.Contains("...[truncated]", result);
        Assert.True(result.Length < 5000);
    }

    [Fact]
    public void Resolve_StepOutput_ExactlyAtLimit_NotTruncated()
    {
        var exactOutput = new string('a', 4000);
        var steps = Steps("s1", output: exactOutput);
        var result = Resolve("{{s1.output}}", steps: steps);

        Assert.Equal(exactOutput, result);
        Assert.DoesNotContain("truncated", result);
    }

    [Fact]
    public void Resolve_StepExitCode_Substituted()
    {
        var step = new WorkflowStepExecution { StepId = "lint", ExitCode = 0 };
        var result = Resolve("Exit: {{lint.exit_code}}", steps: new() { ["lint"] = step });
        Assert.Equal("Exit: 0", result);
    }

    [Fact]
    public void Resolve_StepExitCode_NullExitCode_Placeholder()
    {
        var step = new WorkflowStepExecution { StepId = "lint", ExitCode = null };
        var result = Resolve("{{lint.exit_code}}", steps: new() { ["lint"] = step });
        Assert.Equal("[no exit code]", result);
    }

    [Fact]
    public void Resolve_StepIssueCount_Substituted()
    {
        var step = new WorkflowStepExecution { StepId = "review", IssueCount = 3 };
        var result = Resolve("Issues: {{review.issue_count}}", steps: new() { ["review"] = step });
        Assert.Equal("Issues: 3", result);
    }

    [Fact]
    public void Resolve_StepStatus_Substituted_Lowercase()
    {
        var step = new WorkflowStepExecution { StepId = "s1", Status = WorkflowStepStatus.Completed };
        var result = Resolve("Status: {{s1.status}}", steps: new() { ["s1"] = step });
        Assert.Equal("Status: completed", result);
    }

    // ── Unknown step / field ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownStep_NotAvailablePlaceholder()
    {
        var result = Resolve("{{missing_step.output}}", steps: []);
        Assert.Equal("[missing_step.output: not available]", result);
    }

    [Fact]
    public void Resolve_UnknownField_UnknownFieldPlaceholder()
    {
        var step = new WorkflowStepExecution { StepId = "s1" };
        var result = Resolve("{{s1.unknown_field}}", steps: new() { ["s1"] = step });
        Assert.Equal("[s1.unknown_field: unknown field]", result);
    }

    // ── Hyphenated step IDs ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_HyphenatedStepId_Substituted()
    {
        var steps = Steps("generate-code", output: "console.log('hi')");
        var result = Resolve("Code: {{generate-code.output}}", steps: steps);
        Assert.Equal("Code: console.log('hi')", result);
    }

    // ── No-op cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoTemplateVars_ReturnedUnchanged()
    {
        const string template = "Plain text with no variables.";
        var result = Resolve(template);
        Assert.Equal(template, result);
    }

    [Fact]
    public void Resolve_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Resolve(string.Empty));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Resolve(
        string template,
        Dictionary<string, string>? context = null,
        Dictionary<string, WorkflowStepExecution>? steps = null)
        => PromptTemplateEngine.Resolve(
            template,
            context ?? [],
            steps   ?? []);

    private static Dictionary<string, WorkflowStepExecution> Steps(string id, string? output = null)
        => new() { [id] = new WorkflowStepExecution { StepId = id, Output = output } };
}
