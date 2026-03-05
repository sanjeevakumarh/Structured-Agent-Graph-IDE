using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;

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
    public void Resolve_UnsetContextVariable_EmptyString()
    {
        // Scriban emits empty string for variables not present in the context.
        var result = Resolve("Hello {{missing}}!", context: []);
        Assert.Equal("Hello !", result);
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
    public void Resolve_UnknownStep_ReturnsTemplateUnchanged()
    {
        // Scriban throws when accessing a member on an undefined variable;
        // RenderWorkflowStep catches and returns the original template text.
        const string template = "{{missing_step.output}}";
        var result = Resolve(template, steps: []);
        Assert.Equal(template, result);
    }

    [Fact]
    public void Resolve_UnknownField_EmptyString()
    {
        // Scriban emits empty string for fields not set on the step ScriptObject.
        var step = new WorkflowStepExecution { StepId = "s1" };
        var result = Resolve("{{s1.unknown_field}}", steps: new() { ["s1"] = step });
        Assert.Equal(string.Empty, result);
    }

    // ── Step IDs with underscores (Scriban convention) ────────────────────────

    [Fact]
    public void Resolve_UnderscoreStepId_Substituted()
    {
        // Scriban uses underscores for identifiers; hyphenated IDs are not supported
        // (the minus sign is treated as arithmetic). All real workflow YAMLs use underscores.
        var steps = Steps("generate_code", output: "console.log('hi')");
        var result = Resolve("Code: {{generate_code.output}}", steps: steps);
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

    // ── Nested Dictionary access (model_preference) ────────────────────────

    [Fact]
    public void RenderRaw_NestedDictionary_ResolvesDeepKeys()
    {
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var mp = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["orchestrator"] = "ollama/qwen2.5:14b@workstation",
            ["subtasks"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["analyst"] = "ollama/llama3.1:8b@mini",
            },
        };
        vars["model_preference"] = mp;

        var result = PromptTemplate.RenderRaw("{{model_preference.subtasks.analyst}}", vars);
        Assert.Equal("ollama/llama3.1:8b@mini", result);
    }

    [Fact]
    public void RenderRaw_NestedDictionary_IfElseFallsThrough()
    {
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["analyst_model_override"] = "",
            ["model_preference"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["subtasks"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["analyst"] = "ollama/llama3.1:8b@mini",
                },
            },
        };

        // Call Scriban directly (bypass RenderRaw's silent catch) to see the raw exception
        var templateText = "{{ if analyst_model_override }}{{analyst_model_override}}{{ else }}LITERAL{{ end }}";
        var template = Scriban.Template.Parse(templateText);
        Assert.False(template.HasErrors, $"Parse errors: {string.Join("; ", template.Messages)}");

        var ctx = new Scriban.TemplateContext { MemberRenamer = m => m.Name };
        var globals = new Scriban.Runtime.ScriptObject();
        foreach (var kv in vars)
            globals[kv.Key] = kv.Value;
        ctx.PushGlobal(globals);

        var result = template.Render(ctx);

        // Scriban treats "" as TRUTHY — empty string passes {{ if var }}
        // Correct idiom: {{ if var != "" }}
        var fixedTemplate = Scriban.Template.Parse(
            "{{ if analyst_model_override != \"\" }}{{analyst_model_override}}{{ else }}LITERAL{{ end }}");
        var fixedResult = fixedTemplate.Render(ctx).Trim();

        Assert.Equal("", result.Trim());        // Confirms bug: "" is truthy → if-branch outputs ""
        Assert.Equal("LITERAL", fixedResult);   // Fix: explicit != "" check
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Resolve(
        string template,
        Dictionary<string, string>? context = null,
        Dictionary<string, WorkflowStepExecution>? steps = null)
        => PromptTemplate.RenderWorkflowStep(
            template,
            context ?? [],
            steps   ?? []);

    private static Dictionary<string, WorkflowStepExecution> Steps(string id, string? output = null)
        => new() { [id] = new WorkflowStepExecution { StepId = id, Output = output } };
}
