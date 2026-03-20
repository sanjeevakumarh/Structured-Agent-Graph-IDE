using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Memory;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WebSearchAdapter"/> covering:
/// - <c>IsConfigured</c> flag (legacy key, SAGIDE:Ollama:Servers, no config)
/// - <c>SearchAsync</c>: empty query, not configured, happy path, cache, failover
/// - <c>ParseSearxngResponse</c>: maxResults, missing array, numbered formatting
/// - URL encoding and trailing-slash normalisation
/// </summary>
public class WebSearchAdapterTests
{
    // ── Fake HTTP infrastructure ───────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _queue = new();
        public List<string> RequestedUrls { get; } = [];

        public void EnqueueJson(string json)
            => _queue.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        public void EnqueueStatus(HttpStatusCode status)
            => _queue.Enqueue(() => new HttpResponseMessage(status));

        public void EnqueueException(Exception ex)
            => _queue.Enqueue(() => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RequestedUrls.Add(request.RequestUri!.ToString());
            if (_queue.TryDequeue(out var factory))
                return Task.FromResult(factory());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[]}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WebSearchAdapter Make(FakeHandler handler, IConfiguration config)
        => new(new HttpClient(handler), config, NullLogger<WebSearchAdapter>.Instance);

    private static IConfiguration LegacyConfig(string searchUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Rag:SearchUrl"] = searchUrl,
            })
            .Build();

    private static IConfiguration ServerConfig(
        string searchUrl0,
        int    ragOrder0 = 0,
        string? searchUrl1 = null,
        int    ragOrder1 = 1) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:RagOrder"]  = ragOrder0.ToString(),
                ["SAGIDE:Ollama:Servers:0:SearchUrl"] = searchUrl0,
                ["SAGIDE:Ollama:Servers:1:RagOrder"]  = ragOrder1.ToString(),
                ["SAGIDE:Ollama:Servers:1:SearchUrl"] = searchUrl1 ?? string.Empty,
            })
            .Build();

    // Minimal SearXNG JSON with two results — snippets must contain query terms
    // so the relevance check passes (prevents false "irrelevant results" rejection)
    private const string TwoResultsJson = """
        {
          "results": [
            { "title": "Result One", "url": "https://example.com/1", "content": "Test query result snippet one with cached hello world" },
            { "title": "Result Two", "url": "https://example.com/2", "content": "Test query result snippet two with cached hello world" }
          ]
        }
        """;

    // ── IsConfigured ──────────────────────────────────────────────────────────

    [Fact]
    public void IsConfigured_NoConfig_ReturnsFalse()
    {
        var config  = new ConfigurationBuilder().Build();
        var adapter = Make(new FakeHandler(), config);

        Assert.False(adapter.IsConfigured);
    }

    [Fact]
    public void IsConfigured_LegacyKey_ReturnsTrue()
    {
        var adapter = Make(new FakeHandler(), LegacyConfig("http://searx:8080"));

        Assert.True(adapter.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ServerWithRagOrderAndSearchUrl_ReturnsTrue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:RagOrder"]  = "0",
                ["SAGIDE:Ollama:Servers:0:SearchUrl"] = "http://searx:8080",
            })
            .Build();
        var adapter = Make(new FakeHandler(), config);

        Assert.True(adapter.IsConfigured);
    }

    [Fact]
    public void IsConfigured_ServerWithRagOrderButNoSearchUrl_ReturnsFalse()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:BaseUrl"]  = "http://ollama:11434",
                ["SAGIDE:Ollama:Servers:0:RagOrder"] = "0",
                // No SearchUrl
            })
            .Build();
        var adapter = Make(new FakeHandler(), config);

        Assert.False(adapter.IsConfigured);
    }

    // ── SearchAsync — edge cases ───────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_EmptyOrWhitespace_ReturnsEmpty(string query)
    {
        var adapter = Make(new FakeHandler(), LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync(query);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task SearchAsync_NotConfigured_ReturnsEmpty()
    {
        var config  = new ConfigurationBuilder().Build();
        var adapter = Make(new FakeHandler(), config);

        var result = await adapter.SearchAsync("test query");

        Assert.Equal(string.Empty, result);
    }

    // ── SearchAsync — happy path ───────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_HappyPath_ReturnsParsedResults()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync("test query", maxResults: 5);

        Assert.Contains("[1] Result One",        result);
        Assert.Contains("URL: https://example.com/1", result);
        Assert.Contains("snippet one",           result);
        Assert.Contains("[2] Result Two",        result);
    }

    [Fact]
    public async Task SearchAsync_HappyPath_QueryAppearsInUrl()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        await adapter.SearchAsync("hello world");

        Assert.Single(handler.RequestedUrls);
        // Uri.ToString() may decode %20 back to a space; check the unescaped form
        var decodedUrl = Uri.UnescapeDataString(handler.RequestedUrls[0]);
        Assert.Contains("hello world", decodedUrl);
        Assert.Contains("/search",     decodedUrl);
        Assert.Contains("format=json", decodedUrl);
    }

    // ── SearchAsync — cache ───────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_CacheHit_SecondCallSkipsHttp()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var first  = await adapter.SearchAsync("cached query");
        var second = await adapter.SearchAsync("cached query");

        Assert.Equal(first, second);
        // Only one HTTP request — second call served from cache
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task SearchAsync_DifferentQueries_BothMakeHttpCalls()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        await adapter.SearchAsync("query A");
        await adapter.SearchAsync("query B");

        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    [Fact]
    public async Task SearchAsync_DifferentMaxResults_BothMakeHttpCalls()
    {
        // Cache key includes maxResults, so different maxResults on same query → two requests
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        await adapter.SearchAsync("same query", maxResults: 3);
        await adapter.SearchAsync("same query", maxResults: 5);

        Assert.Equal(2, handler.RequestedUrls.Count);
    }

    // ── SearchAsync — failover ────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_FirstUrlFails_TriesSecond_ReturnsResult()
    {
        var handler = new FakeHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);  // primary fails
        handler.EnqueueJson(TwoResultsJson);                       // fallback succeeds
        var adapter = Make(handler, ServerConfig("http://primary:8080", 0, "http://fallback:8080", 1));

        var result = await adapter.SearchAsync("test query");

        Assert.Contains("[1] Result One", result);
        Assert.Equal(2, handler.RequestedUrls.Count);
        // First URL is the primary (lower RagOrder)
        Assert.Contains("primary", handler.RequestedUrls[0]);
        Assert.Contains("fallback", handler.RequestedUrls[1]);
    }

    [Fact]
    public async Task SearchAsync_AllUrlsFail_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync("test query");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task SearchAsync_HttpException_Swallowed_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        // Must not throw; exceptions are swallowed and next URL tried (or empty returned)
        var result = await adapter.SearchAsync("test query");

        Assert.Equal(string.Empty, result);
    }

    // ── ParseSearxngResponse ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_MaxResults_Respected()
    {
        var json = """
            {
              "results": [
                { "title": "R1", "url": "https://a.com", "content": "q relevant snippet" },
                { "title": "R2", "url": "https://b.com", "content": "q relevant snippet" },
                { "title": "R3", "url": "https://c.com", "content": "q relevant snippet" }
              ]
            }
            """;
        var handler = new FakeHandler();
        handler.EnqueueJson(json);
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync("q", maxResults: 2);

        Assert.Contains("[1] R1",    result);
        Assert.Contains("[2] R2",    result);
        Assert.DoesNotContain("[3]", result);
    }

    [Fact]
    public async Task SearchAsync_NoResultsArray_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson("{\"query\": \"test\"}");  // no "results" key
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync("test");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task SearchAsync_EmptyResultsArray_ReturnsEmpty()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson("{\"results\":[]}");
        var adapter = Make(handler, LegacyConfig("http://searx:8080"));

        var result = await adapter.SearchAsync("test");

        Assert.Equal(string.Empty, result);
    }

    // ── URL normalisation ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_TrailingSlashInUrl_NoDoubleSlashInRequest()
    {
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        // Legacy URL has trailing slash
        var adapter = Make(handler, LegacyConfig("http://searx:8080/"));

        await adapter.SearchAsync("test");

        Assert.DoesNotContain("//search", handler.RequestedUrls[0]);
    }

    [Fact]
    public async Task SearchAsync_SearchSuffixInUrl_StrippedBeforeUse()
    {
        // If the user accidentally includes "/search" in the configured URL,
        // it should be stripped so the request doesn't become /search/search?...
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SAGIDE:Ollama:Servers:0:RagOrder"]  = "0",
                ["SAGIDE:Ollama:Servers:0:SearchUrl"] = "http://searx:8080/search",  // misconfigured
            })
            .Build();
        var handler = new FakeHandler();
        handler.EnqueueJson(TwoResultsJson);
        var adapter = Make(handler, config);

        await adapter.SearchAsync("test");

        // Path should be /search (once), not /search/search
        Assert.DoesNotContain("/search/search", handler.RequestedUrls[0]);
    }
}
