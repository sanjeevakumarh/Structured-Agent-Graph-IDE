using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Sends search queries to a SearXNG instance and returns formatted result text.
/// <para>
/// Search URLs are collected from all <c>Ollama:Servers</c> entries that have a
/// numeric <c>RagOrder</c> field and a non-empty <c>SearchUrl</c>, sorted by
/// <c>RagOrder</c> ascending (0 = primary, 1 = first fallback, …).
/// Each query tries them in sequence and returns the first successful result.
/// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended as a final fallback.
/// </para>
/// </summary>
public sealed class WebSearchAdapter
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _searchUrls;
    private readonly ILogger<WebSearchAdapter> _logger;

    // In-memory query cache: query → (result, fetchedAt)
    private readonly Dictionary<string, (string result, DateTime fetchedAt)> _cache = [];
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(30);

    public WebSearchAdapter(HttpClient http, IConfiguration configuration, ILogger<WebSearchAdapter> logger)
    {
        _http       = http;
        _searchUrls = ResolveSearchUrls(configuration);
        _logger     = logger;
    }

    /// <summary>
    /// Collects SearXNG URLs from servers that have a numeric <c>RagOrder</c> and a
    /// non-empty <c>SearchUrl</c>, sorted ascending by <c>RagOrder</c>.
    /// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended last.
    /// </summary>
    private static IReadOnlyList<string> ResolveSearchUrls(IConfiguration cfg)
    {
        var ordered = new List<(int order, string url)>();

        foreach (var server in cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            if (!int.TryParse(server["RagOrder"], out var order))
                continue; // no RagOrder → inference-only server

            var url = server["SearchUrl"]?.TrimEnd('/');
            // Strip /search path suffix in case the URL was misconfigured with it included
            if (url?.EndsWith("/search", StringComparison.OrdinalIgnoreCase) == true)
                url = url[..^7];
            if (!string.IsNullOrWhiteSpace(url))
                ordered.Add((order, url!));
        }

        var urls = ordered
            .OrderBy(x => x.order)
            .Select(x => x.url)
            .ToList();

        // Legacy flat key — appended last as final fallback
        var legacy = cfg["SAGIDE:Rag:SearchUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(legacy) && !urls.Contains(legacy))
            urls.Add(legacy);

        return urls;
    }

    /// <summary>Returns true if at least one SearXNG URL is configured.</summary>
    public bool IsConfigured => _searchUrls.Count > 0;

    /// <summary>
    /// Searches for <paramref name="query"/> and returns a formatted string of top results.
    /// Tries each configured SearXNG URL in order; returns the first successful result.
    /// Result format: numbered list of "Title\nURL\nSnippet\n".
    /// Returns empty string if unconfigured or all endpoints fail.
    /// </summary>
    public async Task<string> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        if (!IsConfigured)
        {
            _logger.LogWarning("web_search_batch: no SearXNG URL configured — skipping query '{Query}'", query);
            return string.Empty;
        }

        // Cache hit
        var cacheKey = $"{query}|{maxResults}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < _cacheTtl)
        {
            return cached.result;
        }

        var encodedQuery = Uri.EscapeDataString(query);

        foreach (var baseUrl in _searchUrls)
        {
            try
            {
                var url = $"{baseUrl}/search?q={encodedQuery}&format=json&categories=general";
                using var response = await _http.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SearXNG at {BaseUrl} returned {Status} for query '{Query}' — trying next",
                        baseUrl, (int)response.StatusCode, query);
                    continue;
                }

                var json   = await response.Content.ReadAsStringAsync(ct);
                var result = ParseSearxngResponse(json, maxResults);

                _cache[cacheKey] = (result, DateTime.UtcNow);
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SearXNG at {BaseUrl} failed for query '{Query}' — trying next", baseUrl, query);
            }
        }

        _logger.LogWarning("All SearXNG endpoints failed for query '{Query}'", query);
        return string.Empty;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static string ParseSearxngResponse(string json, int maxResults)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var resultsEl))
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            var count = 0;

            foreach (var result in resultsEl.EnumerateArray())
            {
                if (count >= maxResults) break;

                var title   = GetStr(result, "title")   ?? "(no title)";
                var url     = GetStr(result, "url")     ?? string.Empty;
                var snippet = GetStr(result, "content") ?? string.Empty;

                sb.AppendLine($"[{count + 1}] {title}");
                if (!string.IsNullOrEmpty(url))     sb.AppendLine($"    URL: {url}");
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"    {snippet}");
                sb.AppendLine();

                count++;
            }

            return sb.ToString().TrimEnd();
        }
        catch
        {
            return json; // return raw on parse failure
        }
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
