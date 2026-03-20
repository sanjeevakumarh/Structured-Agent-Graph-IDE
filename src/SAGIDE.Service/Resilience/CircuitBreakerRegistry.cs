using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Resilience;

/// <summary>
/// Holds one <see cref="ProviderCircuitBreaker"/> per <see cref="ModelProvider"/>.
/// Created lazily on first access so providers that are never used don't get breakers.
/// Implements <see cref="ICircuitBreakerRegistry"/> so the ModelRouter module can query
/// breaker state without depending on this assembly directly.
/// </summary>
public sealed class CircuitBreakerRegistry : ICircuitBreakerRegistry
{
    private readonly CircuitBreakerConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<ModelProvider, ProviderCircuitBreaker> _breakers = new();

    public CircuitBreakerRegistry(CircuitBreakerConfig config, ILoggerFactory loggerFactory)
    {
        _config        = config;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Returns <c>null</c> when circuit breakers are disabled in config.
    /// Otherwise returns (or creates) the breaker for the given provider.
    /// </summary>
    public ProviderCircuitBreaker? GetBreaker(ModelProvider provider)
    {
        if (!_config.Enabled) return null;

        return _breakers.GetOrAdd(provider, p => new ProviderCircuitBreaker(
            providerName     : p.ToString(),
            failureThreshold : _config.FailureThreshold,
            resetTimeout     : TimeSpan.FromSeconds(_config.ResetTimeoutSec),
            logger           : _loggerFactory.CreateLogger<ProviderCircuitBreaker>()));
    }

    /// <inheritdoc/>
    public bool IsCallPermitted(ModelProvider provider)
    {
        if (!_config.Enabled) return true;
        var breaker = GetBreaker(provider);
        return breaker is null || breaker.IsCallPermitted();
    }

    /// <summary>Snapshot of all breaker states for the metrics/health endpoint.</summary>
    public IReadOnlyDictionary<string, (string State, double SecondsUntilReset)> GetSnapshot()
    {
        return _breakers.ToDictionary(
            kv => kv.Key.ToString(),
            kv => (kv.Value.State, kv.Value.SecondsUntilReset));
    }
}
