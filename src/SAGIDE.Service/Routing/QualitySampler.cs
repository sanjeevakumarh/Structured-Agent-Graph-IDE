using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Providers;

namespace SAGIDE.Service.Routing;

/// <summary>
/// Idle-capacity quality sampler — after a successful task completes, this class
/// optionally dispatches a direct eval call to the configured eval model (from
/// prompts/shared/model-quality-eval.yaml) to score the original model output
/// and stores the result in <see cref="IModelQualityRepository"/>.
///
/// All evaluation work runs fire-and-forget so it never blocks task execution.
///
/// Safety guards (all must pass before an eval fires):
///   1. QualitySamplingEnabled == true
///   2. task.SourceTag != "quality_probe"  (no recursive probes)
///   3. Model is in QualityProbeAllowlist
///   4. Active probe count &lt; MaxConcurrentProbes
///   5. Random sampling rate check
///   6. Token budget for this hour not exceeded
///   7. TaskQueue depth is low (&lt; 2 pending tasks)
/// </summary>
public sealed class QualitySampler
{
    private readonly RoutingConfig _config;
    private readonly IModelQualityRepository _qualityRepo;
    private readonly ProviderFactory _providerFactory;
    private readonly EndpointAliasResolver _aliasResolver;
    private readonly TaskQueue _taskQueue;
    private readonly PromptRegistry _promptRegistry;
    private readonly ILogger<QualitySampler> _logger;

    // Token budget tracking — resets every hour
    private long _tokensThisHour;
    private DateTime _hourWindowStart = DateTime.UtcNow;
    private readonly object _tokenLock = new();

    // Concurrency guard
    private int _activeProbes;

