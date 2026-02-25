using System.ServiceModel.Syndication;
using System.Xml;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Fetches content from HTTP URLs and RSS/Atom feeds.
/// Rate-limited; response TTL cache prevents re-fetching within the configured window.
/// Only runs on the laptop (the sole node with internet access).
/// </summary>
public sealed class WebFetcher
{
    private readonly HttpClient _http;
    private readonly ILogger<WebFetcher> _logger;
    private readonly TimeSpan _rateLimitDelay;
    private readonly TimeSpan _cacheTtl;

    // Simple in-memory cache: url → (content, fetched_at)
    private readonly Dictionary<string, (FetchedDocument doc, DateTime fetchedAt)> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WebFetcher(HttpClient http, ILogger<WebFetcher> logger,
        TimeSpan? rateLimitDelay = null, TimeSpan? cacheTtl = null)
    {
        _http           = http;
        _logger         = logger;
        _rateLimitDelay = rateLimitDelay ?? TimeSpan.FromSeconds(1);
        _cacheTtl       = cacheTtl       ?? TimeSpan.FromHours(4);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fetch a single HTTP URL as plain text. Returns cached content if within TTL.</summary>
    public async Task<FetchedDocument> FetchUrlAsync(string url, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (TryGetCached(url, out var cached)) return cached;

            await Task.Delay(_rateLimitDelay, ct);
            _logger.LogDebug("Fetching URL: {Url}", url);

            // HttpClientHandler silently refuses to follow HTTPS→HTTP redirect
            // (security downgrade). We follow up to 5 such hops manually so that
            // URLs like https://arxiv.org/rss/cs.RO → http://export.arxiv.org/rss/cs.RO
            // succeed without YAML changes.
            var response = await _http.GetAsync(url, ct);
            var manualRedirects = 0;
            while (manualRedirects < 5 &&
                   (int)response.StatusCode is >= 300 and < 400 &&
                   response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(url), location).ToString();
                _logger.LogDebug("Following redirect {From} → {To}", url, next);
                url = next;
                response = await _http.GetAsync(url, ct);
                manualRedirects++;
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            var doc = new FetchedDocument(url, ExtractTitle(url, body), body, DateTime.UtcNow, "http");
            _cache[url] = (doc, DateTime.UtcNow);
            return doc;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Fetch and parse an RSS or Atom feed. Returns one FetchedDocument per entry.</summary>
    public async Task<IReadOnlyList<FetchedDocument>> FetchRssAsync(string feedUrl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // Always cache under the caller-supplied URL (originalUrl) so the entry is
            // found on the next call even when a redirect was followed internally.
            var originalUrl = feedUrl;
            var cacheKey    = $"rss:{originalUrl}";
            if (TryGetCached(cacheKey, out _))
            {
                // Return all cached docs that match this feed
                return [.. _cache
                    .Where(kv => kv.Key.StartsWith($"rss-item:{originalUrl}", StringComparison.Ordinal))
                    .Select(kv => kv.Value.doc)];
            }

            await Task.Delay(_rateLimitDelay, ct);
            _logger.LogDebug("Fetching RSS: {Url}", feedUrl);

            // Same manual redirect logic as FetchUrlAsync — handles HTTPS→HTTP downgrade that
            // HttpClientHandler refuses to follow automatically.
            var currentUrl = feedUrl;
            var response   = await _http.GetAsync(currentUrl, ct);
            var manualRedirects = 0;
            while (manualRedirects < 5 &&
                   (int)response.StatusCode is >= 300 and < 400 &&
                   response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(currentUrl), location).ToString();
                _logger.LogDebug("RSS redirect {From} → {To}", currentUrl, next);
                currentUrl = next;
                response   = await _http.GetAsync(currentUrl, ct);
                manualRedirects++;
            }
            response.EnsureSuccessStatusCode();
            var xml  = await response.Content.ReadAsStringAsync(ct);
            var docs = ParseRss(currentUrl, xml);

            // Cache under the original caller URL so subsequent calls get a cache hit
            _cache[cacheKey] = (new FetchedDocument(originalUrl, originalUrl, xml, DateTime.UtcNow, "rss"), DateTime.UtcNow);
            foreach (var d in docs)
                _cache[$"rss-item:{originalUrl}:{d.Url}"] = (d, DateTime.UtcNow);

            return docs;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Fetches multiple data sources defined in a PromptDefinition.
    /// Returns all collected documents grouped by data source type.
    /// </summary>
    public async Task<IReadOnlyList<FetchedDocument>> FetchDataSourcesAsync(
        IEnumerable<PromptDataSource> sources, CancellationToken ct = default)
    {
        var all = new List<FetchedDocument>();

        foreach (var source in sources)
        {
            try
            {
                switch (source.Type.ToLowerInvariant())
                {
                    case "web_api":
                    case "http":
                        if (!string.IsNullOrEmpty(source.Url))
                            all.Add(await FetchUrlAsync(source.Url, ct));
                        break;

                    case "rss":
                    case "atom":
                        if (!string.IsNullOrEmpty(source.Url))
                            all.AddRange(await FetchRssAsync(source.Url, ct));
                        break;

                    case "local_file":
                        if (!string.IsNullOrEmpty(source.Path))
                            all.Add(await ReadLocalFileAsync(source.Path, ct));
                        break;

                    default:
                        _logger.LogWarning("Unsupported data source type: {Type}", source.Type);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch data source {Type}:{Url}", source.Type, source.Url ?? source.Path);
            }
        }

        return all;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryGetCached(string key, out FetchedDocument doc)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.fetchedAt < _cacheTtl)
        {
            doc = entry.doc;
            return true;
        }
        doc = default!;
        return false;
    }

    private static IReadOnlyList<FetchedDocument> ParseRss(string feedUrl, string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml));
            var feed = SyndicationFeed.Load(reader);
            return [.. feed.Items.Select(item =>
            {
                var link = item.Links.FirstOrDefault()?.Uri?.ToString() ?? feedUrl;
                var title = item.Title?.Text ?? string.Empty;
                var body = item.Summary?.Text ?? item.Content?.ToString() ?? string.Empty;
                var publishedAt = item.PublishDate == DateTimeOffset.MinValue
                    ? DateTime.UtcNow
                    : item.PublishDate.UtcDateTime;
                return new FetchedDocument(link, title, body, publishedAt, "rss");
            })];
        }
        catch
        {
            return [];
        }
    }

    private static async Task<FetchedDocument> ReadLocalFileAsync(string path, CancellationToken ct)
    {
        var expanded = path.Replace("~/", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/");
        var content  = await File.ReadAllTextAsync(expanded, ct);
        return new FetchedDocument(expanded, Path.GetFileName(expanded), content, DateTime.UtcNow, "local_file");
    }

    private static string ExtractTitle(string url, string body)
    {
        // Very lightweight title extraction: look for <title>...</title>
        var start = body.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return url;
        start += 7;
        var end = body.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
        return end > start ? body[start..end].Trim() : url;
    }
}
