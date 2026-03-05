using Microsoft.Extensions.Configuration;

namespace SAGIDE.Service.Routing;

/// <summary>
/// Maps configured Ollama and OpenAI-compatible server BaseUrls to their logical aliases
/// (e.g., "http://workstation:11434" → "workstation"). Used for perf sample labelling
/// and optional log redaction so raw hostnames never appear in shared artifacts.
/// </summary>
public sealed class EndpointAliasResolver
{
    // Sorted longest-first to prevent partial substring replacements during Redact().
    private readonly IReadOnlyList<KeyValuePair<string, string>> _urlToAliasSorted;
    // Reverse map: alias → URL for Resolve()
    private readonly IReadOnlyDictionary<string, string> _aliasToUrl;
    private readonly bool _redactEnabled;

    public EndpointAliasResolver(IConfiguration cfg)
    {
        _redactEnabled = cfg.GetValue("SAGIDE:Logging:RedactModelEndpoints", true);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            var url   = server["BaseUrl"]?.TrimEnd('/');
            var alias = server["Name"];
            if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(alias))
                map[url] = alias;
        }
        foreach (var server in cfg.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren())
        {
            var url   = server["BaseUrl"]?.TrimEnd('/');
            var alias = server["Name"];
            if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(alias))
                map[url] = alias;
        }

        // Sort longest URL first so "http://host:11434/prefix" matches before "http://host:11434"
        _urlToAliasSorted = [.. map.OrderByDescending(kv => kv.Key.Length)];

        // Build reverse map for alias→URL lookups (used by QualitySampler and others)
        _aliasToUrl = map.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Returns the BaseUrl for <paramref name="alias"/>, or null if not configured.</summary>
    public string? Resolve(string? alias)
    {
        if (string.IsNullOrEmpty(alias)) return null;
        return _aliasToUrl.TryGetValue(alias, out var url) ? url : null;
    }

    /// <summary>Returns the alias for <paramref name="baseUrl"/>, or "unknown" if not configured.</summary>
    public string GetAlias(string? baseUrl)
    {
        if (string.IsNullOrEmpty(baseUrl)) return "unknown";
        var normalized = baseUrl.TrimEnd('/');
        foreach (var (url, alias) in _urlToAliasSorted)
            if (string.Equals(url, normalized, StringComparison.OrdinalIgnoreCase))
                return alias;
        return "unknown";
    }

    /// <summary>
    /// When <c>SAGIDE:Logging:RedactModelEndpoints</c> is true (default), replaces all known
    /// BaseUrl occurrences in <paramref name="text"/> with <c>[alias]</c>.
    /// When disabled, returns the original text unchanged.
    /// </summary>
    public string Redact(string text)
    {
        if (!_redactEnabled || string.IsNullOrEmpty(text)) return text;
        foreach (var (url, alias) in _urlToAliasSorted)
            text = text.Replace(url, $"[{alias}]", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
