using SAGIDE.Contracts;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Template and path helpers extracted from <see cref="SubtaskCoordinator"/>.
/// Variable context building, path resolution, slug derivation, and collection parsing.
/// </summary>
public sealed partial class SubtaskCoordinator
{
    // ── Variable context ────────────────────────────────────────────────────────

    private static Dictionary<string, object> BuildVarContext(
        PromptDefinition prompt,
        Dictionary<string, string>? overrides)
    {
        var now = DateTime.UtcNow;
        var weekStart = now.AddDays(-(int)now.DayOfWeek);
        var weekEnd   = weekStart.AddDays(6);
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"]          = now.ToString("yyyy-MM-dd"),
            ["datestamp"]     = now.ToString("yyyy-MM-dd-HH-mm"),
            ["datetime"]      = now.ToString("O"),
            ["today"]         = now.ToString("MMMM d, yyyy"),
            ["current_year"]  = now.ToString("yyyy"),
            ["current_month"] = now.ToString("MMMM yyyy"),
            ["current_week"]  = $"{weekStart:MMMM d}–{weekEnd:MMMM d}, {now:yyyy}",
        };

        foreach (var kv in prompt.Variables)
            vars[kv.Key] = kv.Value;

        if (overrides is not null)
            foreach (var kv in overrides)
                vars[kv.Key] = kv.Value;

        if (vars.TryGetValue("topic", out var topicVal))
            vars["topic_slug"] = BuildTopicSlug(topicVal?.ToString() ?? string.Empty);

        if (vars.TryGetValue("ticker", out var tickerVal))
            vars["ticker_upper"] = tickerVal?.ToString()?.ToUpperInvariant() ?? string.Empty;

        if (prompt.ModelPreference is not null)
        {
            var mp = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (prompt.ModelPreference.Primary is not null)
                mp["primary"] = prompt.ModelPreference.Primary;
            if (prompt.ModelPreference.Fallback is not null)
                mp["fallback"] = prompt.ModelPreference.Fallback;
            if (prompt.ModelPreference.Orchestrator is not null)
                mp["orchestrator"] = prompt.ModelPreference.Orchestrator;
            if (prompt.ModelPreference.Subtasks.Count > 0)
                mp["subtasks"] = prompt.ModelPreference.Subtasks
                    .ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase);
            vars["model_preference"] = mp;
        }

        return vars;
    }

    // ── Trace folder helpers ─────────────────────────────────────────────────────

    private string? ComputeTraceFolderPath(PromptDefinition prompt, Dictionary<string, object> vars)
    {
        var dest = prompt.Output?.Destination
                   ?? prompt.Outputs.FirstOrDefault(o => !string.IsNullOrEmpty(o.Destination))?.Destination;
        if (string.IsNullOrEmpty(dest)) return null;

        var rendered = ExpandPath(ResolveSimpleTemplate(dest, vars));
        var dir      = Path.GetDirectoryName(rendered);
        var baseName = Path.GetFileNameWithoutExtension(rendered);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

        return Path.Combine(dir, baseName);
    }

    // ── Template helpers ────────────────────────────────────────────────────────

    private static string ResolveSimpleTemplate(string template, Dictionary<string, object> vars) =>
        PromptTemplate.RenderRaw(template, vars);

    /// <summary>
    /// Derives a file-safe slug from a topic string: up to 4 meaningful keywords joined by underscores.
    /// </summary>
    private static string BuildTopicSlug(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "digest";

        var words = topic.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();

        var keywords = words.Where(w => !_slugStopWords.Contains(w)).Take(4).ToList();

        if (keywords.Count < 2)
            keywords = words.Take(4).ToList();

        return keywords.Count > 0 ? string.Join("_", keywords) : "digest";
    }

    private static IEnumerable<string> ResolveCollection(string expression, Dictionary<string, object> vars)
    {
        var varName = ExtractLeadingVarName(expression);
        if (!string.IsNullOrEmpty(varName) && vars.TryGetValue(varName, out var val))
        {
            if (val is IEnumerable<string> list) return list;
            var str = val?.ToString() ?? string.Empty;
            return str.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Where(s => !s.Equals("symbol", StringComparison.OrdinalIgnoreCase));
        }

        var resolved = ResolveSimpleTemplate(expression, vars);
        return string.IsNullOrWhiteSpace(resolved) ? [] : [resolved];
    }

    private static string ExtractLeadingVarName(string template)
    {
        var start = template.IndexOf("{{", StringComparison.Ordinal);
        var end   = template.IndexOf("}}", StringComparison.Ordinal);
        if (start < 0 || end < 0) return template.Trim();
        var inner = template[(start + 2)..end].Trim();
        var dot   = inner.IndexOf('.');
        return dot > 0 ? inner[..dot] : inner;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~/") || path == "~")
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.Length > 2 ? path[2..] : string.Empty);
        return path;
    }
}
