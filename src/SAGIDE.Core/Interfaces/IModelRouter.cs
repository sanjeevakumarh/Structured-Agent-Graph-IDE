using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// The single entry point for all model dispatch in the Agent OS.
///
/// Callers ask for a streaming or non-streaming completion; the router
/// decides which provider and which endpoint to use based on:
///   - Requested <see cref="ModelProvider"/> and model ID
///   - Live host health (VRAM-warm host preference)
///   - Historical performance hints (p95 latency, success rate)
///   - Circuit-breaker state (skip known-bad providers)
///   - Endpoint override in <see cref="ModelConfig"/>
///
/// This interface intentionally mirrors <see cref="IAgentProvider"/> so
/// callers don't need to know which concrete provider is behind it.
/// The router handles all failover, retry, and host-selection logic
/// that was previously scattered across <c>AgentOrchestrator</c>.
/// </summary>
public interface IModelRouter
{
    /// <summary>
    /// Returns true when at least one provider is registered and reachable.
    /// </summary>
    bool HasProvider(ModelProvider provider);

    /// <summary>
    /// Streams incremental text chunks for the given prompt and model config.
    /// The router selects the best available host and handles failover transparently.
    /// </summary>
    IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt,
        ModelConfig model,
        CancellationToken ct = default);

    /// <summary>
    /// Non-streaming completion — accumulates the full response and returns it.
    /// Used by quality sampler and any caller that doesn't need incremental output.
    /// </summary>
    Task<string> CompleteAsync(
        string prompt,
        ModelConfig model,
        CancellationToken ct = default);

    /// <summary>
    /// Token counts from the most recent call on this router instance.
    /// Thread-safety note: read immediately after awaiting Complete* — not safe
    /// for concurrent callers sharing one router instance.
    /// </summary>
    int LastInputTokens { get; }
    int LastOutputTokens { get; }

    /// <summary>
    /// All providers currently registered and their availability.
    /// Used by <c>GET /api/models</c> and health checks.
    /// </summary>
    IReadOnlyList<ModelProvider> AvailableProviders { get; }
}
