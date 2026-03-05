using Scriban;
using Scriban.Runtime;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Single Scriban-based rendering surface for all template types in the system.
/// Replaces the custom Regex engine (PromptTemplateEngine) and the ad-hoc string.Replace
/// used in SubtaskCoordinator.
/// </summary>
public static class PromptTemplate
{
    /// <summary>
    /// Maximum characters of step output included when rendering {{step_id.output}} in workflow
    /// step prompts. Set once at application startup via <see cref="Configure"/> from
    /// SAGIDE:Orchestration:MaxStepOutputChars (default 4000).
    /// </summary>
    public static int MaxOutputChars { get; set; } = 4000;

    /// <summary>Applies startup configuration. Call once from Program.cs after building config.</summary>
    public static void Configure(int maxOutputChars) => MaxOutputChars = maxOutputChars;

    /// <summary>
    /// Renders the main <c>prompt_template</c> of a prompt definition.
    /// </summary>
    /// <param name="definition">The prompt to render.</param>
    /// <param name="variables">
    /// Runtime variables that override or extend those declared in the YAML.
    /// Values may be strings, numbers, lists, or dictionaries.
    /// </param>
    public static string Render(PromptDefinition definition, Dictionary<string, object>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(definition.PromptTemplate))
            return string.Empty;

        return RenderTemplate(definition.PromptTemplate, definition, variables);
    }

    /// <summary>
    /// Renders a subtask's <c>prompt_template</c> field.
    /// </summary>
    public static string RenderSubtask(PromptSubtask subtask, PromptDefinition parent, Dictionary<string, object>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(subtask.PromptTemplate))
            return string.Empty;

        return RenderTemplate(subtask.PromptTemplate, parent, variables);
    }

    /// <summary>
    /// Renders the synthesis <c>prompt_template</c> field.
    /// </summary>
    public static string RenderSynthesis(PromptDefinition definition, Dictionary<string, object>? variables = null)
    {
        if (string.IsNullOrWhiteSpace(definition.Synthesis?.PromptTemplate))
            return string.Empty;

        return RenderTemplate(definition.Synthesis.PromptTemplate, definition, variables);
    }

    /// <summary>
    /// Renders a raw template string with a flat variable dictionary.
    /// Used by <see cref="SAGIDE.Service.Orchestrator.SubtaskCoordinator"/> for data-collection
    /// step templates (source URLs, query strings, step prompts).
    /// On parse or render errors the original template is returned unchanged.
    /// </summary>
    public static string RenderRaw(string templateText, Dictionary<string, object> vars)
    {
        if (string.IsNullOrWhiteSpace(templateText) || !templateText.Contains("{{"))
            return templateText;

        try
        {
            var template = Template.Parse(templateText);
            if (template.HasErrors) return templateText;

            var ctx = new TemplateContext { MemberRenamer = m => m.Name };
            var globals = new ScriptObject();
            foreach (var kv in vars)
                globals[kv.Key] = kv.Value;

            ctx.PushGlobal(globals);
            return template.Render(ctx) ?? templateText;
        }
        catch
        {
            return templateText;
        }
    }

    /// <summary>
    /// Renders a workflow step prompt using Scriban.
    /// Exposes:
    ///   <list type="bullet">
    ///     <item><description><c>inputContext</c> variables as top-level identifiers.</description></item>
    ///     <item><description>Each <c>stepExecutions</c> entry as <c>step_id.output</c>,
    ///       <c>step_id.exit_code</c>, <c>step_id.issue_count</c>, <c>step_id.status</c>.</description></item>
    ///   </list>
    /// Step output is truncated to <paramref name="maxOutputChars"/> characters.
    /// On parse or render errors the original template is returned unchanged (graceful degradation).
    /// </summary>
    public static string RenderWorkflowStep(
        string templateText,
        Dictionary<string, string> inputContext,
        Dictionary<string, WorkflowStepExecution> stepExecutions,
        int maxOutputChars = 4000)
    {
        if (string.IsNullOrWhiteSpace(templateText) || !templateText.Contains("{{"))
            return templateText;

        try
        {
            var template = Template.Parse(templateText);
            if (template.HasErrors) return templateText;

            var now = DateTime.UtcNow;
            var ctx = new TemplateContext { MemberRenamer = m => m.Name };
            var globals = new ScriptObject();

            globals["date"]      = now.ToString("yyyy-MM-dd");
            globals["datestamp"] = now.ToString("yyyy-MM-dd-HH-mm");
            globals["datetime"]  = now.ToString("O");

            // Flat context variables (user-supplied InputContext)
            foreach (var kv in inputContext)
                globals[kv.Key] = kv.Value;

            // Step execution results — accessible as {{ step_id.output }}, {{ step_id.exit_code }}, etc.
            foreach (var kv in stepExecutions)
            {
                var rawOutput = kv.Value.Output ?? string.Empty;
                var stepObj   = new ScriptObject();
                stepObj["output"] = rawOutput.Length > maxOutputChars
                    ? string.Concat(rawOutput.AsSpan(0, maxOutputChars), "\n...[truncated]")
                    : rawOutput;
                stepObj["exit_code"]   = kv.Value.ExitCode?.ToString() ?? "[no exit code]";
                stepObj["issue_count"] = kv.Value.IssueCount.ToString();
                stepObj["status"]      = kv.Value.Status.ToString().ToLowerInvariant();
                globals[kv.Key]        = stepObj;
            }

            ctx.PushGlobal(globals);
            return template.Render(ctx) ?? templateText;
        }
        catch
        {
            return templateText;
        }
    }

    // ── Core render helper (for PromptDefinition-backed templates) ────────────

    private static string RenderTemplate(
        string templateText,
        PromptDefinition definition,
        Dictionary<string, object>? overrides)
    {
        var template = Template.Parse(templateText);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"Template parse errors in '{definition.Name}': " +
                string.Join("; ", template.Messages.Select(m => m.Message)));

        var ctx = new TemplateContext { MemberRenamer = m => m.Name };
        var globals = new ScriptObject();

        // Auto-inject date/time
        var now = DateTime.UtcNow;
        globals["date"]      = now.ToString("yyyy-MM-dd");
        globals["datestamp"] = now.ToString("yyyy-MM-dd-HH-mm");
        globals["datetime"]  = now.ToString("O");

        // Inject YAML-declared variables (lowest priority)
        foreach (var kv in definition.Variables)
            globals[kv.Key] = kv.Value;

        // Inject caller-supplied overrides (highest priority)
        if (overrides is not null)
            foreach (var kv in overrides)
                globals[kv.Key] = kv.Value;

        ctx.PushGlobal(globals);
        return template.Render(ctx);
    }
}
