using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using ModelRouterImpl = SAGIDE.ModelRouter.ModelRouter;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="ModelRouter"/> covering:
/// - Provider selection by ModelProvider enum
/// - Unknown provider throws with helpful message
/// - Circuit breaker open → InvalidOperationException
/// - Circuit breaker closed → call proceeds
/// - No circuit breaker (null) → call proceeds
/// - HasProvider reflects registered set
/// - AvailableProviders lists all registered providers
/// - Token counts forwarded from provider after CompleteAsync
/// - Token counts forwarded after CompleteStreamingAsync
/// </summary>
public class ModelRouterTests
{
    // ── Fake provider ─────────────────────────────────────────────────────────

    private sealed class FakeProvider : IAgentProvider
    {
        public ModelProvider Provider         { get; }
        public int           LastInputTokens  { get; private set; }
        public int           LastOutputTokens { get; private set; }

        private readonly string _response;
        private readonly int    _inputTokens;
        private readonly int    _outputTokens;

        public FakeProvider(
            ModelProvider provider,
            string response      = "ok",
            int inputTokens      = 10,
            int outputTokens     = 20)
        {
            Provider      = provider;
            _response     = response;
            _inputTokens  = inputTokens;
            _outputTokens = outputTokens;
        }

        public Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
        {
            LastInputTokens  = _inputTokens;
            LastOutputTokens = _outputTokens;
            return Task.FromResult(_response);
        }

        public async IAsyncEnumerable<string> CompleteStreamingAsync(
            string prompt, ModelConfig model,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastInputTokens  = _inputTokens;
            LastOutputTokens = _outputTokens;
            yield return _response;
            await Task.CompletedTask;
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private static ModelRouterImpl MakeRouter(
        IEnumerable<IAgentProvider> providers,
        Func<ModelProvider, bool>? isCircuitOpen = null)
        => new(providers, isCircuitOpen, NullLogger<ModelRouterImpl>.Instance);

    private static ModelConfig Config(ModelProvider p) =>
        new(p, "test-model");

    // ── Provider selection ────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_RoutesToCorrectProvider()
    {
        var ollama  = new FakeProvider(ModelProvider.Ollama,  "ollama-response");
        var claude  = new FakeProvider(ModelProvider.Claude,  "claude-response");
        var router  = MakeRouter([ollama, claude]);

        var result = await router.CompleteAsync("hello", Config(ModelProvider.Ollama));

        Assert.Equal("ollama-response", result);
    }

    [Fact]
    public async Task CompleteAsync_UnknownProvider_Throws()
    {
        var router = MakeRouter([new FakeProvider(ModelProvider.Ollama)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CompleteAsync("hello", Config(ModelProvider.Claude)));

        Assert.Contains("Claude", ex.Message);
        Assert.Contains("Ollama", ex.Message); // shows available providers
    }

    [Fact]
    public async Task CompleteStreamingAsync_RoutesToCorrectProvider()
    {
        var ollama = new FakeProvider(ModelProvider.Ollama, "stream-chunk");
        var router = MakeRouter([ollama]);

        var chunks = new List<string>();
        await foreach (var chunk in router.CompleteStreamingAsync("hello", Config(ModelProvider.Ollama)))
            chunks.Add(chunk);

        Assert.Equal(["stream-chunk"], chunks);
    }

    // ── Circuit breaker ───────────────────────────────────────────────────────

    [Fact]
    public async Task CircuitOpen_CompleteAsync_Throws()
    {
        var router = MakeRouter(
            [new FakeProvider(ModelProvider.Ollama)],
            isCircuitOpen: _ => true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CompleteAsync("hello", Config(ModelProvider.Ollama)));

        Assert.Contains("Circuit breaker open", ex.Message);
    }

    [Fact]
    public async Task CircuitOpen_CompleteStreamingAsync_Throws()
    {
        var router = MakeRouter(
            [new FakeProvider(ModelProvider.Ollama)],
            isCircuitOpen: _ => true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in router.CompleteStreamingAsync("hello", Config(ModelProvider.Ollama)))
            { }
        });

        Assert.Contains("Circuit breaker open", ex.Message);
    }

    [Fact]
    public async Task CircuitClosed_CompleteAsync_Succeeds()
    {
        var router = MakeRouter(
            [new FakeProvider(ModelProvider.Ollama, "result")],
            isCircuitOpen: _ => false);

        var result = await router.CompleteAsync("hello", Config(ModelProvider.Ollama));
        Assert.Equal("result", result);
    }

    [Fact]
    public async Task CircuitBreakerOnlyOpenForOneProvider_OtherProviderSucceeds()
    {
        var router = MakeRouter(
            [new FakeProvider(ModelProvider.Ollama), new FakeProvider(ModelProvider.Claude, "claude-ok")],
            isCircuitOpen: p => p == ModelProvider.Ollama);

        // Ollama is blocked
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.CompleteAsync("hello", Config(ModelProvider.Ollama)));

        // Claude is not
        var result = await router.CompleteAsync("hello", Config(ModelProvider.Claude));
        Assert.Equal("claude-ok", result);
    }

    [Fact]
    public async Task NullCircuitBreaker_AllCallsSucceed()
    {
        var router = MakeRouter(
            [new FakeProvider(ModelProvider.Ollama, "no-cb")],
            isCircuitOpen: null);

        var result = await router.CompleteAsync("hello", Config(ModelProvider.Ollama));
        Assert.Equal("no-cb", result);
    }

    // ── Token forwarding ──────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteAsync_ForwardsTokenCounts()
    {
        var provider = new FakeProvider(ModelProvider.Ollama, inputTokens: 42, outputTokens: 99);
        var router   = MakeRouter([provider]);

        await router.CompleteAsync("hello", Config(ModelProvider.Ollama));

        Assert.Equal(42, router.LastInputTokens);
        Assert.Equal(99, router.LastOutputTokens);
    }

    [Fact]
    public async Task CompleteStreamingAsync_ForwardsTokenCounts()
    {
        var provider = new FakeProvider(ModelProvider.Ollama, inputTokens: 7, outputTokens: 13);
        var router   = MakeRouter([provider]);

        await foreach (var _ in router.CompleteStreamingAsync("hello", Config(ModelProvider.Ollama)))
        { }

        Assert.Equal(7,  router.LastInputTokens);
        Assert.Equal(13, router.LastOutputTokens);
    }

    // ── HasProvider / AvailableProviders ─────────────────────────────────────

    [Fact]
    public void HasProvider_RegisteredProvider_ReturnsTrue()
    {
        var router = MakeRouter([new FakeProvider(ModelProvider.Ollama)]);
        Assert.True(router.HasProvider(ModelProvider.Ollama));
    }

    [Fact]
    public void HasProvider_UnregisteredProvider_ReturnsFalse()
    {
        var router = MakeRouter([new FakeProvider(ModelProvider.Ollama)]);
        Assert.False(router.HasProvider(ModelProvider.Claude));
    }

    [Fact]
    public void AvailableProviders_ListsAllRegistered()
    {
        var router = MakeRouter([
            new FakeProvider(ModelProvider.Ollama),
            new FakeProvider(ModelProvider.Claude),
        ]);

        Assert.Equal(2, router.AvailableProviders.Count);
        Assert.Contains(ModelProvider.Ollama, router.AvailableProviders);
        Assert.Contains(ModelProvider.Claude, router.AvailableProviders);
    }

    [Fact]
    public void EmptyProviders_AvailableProviders_IsEmpty()
    {
        var router = MakeRouter([]);
        Assert.Empty(router.AvailableProviders);
    }
}
