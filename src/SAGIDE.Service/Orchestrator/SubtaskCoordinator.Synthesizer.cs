using SAGIDE.Contracts;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Synthesis, output writing, and model-spec parsing for <see cref="SubtaskCoordinator"/>.
/// Renders the final synthesis template (or aggregates subtask results), writes output
/// files, and resolves model spec strings into provider + model ID + endpoint tuples.
/// </summary>
public sealed partial class SubtaskCoordinator
{
    // ── Synthesis ───────────────────────────────────────────────────────────────

    private static string RenderSynthesisOrAggregate(
        PromptDefinition prompt,
        Dictionary<string, object> vars,
        Dictionary<string, string> subtaskResults)
    {
        if (prompt.Synthesis?.PromptTemplate is not null)
        {
            try { return PromptTemplate.RenderSynthesis(prompt, vars); }
            catch (Exception) { /* fall through to aggregation */ }
        }

        var sb = new System.Text.StringBuilder();
        foreach (var (name, output) in subtaskResults)
        {
            sb.AppendLine($"## {name}");
            sb.AppendLine(output);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Output writing ──────────────────────────────────────────────────────────

    private async Task WriteOutputAsync(
        string destinationTemplate,
        string content,
        Dictionary<string, object> vars)
    {
        try
        {
            var path = ExpandPath(ResolveSimpleTemplate(destinationTemplate, vars));
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(path, content);
            _logger.LogInformation("Output written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write output to '{Dest}'", destinationTemplate);
        }
    }

    // ── Model spec parsing ───────────────────────────────────────────────────────

    private string? ResolveCapability(string capabilityName) =>
        _config[$"SAGIDE:Routing:Capabilities:{capabilityName}"];

    /// <summary>
    /// Parses a model spec such as "ollama/deepseek-r1:14b@mini" into (provider, modelId, endpoint).
    /// Supports capability: prefix, @machine suffix, and provider prefixes (ollama/, claude, codex/, gemini/).
    /// </summary>
    private (ModelProvider Provider, string ModelId, string? Endpoint) ParseModelSpec(string spec)
    {
        string? endpoint = null;

        if (spec.StartsWith("capability:", StringComparison.OrdinalIgnoreCase))
        {
            var capName  = spec[11..].Trim();
            var resolved = ResolveCapability(capName);
            if (!string.IsNullOrWhiteSpace(resolved))
                spec = resolved;
            else
                _logger.LogWarning("Capability '{Cap}' not found in SAGIDE:Routing:Capabilities — using spec as-is", capName);
        }

        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            var machine = spec[(atIdx + 1)..].Trim();
            spec     = spec[..atIdx].Trim();
            endpoint = ResolveServerUrl(machine);

            if (endpoint is null)
                _logger.LogWarning(
                    "Machine '{Machine}' not found in configured servers — request will use health-monitor routing",
                    machine);
        }

        if (spec.StartsWith("ollama/",  StringComparison.OrdinalIgnoreCase)) return (ModelProvider.Ollama,  spec[7..],                  endpoint);
        if (spec.StartsWith("claude",   StringComparison.OrdinalIgnoreCase)) return (ModelProvider.Claude,  spec,                       endpoint);
        if (spec.StartsWith("gemini/",  StringComparison.OrdinalIgnoreCase)) return (ModelProvider.Gemini,  spec[7..],                  endpoint);
        if (spec.StartsWith("codex/",   StringComparison.OrdinalIgnoreCase) ||
            spec.StartsWith("openai/",  StringComparison.OrdinalIgnoreCase))
        {
            var slash = spec.IndexOf('/');
            return (ModelProvider.Codex, spec[(slash + 1)..], endpoint);
        }

        return (ModelProvider.Ollama, spec, endpoint);
    }

    private string? ResolveServerUrl(string machineName)
    {
        foreach (var server in _config.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        }
        foreach (var server in _config.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren())
        {
            if (string.Equals(server["Name"], machineName, StringComparison.OrdinalIgnoreCase))
                return server["BaseUrl"];
        }
        return null;
    }
}
