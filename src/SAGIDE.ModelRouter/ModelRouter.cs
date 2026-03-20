using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Observability;

namespace SAGIDE.ModelRouter;

/// <summary>
/// Concrete implementation of <see cref="IModelRouter"/>.
///
/// Wraps the collection of registered <see cref="IAgentProvider"/> instances and adds:
/// - Provider selection by <see cref="ModelProvider"/>
/// - Circuit-breaker guard (delegated to the caller's existing registry via callback)
/// - Span instrumentation via <see cref="SagideActivitySource.ModelRouter"/>
/// - Token count forwarding from the selected provider
///
/// Failover across Ollama hosts is handled inside <c>OllamaProvider</c> itself
/// (unchanged) — this router picks the right provider, not the right host.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly IReadOnlyDictionary<ModelProvider, IAgentProvider> _providers;
    private readonly Func<ModelProvider, bool>? _isCircuitOpen;
    private readonly ILogger<ModelRouter> _logger;

    private int _lastInputTokens;
    private int _lastOutputTokens;

    public int LastInputTokens  => _lastInputTokens;
    public int LastOutputTokens => _lastOutputTokens;

    public IReadOnlyList<ModelProvider> AvailableProviders =>
        _providers.Keys.ToList();

    /// <param name="providers">All registered agent providers (injected as IEnumerable).</param>
    /// <param name="isCircuitOpen">
    /// Optional callback: returns true when the circuit breaker for a provider is open
    /// and calls should be rejected immediately. Pass null to disable circuit-breaker integration.
    /// </param>
    /// <param name="logger">Logger.</param>
    public ModelRouter(
        IEnumerable<IAgentProvider> providers,
        Func<ModelProvider, bool>? isCircuitOpen,
        ILogger<ModelRouter> logger)
    {
        _providers      = providers.ToDictionary(p => p.Provider);
        _isCircuitOpen  = isCircuitOpen;
        _logger         = logger;
    }

    public bool HasProvider(ModelProvider provider) =>
        _providers.ContainsKey(provider);

    // ── IModelRouter ──────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt,
        ModelConfig model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var provider = Resolve(model.Provider);

        using var activity = SagideActivitySource.Start(
            SagideActivitySource.ModelRouter,
            "router.complete_streaming",
            ActivityKind.Client,
            TraceContext.TraceId);
        activity?.SetTag("llm.provider", model.Provider.ToString());
        activity?.SetTag("llm.model",    model.ModelId);

        await foreach (var chunk in provider.CompleteStreamingAsync(prompt, model, ct)
                           .ConfigureAwait(false))
        {
            yield return chunk;
        }

        _lastInputTokens  = provider.LastInputTokens;
        _lastOutputTokens = provider.LastOutputTokens;
        activity?.SetTag("llm.tokens_input",  _lastInputTokens);
        activity?.SetTag("llm.tokens_output", _lastOutputTokens);
    }

    public async Task<string> CompleteAsync(
        string prompt,
        ModelConfig model,
        CancellationToken ct = default)
    {
        var provider = Resolve(model.Provider);

        using var activity = SagideActivitySource.Start(
            SagideActivitySource.ModelRouter,
            "router.complete",
            ActivityKind.Client,
            TraceContext.TraceId);
        activity?.SetTag("llm.provider", model.Provider.ToString());
        activity?.SetTag("llm.model",    model.ModelId);

        var result = await provider.CompleteAsync(prompt, model, ct).ConfigureAwait(false);

        _lastInputTokens  = provider.LastInputTokens;
        _lastOutputTokens = provider.LastOutputTokens;
        activity?.SetTag("llm.tokens_input",  _lastInputTokens);
        activity?.SetTag("llm.tokens_output", _lastOutputTokens);

        return result;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private IAgentProvider Resolve(ModelProvider provider)
    {
        if (_isCircuitOpen?.Invoke(provider) == true)
        {
            _logger.LogWarning("IModelRouter: circuit open for {Provider} — rejecting call", provider);
            throw new InvalidOperationException(
                $"Circuit breaker open for provider {provider}. Call rejected.");
        }

        if (!_providers.TryGetValue(provider, out var agentProvider))
            throw new InvalidOperationException(
                $"No provider registered for {provider}. Available: {string.Join(", ", _providers.Keys)}");

        return agentProvider;
    }
}
