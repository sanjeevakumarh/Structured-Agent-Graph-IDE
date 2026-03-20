using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Scores LLM outputs on a 0-100 scale using a lightweight model (e.g. edge/localhost).
/// Designed for fast, cheap quality checks — not production grading.
/// </summary>
public sealed partial class QualityScorer
{
    private readonly QualityScoringConfig _config;
    private readonly ProviderFactory _providerFactory;
    private readonly EndpointAliasResolver _aliasResolver;
    private readonly ILogger<QualityScorer> _logger;

    private const string ScoringPromptPrefix = """
        You are an expert AI output quality evaluator.
        Score the model output below on a scale of 0 to 100 based on:
        - Accuracy and factual correctness (no hallucinations)
        - Coherence and logical flow
        - Relevance to the input request
        - Completeness (addresses all aspects of the input)

        Return ONLY a valid JSON object with score and reason fields.
        Example: {"score": 75, "reason": "Good coverage but missing detail on X"}
        No explanation, no markdown fences, no other text.
        """;

    public QualityScorer(
        QualityScoringConfig config,
        ProviderFactory providerFactory,
        EndpointAliasResolver aliasResolver,
        ILogger<QualityScorer> logger)
    {
        _config = config;
        _providerFactory = providerFactory;
        _aliasResolver = aliasResolver;
        _logger = logger;
    }

    public bool ShouldScoreStep => _config.IsStepMode;
    public bool ShouldScoreWorkflow => _config.IsWorkflowMode || _config.IsStepMode;

    /// <summary>
    /// Score a single LLM response. Returns (score 0-100, reason) or (-1, error) on failure.
    /// Input is truncated to keep the scoring prompt small and fast.
    /// </summary>
    public async Task<QualityScore> ScoreAsync(
        string input, string output, string label, CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(output))
            return new QualityScore(-1, "scoring_disabled_or_empty", label);

        var truncatedInput = Truncate(input, 2000);
        var truncatedOutput = Truncate(output, 4000);
        var prompt = $"{ScoringPromptPrefix}\n\nINPUT:\n{truncatedInput}\n\nMODEL OUTPUT:\n{truncatedOutput}\n\nScore this output (0-100):";

        // Try primary scoring model, then fallback
        foreach (var modelSpec in new[] { _config.ScoringModel, _config.FallbackScoringModel })
        {
            if (string.IsNullOrEmpty(modelSpec)) continue;
            try
            {
                var (provider, modelId, endpoint) = ParseModelSpec(modelSpec);
                var agentProvider = _providerFactory.GetProvider(provider);
                if (agentProvider is null) continue;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));

                var model = new ModelConfig(provider, modelId, Endpoint: endpoint);
                var response = await agentProvider.CompleteAsync(prompt, model, cts.Token);

                var parsed = ParseScoreResponse(response);
                if (parsed is not null)
                {
                    _logger.LogInformation(
                        "Quality score for '{Label}': {Score}/100 — {Reason} (model: {Model})",
                        label, parsed.Value.Score, parsed.Value.Reason, modelSpec);
                    return new QualityScore(parsed.Value.Score, parsed.Value.Reason, label);
                }

                _logger.LogWarning("Quality scorer: unparseable response from {Model}: {Response}",
                    modelSpec, Truncate(response, 200));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Quality scorer failed with {Model}", modelSpec);
            }
        }

        return new QualityScore(-1, "scoring_failed", label);
    }

    private static (int Score, string Reason)? ParseScoreResponse(string response)
    {
        // Try to extract {"score": N, "reason": "..."} from response
        // The model may wrap it in markdown or add extra text
        var match = ScoreJsonRegex().Match(response);
        if (!match.Success) return null;

        try
        {
            var json = match.Value;
            using var doc = JsonDocument.Parse(json);
            var score = doc.RootElement.GetProperty("score").GetInt32();
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            if (score is >= 0 and <= 100)
                return (score, reason);
        }
        catch { /* ignore parse failures */ }
        return null;
    }

    private (ModelProvider Provider, string ModelId, string? Endpoint) ParseModelSpec(string spec)
    {
        ModelProvider provider;
        if (spec.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Ollama; spec = spec[7..]; }
        else if (spec.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Codex; spec = spec[7..]; }
        else if (spec.StartsWith("codex/", StringComparison.OrdinalIgnoreCase))
        { provider = ModelProvider.Codex; spec = spec[6..]; }
        else
            provider = ModelProvider.Ollama;

        string? endpoint = null;
        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            var machine = spec[(atIdx + 1)..].Trim();
            spec = spec[..atIdx].Trim();
            endpoint = ResolveServerUrl(machine);
        }
        return (provider, spec, endpoint);
    }

    private string? ResolveServerUrl(string machineName)
        => _aliasResolver.Resolve(machineName);

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "\n[...truncated]";

    [GeneratedRegex(@"\{[^{}]*""score""[^{}]*\}", RegexOptions.Singleline)]
    private static partial Regex ScoreJsonRegex();
}

public readonly record struct QualityScore(int Score, string Reason, string Label);