    public QualitySampler(
        RoutingConfig config,
        IModelQualityRepository qualityRepo,
        ProviderFactory providerFactory,
        EndpointAliasResolver aliasResolver,
        TaskQueue taskQueue,
        PromptRegistry promptRegistry,
        ILogger<QualitySampler> logger)
    {
        _config          = config;
        _qualityRepo     = qualityRepo;
        _providerFactory = providerFactory;
        _aliasResolver   = aliasResolver;
        _taskQueue       = taskQueue;
        _promptRegistry  = promptRegistry;
        _logger          = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called from AgentOrchestrator's success path. All guards are checked internally.
    /// Returns immediately; the eval runs in a background task.
    /// </summary>
    public void Trigger(AgentTask task, string originalPrompt, string originalResponse)
    {
        if (!CheckGuards(task)) return;
        _ = Task.Run(() => RunEvalAsync(task, originalPrompt, originalResponse));
    }

    // ── Guard checks ──────────────────────────────────────────────────────────

    private bool CheckGuards(AgentTask task)
    {
        if (!_config.QualitySamplingEnabled) return false;
        if (task.SourceTag == "quality_probe") return false;

        // Model must be in allowlist (e.g. "ollama/qwen2.5-coder:7b")
        var fullModel = $"{task.ModelProvider.ToString().ToLowerInvariant()}/{task.ModelId}";
        if (!_config.QualityProbeAllowlist.Any(p =>
                string.Equals(p, fullModel, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Concurrency cap
        if (_activeProbes >= _config.MaxConcurrentProbes) return false;

        // Probabilistic sampling
        if (Random.Shared.NextDouble() > _config.SamplingRate) return false;

        // Low queue depth — don't add load when the system is busy
        if (_taskQueue.PendingCount >= 2) return false;

        return true;
    }

    private bool TryReserveTokens(int estimatedTokens)
    {
        lock (_tokenLock)
        {
            // Reset the budget every hour
            if (DateTime.UtcNow - _hourWindowStart > TimeSpan.FromHours(1))
            {
                _tokensThisHour  = 0;
                _hourWindowStart = DateTime.UtcNow;
            }

            if (_tokensThisHour + estimatedTokens > _config.MaxQualitySampleTokensPerHour)
                return false;

            _tokensThisHour += estimatedTokens;
            return true;
        }
    }

    // ── Eval execution ────────────────────────────────────────────────────────

    private async Task RunEvalAsync(AgentTask task, string originalPrompt, string originalResponse)
    {
        Interlocked.Increment(ref _activeProbes);
        try
        {
            // Load the eval prompt template
            var promptDef = _promptRegistry.GetByKey("shared", "model-quality-eval");
            if (promptDef is null)
            {
                _logger.LogDebug("QualitySampler: model-quality-eval.yaml not found in registry; skipping");
                return;
            }

            // Truncate inputs to avoid runaway token costs (eval prompt is short by design)
            var truncatedInput    = Truncate(originalPrompt, 1200);
            var truncatedResponse = Truncate(originalResponse, 1200);

            var rendered = PromptTemplate.Render(promptDef, new Dictionary<string, object>
            {
                ["input"]          = truncatedInput,
                ["primary_output"] = truncatedResponse,
            });

            // Estimate tokens (rough: chars / 4) and check hourly budget
            var estimatedTokens = (rendered.Length + 50) / 4;
            if (!TryReserveTokens(estimatedTokens))
            {
                _logger.LogDebug("QualitySampler: hourly token budget exhausted; skipping");
                return;
            }

            // Eval model comes from model-quality-eval.yaml — no hardcoding in C#
            var evalModelSpec = promptDef.ModelPreference?.Primary ?? string.Empty;
            if (string.IsNullOrWhiteSpace(evalModelSpec))
            {
                _logger.LogDebug("QualitySampler: model-quality-eval.yaml has no model_preference.primary; skipping");
                return;
            }

            var (evalProvider, evalModelId, evalEndpoint) = ParseEvalModelSpec(evalModelSpec);
            var evalProviderInstance = _providerFactory.GetProvider(evalProvider);
            if (evalProviderInstance is null)
            {
                _logger.LogDebug("QualitySampler: eval provider {Provider} not available; skipping", evalProvider);
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string rawScore;
            try
            {
                rawScore = await evalProviderInstance.CompleteAsync(
                    rendered,
                    new ModelConfig(evalProvider, evalModelId, Endpoint: evalEndpoint),
                    cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QualitySampler: eval call to {Provider}/{Model} failed", evalProvider, evalModelId);
                return;
            }

            var score = ParseScore(rawScore);
            if (score < 0)
            {
                _logger.LogDebug("QualitySampler: could not parse score from response: {Response}", rawScore);
                return;
            }

            var endpoint = task.Metadata.GetValueOrDefault("modelEndpoint", "");
            var sample = new ModelQualitySample(
                Id:              Guid.NewGuid().ToString("N")[..16],
                Provider:        task.ModelProvider.ToString(),
                ModelId:         task.ModelId,
                ServerAlias:     _aliasResolver.GetAlias(endpoint),
                ScoredAt:        DateTime.UtcNow,
                Score:           score,
                ReferenceTaskId: task.Id);

            await _qualityRepo.InsertSampleAsync(sample);

            _logger.LogInformation(
                "QualitySampler: scored {Provider}/{Model}@{Server} → {Score}/100 (task {TaskId})",
                task.ModelProvider, task.ModelId, sample.ServerAlias, score, task.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QualitySampler: unexpected error during eval");
        }
        finally
        {
            Interlocked.Decrement(ref _activeProbes);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal model-spec parser — mirrors SubtaskCoordinator.ParseModelSpec for the
    /// eval-only path. Reads alias→URL from EndpointAliasResolver (no C# hardcoding).
    /// </summary>
    private (ModelProvider Provider, string ModelId, string? Endpoint) ParseEvalModelSpec(string spec)
    {
        string? endpoint = null;

        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            var machine = spec[(atIdx + 1)..].Trim();
            spec     = spec[..atIdx].Trim();
            endpoint = _aliasResolver.Resolve(machine);
            if (endpoint is null)
                _logger.LogWarning("QualitySampler: machine alias '{Machine}' not found in server config", machine);
        }

        if (spec.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Ollama, spec[7..], endpoint);

        if (spec.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Claude, spec, endpoint);

        return (ModelProvider.Ollama, spec, endpoint);
    }

    private static double ParseScore(string raw)
    {
        // Strip code fences if Claude wrapped the JSON
        var text = raw.Trim();
        var jsonStart = text.IndexOf('{');
        var jsonEnd   = text.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < 0) return -1;

        var json = text[jsonStart..(jsonEnd + 1)];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("score", out var scoreEl))
            {
                if (scoreEl.ValueKind == JsonValueKind.Number && scoreEl.TryGetDouble(out var d))
                    return Math.Clamp(d, 0, 100);
            }
        }
        catch { /* fall through */ }
        return -1;
    }

    private static string Truncate(string s, int maxChars)
        => s.Length <= maxChars ? s : s[..maxChars] + "…";
}
