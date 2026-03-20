using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Memory;

/// <summary>
/// Sends search queries to a SearXNG instance and returns formatted result text.
/// <para>
/// Search URLs are collected from all <c>Ollama:Servers</c> entries that have a
/// numeric <c>RagOrder</c> field and a non-empty <c>SearchUrl</c>, sorted by
/// <c>RagOrder</c> ascending (0 = primary, 1 = first fallback, …).
/// Each query tries them in sequence and returns the first successful result.
/// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended as a final fallback.
/// </para>
/// <para>
/// Results are persisted to SQLite via <see cref="ISearchCacheRepository"/> with per-domain
/// TTLs. Fresh results are scored by <see cref="SearchQualityScorer"/>; low-quality results
/// (captcha, bot walls) are rejected in favor of stale cached data when available.
/// </para>
/// </summary>
public sealed class WebSearchAdapter
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _searchUrls;
    private readonly ILogger<WebSearchAdapter> _logger;
    private readonly string? _engines;
    private readonly ISearchCacheRepository? _persistentCache;
    private readonly WebFetcher? _webFetcher;
    private readonly IReadOnlyDictionary<string, int> _domainTtlHours;
    private readonly int _defaultTtlHours;

    // In-memory query cache: query → (result, fetchedAt) — fast L1 cache over persistent L2
    private readonly Dictionary<string, (string result, DateTime fetchedAt)> _cache = [];
    private readonly TimeSpan _cacheTtl;

    /// <summary>Parsed search result with text, count, and extracted URLs.</summary>
    private readonly record struct SearchParseResult(string Text, int Count, IReadOnlyList<string> Urls);

    public WebSearchAdapter(HttpClient http, IConfiguration configuration, ILogger<WebSearchAdapter> logger,
        ISearchCacheRepository? persistentCache = null, WebFetcher? webFetcher = null)
    {
        _http            = http;
        _searchUrls      = ResolveSearchUrls(configuration);
        _logger          = logger;
        _engines         = configuration["SAGIDE:Rag:SearchEngines"];
        _persistentCache = persistentCache;
        _webFetcher      = webFetcher;
        var minutes      = configuration.GetValue("SAGIDE:Caching:SearchCacheTtlMinutes", 30);
        _cacheTtl        = minutes > 0 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero;

        // Per-domain TTL (hours): SAGIDE:Caching:SearchCacheTtlByDomain:finance = 24, etc.
        _defaultTtlHours = configuration.GetValue("SAGIDE:Caching:PersistentSearchCacheTtlHours", 4);
        var domainTtls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration.GetSection("SAGIDE:Caching:SearchCacheTtlByDomain").GetChildren())
        {
            if (int.TryParse(child.Value, out var hours))
                domainTtls[child.Key] = hours;
        }
        _domainTtlHours = domainTtls;
    }

    /// <summary>
    /// Collects SearXNG URLs from servers that have a numeric <c>RagOrder</c> and a
    /// non-empty <c>SearchUrl</c>, sorted ascending by <c>RagOrder</c>.
    /// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended last.
    /// </summary>
    private static IReadOnlyList<string> ResolveSearchUrls(IConfiguration cfg)
    {
        var ordered = new List<(int order, string url)>();

        // Scan both Ollama and OpenAI-compatible server sections for SearchUrl
        var allSections = new[]
        {
            cfg.GetSection("SAGIDE:Ollama:Servers"),
            cfg.GetSection("SAGIDE:OpenAICompatible:Servers")
        };

        foreach (var section in allSections)
        foreach (var server in section.GetChildren())
        {
            if (!int.TryParse(server["RagOrder"], out var order))
            {
                // Accept servers with SearchUrl but no RagOrder — append at end (order 999)
                var searchOnly = server["SearchUrl"]?.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(searchOnly))
                {
                    if (searchOnly.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
                        searchOnly = searchOnly[..^7];
                    ordered.Add((999, searchOnly));
                }
                continue;
            }

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
    /// <para>
    /// Cache strategy (L1 in-memory → L2 persistent SQLite → internet):
    /// <list type="number">
    ///   <item>L1 in-memory cache hit within TTL → return immediately</item>
    ///   <item>L2 persistent cache hit within domain TTL → return + populate L1</item>
    ///   <item>Fetch from SearXNG → score quality → accept or reject</item>
    ///   <item>If rejected and L2 has stale data with good quality → return stale</item>
    ///   <item>If rejected and no L2 → return fresh anyway with warning</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<string> SearchAsync(
        string query,
        int maxResults = 5,
        string? domain = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        if (!IsConfigured)
        {
            _logger.LogWarning("web_search_batch: no SearXNG URL configured — skipping query '{Query}'", query);
            return string.Empty;
        }

        var cacheKey = $"{query}|{maxResults}";
        var domainKey = domain ?? "default";
        var ttlHours = _domainTtlHours.TryGetValue(domainKey, out var h) ? h : _defaultTtlHours;

        // L1: in-memory cache hit
        if (_cacheTtl > TimeSpan.Zero
            && _cache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < _cacheTtl)
        {
            return cached.result;
        }

        // L2: persistent cache hit (within domain TTL)
        var queryHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}|{maxResults}")));
        SearchCacheEntry? persistedEntry = null;
        if (_persistentCache is not null)
        {
            persistedEntry = await _persistentCache.GetAsync(queryHash);
            if (persistedEntry is not null)
            {
                var age = DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt);
                if (age < TimeSpan.FromHours(ttlHours))
                {
                    _logger.LogDebug("Persistent cache hit for '{Query}' (age={Age:F1}h, domain={Domain})",
                        query, age.TotalHours, domainKey);
                    _cache[cacheKey] = (persistedEntry.ResultText, DateTime.UtcNow);
                    return persistedEntry.ResultText;
                }
            }
        }

        // L3: fetch from internet
        var freshParsed  = await FetchFromSearchEnginesAsync(query, maxResults, ct);
        var freshResult  = freshParsed.Text;
        var freshCount   = freshParsed.Count;

        if (string.IsNullOrEmpty(freshResult))
        {
            // Total failure — use stale cache if available
            if (persistedEntry is not null && persistedEntry.QualityScore >= SearchQualityScorer.AcceptThreshold)
            {
                _logger.LogWarning(
                    "All search engines failed for '{Query}' — using stale cache (age={Age})",
                    query, DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt));
                var staleResult = persistedEntry.ResultText + $"\n\n[Stale data from {persistedEntry.FetchedAt} — live search failed]";
                _cache[cacheKey] = (staleResult, DateTime.UtcNow);
                return staleResult;
            }
            return string.Empty;
        }

        // Score quality
        var (score, reason) = SearchQualityScorer.Score(freshResult, freshCount);

        if (score >= SearchQualityScorer.AcceptThreshold)
        {
            // Good fresh data — persist and return
            _cache[cacheKey] = (freshResult, DateTime.UtcNow);
            if (_persistentCache is not null)
            {
                await _persistentCache.UpsertAsync(new SearchCacheEntry(
                    queryHash, query, freshResult, freshCount, score, domainKey, DateTime.UtcNow.ToString("O")));
            }
            if (score < 0.5)
                _logger.LogDebug("Search result for '{Query}' has marginal quality (score={Score}, reason={Reason})",
                    query, score, reason);
            return freshResult;
        }

        // Bad fresh data — prefer stale cache
        _logger.LogWarning(
            "Fresh search rejected for '{Query}' (score={Score}, reason={Reason})",
            query, score, reason);

        if (persistedEntry is not null && persistedEntry.QualityScore >= SearchQualityScorer.AcceptThreshold)
        {
            _logger.LogInformation(
                "Using stale cache for '{Query}' (cached score={CachedScore}, age={Age})",
                query, persistedEntry.QualityScore,
                DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt));
            var staleResult = persistedEntry.ResultText +
                $"\n\n[Stale data from {persistedEntry.FetchedAt} — fresh search returned low-quality results ({reason})]";
            _cache[cacheKey] = (staleResult, DateTime.UtcNow);
            return staleResult;
        }

        // No good cache — return fresh with warning (better than nothing)
        _logger.LogWarning("No cached alternative for '{Query}' — returning low-quality results", query);
        if (_persistentCache is not null)
        {
            await _persistentCache.UpsertAsync(new SearchCacheEntry(
                queryHash, query, freshResult, freshCount, score, domainKey, DateTime.UtcNow.ToString("O")));
        }
        _cache[cacheKey] = (freshResult, DateTime.UtcNow);
        return freshResult + $"\n\n[Warning: search results may be low quality ({reason})]";
    }

    /// <summary>
    /// Searches for <paramref name="query"/>, then fetches the top <paramref name="fetchPages"/>
    /// result URLs, extracts text from the HTML, and appends page content to the search snippets.
    /// This gives the LLM actual page data instead of just meta-description snippets.
    /// </summary>
    public async Task<string> SearchWithPageContentAsync(
        string query,
        int maxResults = 5,
        int fetchPages = 2,
        int maxCharsPerPage = 3000,
        string? domain = null,
        CancellationToken ct = default)
    {
        if (_webFetcher is null)
        {
            _logger.LogWarning("SearchWithPageContentAsync: no WebFetcher available, falling back to snippet-only");
            return await SearchAsync(query, maxResults, domain, ct);
        }

        // Step 1: get search results (snippets + URLs)
        var parsed = await FetchFromSearchEnginesAsync(query, maxResults, ct);
        if (parsed.Count == 0) return await SearchAsync(query, maxResults, domain, ct);

        // Persist snippets to cache (reuse existing logic)
        var snippetText = parsed.Text;

        // Step 2: fetch top N page URLs and extract text
        var pageContents = new List<string>();
        var fetched = 0;

        foreach (var url in parsed.Urls.Take(fetchPages))
        {
            try
            {
                using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pageCts.CancelAfter(TimeSpan.FromSeconds(10));

                var doc = await _webFetcher.FetchUrlAsync(url, pageCts.Token);
                var text = await HtmlTextExtractor.ExtractAsync(doc.Body, maxCharsPerPage);

                if (!string.IsNullOrWhiteSpace(text) && text.Length > 100)
                {
                    pageContents.Add($"### Page Content [{fetched + 1}]\nURL: {url}\n{text}");
                    fetched++;
                    _logger.LogDebug("Fetched page content from {Url} ({Chars} chars)", url, text.Length);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Failed to fetch page content from {Url} — skipping", url);
            }
        }

        if (pageContents.Count == 0)
        {
            _logger.LogDebug("No page content extracted for '{Query}' — returning snippets only", query);
            return snippetText;
        }

        // Combine: snippets first (for context), then fetched page content (for data)
        var combined = $"{snippetText}\n\n---\n{string.Join("\n\n", pageContents)}";
        _logger.LogInformation("Search '{Query}': {SnippetCount} snippets + {PageCount} pages fetched",
            query, parsed.Count, pageContents.Count);
        return combined;
    }

    /// <summary>Backwards-compatible overload without domain parameter.</summary>
    public Task<string> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        SearchAsync(query, maxResults, domain: null, ct);

    // ── Internet fetch ───────────────────────────────────────────────────────

    private async Task<SearchParseResult> FetchFromSearchEnginesAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var encodedQuery = Uri.EscapeDataString(query);

        foreach (var baseUrl in _searchUrls)
        {
            try
            {
                var url = string.IsNullOrWhiteSpace(_engines)
                    ? $"{baseUrl}/search?q={encodedQuery}&format=json"
                    : $"{baseUrl}/search?q={encodedQuery}&format=json&engines={Uri.EscapeDataString(_engines)}";
                using var response = await _http.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SearXNG at {BaseUrl} returned {Status} for query '{Query}' — trying next",
                        baseUrl, (int)response.StatusCode, query);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var parsed = ParseSearxngResponse(json, maxResults);

                if (parsed.Count == 0)
                {
                    _logger.LogWarning(
                        "SearXNG at {BaseUrl} returned 0 results for '{Query}' (rate-limited?) — trying next",
                        baseUrl, query);
                    continue;
                }

                // Relevance check: if no result contains any meaningful query term,
                // the engine interpreted the query as isolated words (e.g. Bing returning
                // "Industrial Revolution" for "industrial equipment CMMS")
                if (!HasRelevantResults(query, parsed.Text))
                {
                    _logger.LogWarning(
                        "SearXNG at {BaseUrl} returned irrelevant results for '{Query}' — trying next",
                        baseUrl, query);
                    continue;
                }

                return parsed;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SearXNG at {BaseUrl} failed for query '{Query}' — trying next", baseUrl, query);
            }
        }

        return new(string.Empty, 0, []);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static SearchParseResult ParseSearxngResponse(string json, int maxResults)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var resultsEl))
                return new(string.Empty, 0, []);

            var sb = new System.Text.StringBuilder();
            var urls = new List<string>();
            var count = 0;

            foreach (var result in resultsEl.EnumerateArray())
            {
                if (count >= maxResults) break;

                var title   = GetStr(result, "title")   ?? "(no title)";
                var url     = GetStr(result, "url")     ?? string.Empty;
                var snippet = GetStr(result, "content") ?? string.Empty;

                // Skip dictionary/definition pages — Bing often returns word definitions
                // instead of domain-relevant content for multi-word queries
                if (IsJunkUrl(url)) continue;

                sb.AppendLine($"[{count + 1}] {title}");
                if (!string.IsNullOrEmpty(url))
                {
                    sb.AppendLine($"    URL: {url}");
                    urls.Add(url);
                }
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"    {snippet}");
                sb.AppendLine();

                count++;
            }

            return new(sb.ToString().TrimEnd(), count, urls);
        }
        catch
        {
            return new(json, 0, []); // return raw on parse failure
        }
    }

    /// <summary>Domains/URL patterns that indicate a dictionary/definition page rather than relevant content.</summary>
    private static readonly string[] JunkUrlPatterns =
    [
        // Dictionary sites
        "merriam-webster.com", "dictionary.com", "cambridge.org/dictionary",
        "vocabulary.com", "collinsdictionary.com", "wordreference.com",
        "thefreedictionary.com", "wiktionary.org", "yourdictionary.com",
        "oxfordlearnersdictionaries.com", "oed.com/dictionary",
        "definitions.net", "lexico.com", "macmillandictionary.com",
        // URL path patterns indicating definitions
        "/definition/", "/meaning/",
    ];

    /// <summary>Returns true if the URL points to a dictionary/definition site.</summary>
    private static bool IsJunkUrl(string url) =>
        !string.IsNullOrEmpty(url) &&
        JunkUrlPatterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stop words excluded from relevance matching — too common to be meaningful.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "by", "from", "is", "are", "was", "were", "be", "been", "being", "have", "has",
        "had", "do", "does", "did", "will", "would", "could", "should", "may", "might",
        "shall", "can", "not", "no", "so", "if", "as", "it", "its", "vs", "how", "what",
        "when", "where", "why", "which", "who", "all", "each", "every", "both", "few",
        "more", "most", "other", "some", "such", "than", "too", "very", "just",
        "about", "into", "through", "during", "before", "after", "above", "below",
        "between", "under", "over", "up", "down", "out", "off", "top", "best", "new",
        // Finance-generic terms too common to be useful for relevance checking
        "stock", "price", "share", "shares", "market", "fund", "etf",
    };

    /// <summary>
    /// Checks whether any result snippet/title contains at least one meaningful query term.
    /// Prevents accepting results where the engine interpreted multi-word queries as
    /// isolated words (e.g. returning "Industrial Revolution" for "industrial equipment CMMS").
    /// </summary>
    private static bool HasRelevantResults(string query, string resultText)
    {
        // Extract meaningful terms (non-stop-words, non-year, length > 2)
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 2 && !StopWords.Contains(t) && !IsYear(t))
            .ToArray();

        if (terms.Length == 0) return true; // can't check — accept

        var lower = resultText.ToLowerInvariant();

        // Check for bigrams first (more specific): "predictive maintenance", "condition monitoring"
        for (var i = 0; i < terms.Length - 1; i++)
        {
            var bigram = $"{terms[i]} {terms[i + 1]}".ToLowerInvariant();
            if (lower.Contains(bigram)) return true;
        }

        // Fallback: require at least 2 individual terms to appear (avoids single-word false positives)
        var matchCount = terms.Count(t => lower.Contains(t.ToLowerInvariant()));
        return matchCount >= Math.Min(2, terms.Length);
    }

    private static bool IsYear(string s) =>
        s.Length == 4 && int.TryParse(s, out var y) && y >= 2000 && y <= 2100;

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
