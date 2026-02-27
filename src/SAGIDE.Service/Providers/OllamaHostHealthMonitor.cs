using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Providers;

/// <summary>
/// Resource-Aware Multi-Host Scheduler.
/// Background service that periodically polls every configured Ollama server's /api/ps
/// endpoint to determine which models are currently loaded in VRAM.
///
/// Exposes TryGetBestHost() which routes model requests to:
///   1. A reachable server that already has the model hot in VRAM  (avoids swap time)
///   2. Any other reachable server from the known set               (failover)
///   3. null — caller falls back to static routing table
/// </summary>
public sealed class OllamaHostHealthMonitor : BackgroundService
{
    private readonly List<string> _allUrls; // all known Ollama base URLs
    private readonly ILogger<OllamaHostHealthMonitor> _logger;
    private readonly ConcurrentDictionary<string, HostState> _state = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private sealed record HostState(
        bool IsReachable,
        IReadOnlyList<string> LoadedModels,
        DateTime LastSeen);

    public OllamaHostHealthMonitor(
        IEnumerable<string> knownUrls,
        ILogger<OllamaHostHealthMonitor> logger)
    {
        _logger  = logger;
        _allUrls = knownUrls.Select(u => u.TrimEnd('/')).Distinct().ToList();

        // Initialize all hosts as unknown until the first poll
        foreach (var url in _allUrls)
            _state[url] = new HostState(false, [], DateTime.MinValue);
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_allUrls.Count == 0) return;

        _logger.LogInformation(
            "OllamaHostHealthMonitor started — tracking {Count} server(s): {Urls}",
            _allUrls.Count, string.Join(", ", _allUrls));

        // Poll immediately so routing is available before the first workflow step
        await PollAllAsync(ct);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(ct))
            await PollAllAsync(ct);
    }

    // ── Public query API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the best Ollama base URL to use for the given model:
    ///   1. <paramref name="preferredUrl"/> if it is reachable (respects static routing)
    ///   2. A reachable URL from <paramref name="candidates"/> that has the model already loaded
    ///   3. Any other reachable URL from <paramref name="candidates"/>
    ///   4. null — all candidates are unreachable
    /// </summary>
    public string? TryGetBestHost(string modelId, string preferredUrl, IEnumerable<string> candidates)
    {
        var list = candidates.Select(u => u.TrimEnd('/')).Distinct().ToList();

        // rule 1: honor static routing if the preferred host is reachable
        var normalized = preferredUrl.TrimEnd('/');
        if (_state.TryGetValue(normalized, out var preferred) && preferred.IsReachable)
            return normalized;

        // rule 2: pick a reachable host that has the model warm in VRAM
        var warmHost = list.FirstOrDefault(url =>
            _state.TryGetValue(url, out var s) && s.IsReachable &&
            s.LoadedModels.Contains(modelId, StringComparer.OrdinalIgnoreCase));
        if (warmHost is not null)
        {
            _logger.LogInformation(
                "OllamaHealthMonitor: routing {Model} to warm host {Url} (preferred {Pref} unreachable)",
                modelId, warmHost, preferredUrl);
            return warmHost;
        }

        // rule 3: any reachable candidate
        var anyReachable = list.FirstOrDefault(url =>
            _state.TryGetValue(url, out var s) && s.IsReachable);
        if (anyReachable is not null)
        {
            _logger.LogInformation(
                "OllamaHealthMonitor: routing {Model} to fallback host {Url} (preferred {Pref} unreachable, no warm host)",
                modelId, anyReachable, preferredUrl);
        }
        return anyReachable;
    }

    /// <summary>Returns true if the given server was reachable on the last poll.</summary>
    public bool IsReachable(string baseUrl)
        => _state.TryGetValue(baseUrl.TrimEnd('/'), out var s) && s.IsReachable;

    /// <summary>Returns the models currently loaded in VRAM on the given server.</summary>
    public IReadOnlyList<string> GetLoadedModels(string baseUrl)
        => _state.TryGetValue(baseUrl.TrimEnd('/'), out var s) ? s.LoadedModels : [];

    /// <summary>
    /// Test seam: directly sets the observed state for a host without performing an HTTP poll.
    /// Only used by unit tests via <c>[assembly: InternalsVisibleTo("SAGIDE.Service.Tests")]</c>.
    /// </summary>
    internal void SimulateHostState(string baseUrl, bool isReachable, IReadOnlyList<string> loadedModels)
        => _state[baseUrl.TrimEnd('/')] = new HostState(isReachable, loadedModels, DateTime.UtcNow);

    // ── Private polling ───────────────────────────────────────────────────────

    private Task PollAllAsync(CancellationToken ct)
        => Task.WhenAll(_allUrls.Select(url => PollOneAsync(url, ct)));

    private async Task PollOneAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{baseUrl}/api/ps", ct);

            if (!response.IsSuccessStatusCode)
            {
                _state[baseUrl] = new HostState(false, [], DateTime.UtcNow);
                return;
            }

            var json    = await response.Content.ReadAsStringAsync(ct);
            var doc     = JsonDocument.Parse(json);
            var models  = new List<string>();

            if (doc.RootElement.TryGetProperty("models", out var modelsEl))
            {
                foreach (var m in modelsEl.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var nameEl) &&
                        nameEl.GetString() is { Length: > 0 } name)
                        models.Add(name);
                }
            }

            var wasReachable = _state.TryGetValue(baseUrl, out var prev) && prev.IsReachable;
            _state[baseUrl] = new HostState(true, models, DateTime.UtcNow);

            if (!wasReachable)
                _logger.LogInformation(
                    "Ollama {Url} is now reachable ({Count} model(s) loaded: {Models})",
                    baseUrl, models.Count, string.Join(", ", models));
            else
                _logger.LogDebug(
                    "Ollama {Url}: {Count} model(s) in VRAM: {Models}",
                    baseUrl, models.Count, string.Join(", ", models));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var wasReachable = _state.TryGetValue(baseUrl, out var prev) && prev.IsReachable;
            _state[baseUrl] = new HostState(false, [], DateTime.UtcNow);
            if (wasReachable)
                _logger.LogWarning("Ollama {Url} became unreachable: {Msg}", baseUrl, ex.Message);
            else
                _logger.LogDebug("Ollama {Url}: unreachable ({Msg})", baseUrl, ex.Message);
        }
    }
}
