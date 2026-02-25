using Scriban;
using Scriban.Runtime;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Renders a PromptDefinition's template strings using Scriban (Liquid-compatible).
/// Automatically injects <c>date</c> and <c>datetime</c> variables.
/// </summary>
public static class PromptTemplate
{
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

    // ── Core render helper ────────────────────────────────────────────────────

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
        globals["date"]     = now.ToString("yyyy-MM-dd");
        globals["datetime"] = now.ToString("O");

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
