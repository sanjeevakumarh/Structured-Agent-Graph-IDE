using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Rag;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WebFetcher"/> covering:
/// - Happy-path fetch
/// - Manual redirect following for HTTPS→HTTP downgrades (the arXiv RSS bug)
/// - Redirect chain and over-limit behaviour
/// - HTML title extraction
/// - In-memory cache (hit and second-call)
/// - RSS parsing (happy path, malformed XML, cache)
/// </summary>
public class WebFetcherTests
{
    // ── Fake HTTP infrastructure ───────────────────────────────────────────────

    /// <summary>
    /// Queue-based fake handler: each call to SendAsync dequeues one pre-configured
    /// response.  Useful for redirect chains where the same URL may be called twice.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();

        public void Enqueue(HttpResponseMessage response) => _queue.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (_queue.TryDequeue(out var resp))
                return Task.FromResult(resp);

            // Fallback: 200 OK with empty body
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }

    private static HttpResponseMessage Ok(string body, string contentType = "text/plain")
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);   // 302
        r.Headers.Location = new Uri(location, UriKind.Absolute);
        return r;
    }

    private static WebFetcher MakeFetcher(FakeHandler handler)
        => new(
            new HttpClient(handler),
            NullLogger<WebFetcher>.Instance,
            rateLimitDelay: TimeSpan.Zero,   // no delay in tests
            cacheTtl: TimeSpan.FromHours(1));

    // ── FetchUrlAsync — happy path ─────────────────────────────────────────────

    [Fact]
    public async Task FetchUrlAsync_200_ReturnsBody()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("hello world"));
        var fetcher = MakeFetcher(handler);

        var doc = await fetcher.FetchUrlAsync("http://example.com/page");

        Assert.Equal("hello world", doc.Body);
        Assert.Equal("http://example.com/page", doc.Url);
        Assert.Equal("http", doc.SourceType);
    }

    [Fact]
    public async Task FetchUrlAsync_HtmlTitle_ExtractedFromBody()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("<html><head><title>My Page</title></head><body>text</body></html>", "text/html"));
        var fetcher = MakeFetcher(handler);

        var doc = await fetcher.FetchUrlAsync("http://example.com/page");

        Assert.Equal("My Page", doc.Title);
    }

    [Fact]
    public async Task FetchUrlAsync_NoTitle_UrlUsedAsTitle()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("plain text with no title tag"));
        var fetcher = MakeFetcher(handler);

        var doc = await fetcher.FetchUrlAsync("http://example.com/notitle");

        Assert.Contains("example.com", doc.Title);
    }

    [Fact]
    public async Task FetchUrlAsync_404_ThrowsHttpRequestException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not Found")
        });
        var fetcher = MakeFetcher(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchUrlAsync("http://example.com/missing"));
    }

    // ── FetchUrlAsync — redirect following ────────────────────────────────────

    [Fact]
    public async Task FetchUrlAsync_302_FollowsManualRedirect()
    {
        // Simulates: https://arxiv.org/rss/cs.RO → 302 → http://export.arxiv.org/rss/cs.RO → 200
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://export.example.com/feed"));
        handler.Enqueue(Ok("redirected content"));
        var fetcher = MakeFetcher(handler);

        var doc = await fetcher.FetchUrlAsync("https://original.example.com/feed");

        Assert.Equal("redirected content", doc.Body);
        // The cache URL should be the final URL after the redirect
        Assert.Equal("http://export.example.com/feed", doc.Url);
    }

    [Fact]
    public async Task FetchUrlAsync_302Chain_FollowsUpToFiveHops()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://step2.example.com/"));
        handler.Enqueue(Redirect("http://step3.example.com/"));
        handler.Enqueue(Ok("final content"));
        var fetcher = MakeFetcher(handler);

        var doc = await fetcher.FetchUrlAsync("https://step1.example.com/");

        Assert.Equal("final content", doc.Body);
    }

    [Fact]
    public async Task FetchUrlAsync_MoreThanFiveRedirects_Throws()
    {
        // 6 consecutive 302s — after 5 manual hops, EnsureSuccessStatusCode throws
        var handler = new FakeHandler();
        for (var i = 0; i < 6; i++)
            handler.Enqueue(Redirect($"http://hop{i + 1}.example.com/"));
        var fetcher = MakeFetcher(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchUrlAsync("https://start.example.com/"));
    }

    [Fact]
    public async Task FetchUrlAsync_302NoLocationHeader_Throws()
    {
        // A 302 with no Location header — cannot follow, EnsureSuccessStatusCode throws
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent("no location")
            // no Location header added
        });
        var fetcher = MakeFetcher(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.FetchUrlAsync("https://example.com/broken-redirect"));
    }

    // ── FetchUrlAsync — cache ─────────────────────────────────────────────────

    [Fact]
    public async Task FetchUrlAsync_CachesResult_SecondCallSkipsHttp()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("cached body"));
        // Only one response queued — if a second HTTP call were made it would get
        // the fallback empty 200 and the body would differ.
        var fetcher = MakeFetcher(handler);

        var first  = await fetcher.FetchUrlAsync("http://example.com/cached");
        var second = await fetcher.FetchUrlAsync("http://example.com/cached");

        Assert.Equal("cached body", first.Body);
        Assert.Equal("cached body", second.Body);
    }

    // ── FetchRssAsync — happy path ────────────────────────────────────────────

    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Test Feed</title>
            <item>
              <title>Paper One</title>
              <link>https://arxiv.org/abs/2401.0001</link>
              <description>Abstract of paper one.</description>
            </item>
            <item>
              <title>Paper Two</title>
              <link>https://arxiv.org/abs/2401.0002</link>
              <description>Abstract of paper two.</description>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public async Task FetchRssAsync_ValidRss_ReturnsEntries()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));
        var fetcher = MakeFetcher(handler);

        var entries = await fetcher.FetchRssAsync("https://example.com/feed.rss");

        Assert.Equal(2, entries.Count);
        Assert.Equal("Paper One",  entries[0].Title);
        Assert.Equal("Paper Two",  entries[1].Title);
        Assert.Equal("Abstract of paper one.", entries[0].Body);
        Assert.Equal("rss",        entries[0].SourceType);
    }

    [Fact]
    public async Task FetchRssAsync_MalformedXml_ReturnsEmpty_NoException()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok("<<< not xml >>>", "application/rss+xml"));
        var fetcher = MakeFetcher(handler);

        var entries = await fetcher.FetchRssAsync("https://example.com/bad.rss");

        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchRssAsync_CachesResult_SecondCallSkipsHttp()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));
        var fetcher = MakeFetcher(handler);

        var first  = await fetcher.FetchRssAsync("https://example.com/feed.rss");
        var second = await fetcher.FetchRssAsync("https://example.com/feed.rss");

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.Equal("Paper One", second[0].Title);
    }

    // ── FetchRssAsync — redirect (same HTTPS→HTTP fix) ────────────────────────

    [Fact]
    public async Task FetchRssAsync_302_FollowsManualRedirect()
    {
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://export.example.com/feed.rss"));
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));
        var fetcher = MakeFetcher(handler);

        var entries = await fetcher.FetchRssAsync("https://original.example.com/feed.rss");

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task FetchRssAsync_CacheKeyUsesOriginalUrl_AfterRedirect()
    {
        // After a redirect, a second call with the ORIGINAL URL must hit cache
        // (not do a new HTTP request).
        var handler = new FakeHandler();
        handler.Enqueue(Redirect("http://export.example.com/feed.rss"));
        handler.Enqueue(Ok(SampleRss, "application/rss+xml"));
        // Only one redirect+content queued — a third call would get the empty fallback.
        var fetcher = MakeFetcher(handler);

        await fetcher.FetchRssAsync("https://original.example.com/feed.rss");
        var cached = await fetcher.FetchRssAsync("https://original.example.com/feed.rss");

        Assert.Equal(2, cached.Count);
        Assert.Equal("Paper One", cached[0].Title);
    }
}
