using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Memory;

/// <summary>
/// Generates float[] vectors via Ollama /api/embeddings or OpenAI-compatible /v1/embeddings.
/// Batches requests to avoid memory pressure from large document sets.
/// </summary>
public sealed class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly bool _useOpenAiFormat;
    private readonly int _batchSize;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(HttpClient http, IConfiguration configuration, ILogger<EmbeddingService> logger)
    {
        _http      = http;
        _logger    = logger;
        _batchSize = configuration.GetValue("SAGIDE:Rag:EmbeddingBatchSize", 32);

        // Resolve embedding model: first check Ollama servers (by RagOrder),
        // then fall back to OpenAI-compatible servers.
        (_model, _baseUrl, _useOpenAiFormat) = ResolveEmbeddingFromServers(configuration);

        if (string.IsNullOrEmpty(_model))
            _logger.LogWarning("No embedding model found in configured servers — " +
                "RAG embed/search calls will return empty results. " +
                "Add a model whose name contains 'embed' to fix this.");
        else
            _logger.LogInformation("Embedding model resolved: {Model} at {BaseUrl} (format: {Format})",
                _model, _baseUrl, _useOpenAiFormat ? "openai" : "ollama");
    }

    /// <summary>True when an embedding model was found in configuration.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_model);

    private static (string Model, string BaseUrl, bool UseOpenAiFormat) ResolveEmbeddingFromServers(IConfiguration cfg)
    {
        // Scan Ollama servers first (by RagOrder), then OpenAI-compatible servers.
        var ollamaServers = cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren().ToList();

        var ragServers = ollamaServers
            .Where(s => int.TryParse(s["RagOrder"], out _))
            .OrderBy(s => int.Parse(s["RagOrder"]!));

        var otherOllamaServers = ollamaServers
            .Where(s => !int.TryParse(s["RagOrder"], out _));

        foreach (var server in ragServers.Concat(otherOllamaServers))
        {
            var baseUrl = server["BaseUrl"] ?? string.Empty;
            foreach (var entry in server.GetSection("Models").GetChildren())
            {
                var id = entry.Value ?? string.Empty;
                if (id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    return (id, baseUrl, false);
            }
        }

        // Fall back to OpenAI-compatible servers
        var openAiServers = cfg.GetSection("SAGIDE:OpenAICompatible:Servers").GetChildren().ToList();
        foreach (var server in openAiServers)
        {
            var baseUrl = server["BaseUrl"] ?? string.Empty;
            foreach (var entry in server.GetSection("Models").GetChildren())
            {
                var id = entry.Value ?? string.Empty;
                if (id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    return (id, baseUrl, true);
            }
        }

        return (string.Empty, string.Empty, false);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Embed a single string. Returns an empty array on failure.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results.Count > 0 ? results[0] : [];
    }

    /// <summary>Embed a list of <see cref="TextChunk"/> objects. Returns parallel float[][] vectors.</summary>
    public async Task<IReadOnlyList<float[]>> EmbedChunksAsync(
        IReadOnlyList<TextChunk> chunks, CancellationToken ct = default)
    {
        var texts = chunks.Select(c => c.Text).ToList();
        return await EmbedBatchAsync(texts, ct);
    }

    // ── Core implementation ───────────────────────────────────────────────────

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("EmbedBatchAsync called but no embedding model is configured — returning empty");
            return [];
        }

        var results = new List<float[]>(texts.Count);

        for (var i = 0; i < texts.Count; i += _batchSize)
        {
            var batch = texts.Skip(i).Take(_batchSize).ToList();
            _logger.LogDebug("Embedding batch {From}-{To} of {Total} using {Model}",
                i, i + batch.Count, texts.Count, _model);

            foreach (var text in batch)
            {
                var vector = await EmbedSingleAsync(text, ct);
                results.Add(vector);
            }
        }

        return results;
    }

    private async Task<float[]> EmbedSingleAsync(string text, CancellationToken ct)
    {
        try
        {
            if (_useOpenAiFormat)
                return await EmbedSingleOpenAiAsync(text, ct);

            var url      = $"{_baseUrl.TrimEnd('/')}/api/embeddings";
            var payload  = new { model = _model, prompt = text };
            var response = await _http.PostAsJsonAsync(url, payload, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(ct);
            return json?.Embedding ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedding failed for text of length {Len}", text.Length);
            return [];
        }
    }

    private async Task<float[]> EmbedSingleOpenAiAsync(string text, CancellationToken ct)
    {
        var url     = $"{_baseUrl.TrimEnd('/')}/v1/embeddings";
        var payload = new { model = _model, input = text };
        var response = await _http.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("embedding", out var embeddingArr))
        {
            return embeddingArr.EnumerateArray()
                .Select(e => e.GetSingle())
                .ToArray();
        }

        return [];
    }

    // ── Wire types ────────────────────────────────────────────────────────────

    private sealed class OllamaEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}
