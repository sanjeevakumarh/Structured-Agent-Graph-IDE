using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Routing;

/// <summary>
/// Computes advisory routing hint scores per (modelId, serverAlias) pair using cached
/// performance data from <see cref="IModelPerfRepository"/>.
/// <para>
/// Exposes <see cref="RankCandidates"/> to reorder Ollama fallback candidate URLs by
/// weighted score (UserChoice × 0.5 + Performance × perfScore + Quality × 0.5).
/// Only fallback ordering is affected — explicit <c>model.Endpoint</c> overrides and
/// VRAM-warm selection in <see cref="OllamaHostHealthMonitor"/> take precedence.
/// </para>
/// <para>
/// Perf summaries are refreshed asynchronously every 60 s (stale-while-revalidate).
/// The hint cache is updated in a fire-and-forget background task so routing calls
/// are always non-blocking.
/// </para>
/// </summary>
public sealed class ModelRoutingHints
{
    private readonly IModelPerfRepository? _perfRepo;
    private readonly EndpointAliasResolver _aliasResolver;
    private readonly RoutingConfig _config;
    private readonly ILogger<ModelRoutingHints> _logger;

    // Stale-while-revalidate cache — volatile so reads see latest write from bg task.
    private volatile IReadOnlyList<ModelPerfSummary> _cachedSummaries = [];
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly object _cacheRefreshLock = new();

    private const int CacheWindowMinutes = 60;
    private const int CacheTtlSeconds   = 60;

    public ModelRoutingHints(
        IModelPerfRepository? perfRepo,
        EndpointAliasResolver aliasResolver,
        RoutingConfig config,
        ILogger<ModelRoutingHints> logger)
    {
        _perfRepo      = perfRepo;
        _aliasResolver = aliasResolver;
        _config        = config;
        _logger        = logger;

        var sum = config.Weights.UserChoice + config.Weights.Performance + config.Weights.Quality;
        if (Math.Abs(sum - 1.0) > 0.05)
            _logger.LogWarning(
                "SAGIDE:Routing:Weights sum to {Sum:F2}, expected ~1.0. Check configuration.", sum);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the candidate base URLs reordered by hint score (highest first).
    /// When there is only one candidate, no perf data, or no repository, returns
    /// the input unchanged so the caller's fallback behaviour is unaffected.
    /// </summary>
    public IEnumerable<string> RankCandidates(IEnumerable<string> candidates, string modelId)
    {
        var list = candidates.ToList();
        if (list.Count <= 1 || _perfRepo is null) return list;

        TriggerCacheRefreshIfStale();
        var summaries = _cachedSummaries;
        if (summaries.Count == 0) return list;

        var perfByAlias = summaries
            .Where(s => string.Equals(s.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(s => s.ServerAlias, s => s, StringComparer.OrdinalIgnoreCase);

        if (perfByAlias.Count == 0) return list;

        var maxP95 = perfByAlias.Values.Max(s => (double)s.P95LatencyMs);

        double ComputeScore(string url)
        {
            var alias = _aliasResolver.GetAlias(url);
            if (!perfByAlias.TryGetValue(alias, out var s))
                return 0.5; // neutral for servers with no history

            // perfScore: lower p95 latency + higher success rate → higher score
            var perfScore = maxP95 > 0
                ? (1.0 - s.P95LatencyMs / maxP95) * s.SuccessRate
                : 0.5;

            // UserChoice is 0.5 (neutral) for fallback candidates — the preferred host
            // is handled by OllamaHostHealthMonitor rule 1.
            // Quality is 0.5 (neutral) until Phase C adds real LLM-scored samples.
            return _config.Weights.UserChoice  * 0.5
                 + _config.Weights.Performance * perfScore
                 + _config.Weights.Quality     * 0.5;
        }

        return list.OrderByDescending(ComputeScore);
    }

    /// <summary>Returns the cached performance summaries (triggers a background refresh if stale).</summary>
    public IReadOnlyList<ModelPerfSummary> GetCachedSummaries()
    {
        TriggerCacheRefreshIfStale();
        return _cachedSummaries;
    }

    // ── Cache management ──────────────────────────────────────────────────────

    private void TriggerCacheRefreshIfStale()
    {
        if (DateTime.UtcNow < _cacheExpiry || _perfRepo is null) return;

        bool shouldRefresh;
        lock (_cacheRefreshLock)
        {
            shouldRefresh = DateTime.UtcNow >= _cacheExpiry;
            if (shouldRefresh)
                // Extend expiry optimistically to prevent concurrent refresh stampede.
                _cacheExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }

        if (shouldRefresh)
            _ = Task.Run(RefreshCacheAsync);
    }

    private async Task RefreshCacheAsync()
    {
        try
        {
            var fresh = await _perfRepo!.GetSummaryAsync(null, null, CacheWindowMinutes);
            _cachedSummaries = fresh;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh routing hints cache");
        }
    }
}
