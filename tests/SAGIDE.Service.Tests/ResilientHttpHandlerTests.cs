using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="ResilientHttpHandler"/> covering:
/// - Immediate success (no retries)
/// - Non-retryable status code (fail immediately)
/// - Retryable status code: retries until success
/// - All retries exhausted (returns last bad response)
/// - Per-attempt timeout (throws TimeoutException)
/// - User cancellation propagated
/// - Retry-After header honoured for 429
/// - HttpRequestException retried then re-thrown
/// </summary>
public class ResilientHttpHandlerTests
{
    // ── Fake HTTP infrastructure ───────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _queue = new();

        public void Enqueue(HttpResponseMessage r) => _queue.Enqueue(() => r);
        public void EnqueueDelay(int ms, HttpResponseMessage r)
            => _queue.Enqueue(() => { Task.Delay(ms).Wait(); return r; });
        public void EnqueueException(Exception ex)
            => _queue.Enqueue(() => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_queue.TryDequeue(out var factory))
                return Task.FromResult(factory());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static ResilientHttpHandler Make(
        FakeHandler handler,
        int maxRetries = 2,
        int timeoutMs  = 5_000)
    {
        var policy = new RetryPolicy
        {
            MaxRetries  = maxRetries,
            InitialDelay = TimeSpan.FromMilliseconds(1),   // no real waiting in tests
            Strategy    = BackoffStrategy.Fixed,
            RetryableStatusCodes = [429, 500, 502, 503],
        };
        return new ResilientHttpHandler(
            new HttpClient(handler),
            policy,
            TimeSpan.FromMilliseconds(timeoutMs),
            NullLogger<ResilientHttpHandler>.Instance);
    }

    private static HttpRequestMessage Req()
        => new(HttpMethod.Post, "http://localhost/api/generate");

    // ── Success cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ImmediateSuccess_ReturnsResponse_OneAttempt()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var rh = Make(handler);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, rh.TotalAttempts);
    }

    // ── Non-retryable errors ──────────────────────────────────────────────────

    [Fact]
    public async Task NonRetryableStatusCode_FailsImmediately_OneAttempt()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)); // 404 — not retryable
        var rh = Make(handler);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, rh.TotalAttempts);
    }

    [Fact]
    public async Task BadRequest_FailsImmediately()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));  // 400
        var rh = Make(handler);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, rh.TotalAttempts);
    }

    // ── Retryable errors ──────────────────────────────────────────────────────

    [Fact]
    public async Task RetryableError_ThenSuccess_Retries()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError)); // 500 → retry
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var rh = Make(handler, maxRetries: 2);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rh.TotalAttempts);
    }

    [Fact]
    public async Task AllRetriesExhausted_ReturnsLastBadResponse()
    {
        var handler = new FakeHandler();
        // maxRetries=2 → 3 total attempts; all 500s
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var rh = Make(handler, maxRetries: 2);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, rh.TotalAttempts);
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_AllAttempts_ThrowsTimeoutException()
    {
        // Every attempt blocks for 200ms but timeout is 10ms → times out
        var handler = new FakeHandler();
        for (var i = 0; i < 3; i++)
            handler.EnqueueDelay(200, new HttpResponseMessage(HttpStatusCode.OK));
        var rh = Make(handler, maxRetries: 0, timeoutMs: 10);

        await Assert.ThrowsAsync<TimeoutException>(
            () => rh.SendWithRetryAsync(() => Req(), CancellationToken.None));
    }

    // ── User cancellation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_PropagatesImmediately()
    {
        var handler = new FakeHandler();
        var cts     = new CancellationTokenSource();
        cts.Cancel();

        var rh = Make(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => rh.SendWithRetryAsync(() => Req(), cts.Token));
    }

    // ── HttpRequestException retry ────────────────────────────────────────────

    [Fact]
    public async Task HttpRequestException_Retried_ThenThrows()
    {
        var handler = new FakeHandler();
        // maxRetries=1 → 2 total attempts; both throw
        handler.EnqueueException(new HttpRequestException("connection refused"));
        handler.EnqueueException(new HttpRequestException("connection refused"));
        var rh = Make(handler, maxRetries: 1);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => rh.SendWithRetryAsync(() => Req(), CancellationToken.None));

        Assert.Equal(2, rh.TotalAttempts);
    }

    [Fact]
    public async Task HttpRequestException_ThenSuccess_Recovers()
    {
        var handler = new FakeHandler();
        handler.EnqueueException(new HttpRequestException("timeout"));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var rh = Make(handler, maxRetries: 1);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Retry-After header ────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimited_RetryAfterHeader_UsedForDelay()
    {
        // The key correctness property: a 429 with Retry-After:0 must still
        // retry and eventually succeed (not fail-fast like a non-retryable code).
        var handler = new FakeHandler();
        var r429    = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r429.Headers.Add("Retry-After", "0");  // 0s wait so test is fast
        handler.Enqueue(r429);
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        var rh = Make(handler, maxRetries: 1);

        var response = await rh.SendWithRetryAsync(() => Req(), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, rh.TotalAttempts);
    }
}
