using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Calls the Ollama /api/embeddings endpoint to generate float[] vectors.
/// Batches requests to avoid memory pressure from large document sets.
/// </summary>
public sealed class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly int _batchSize;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(HttpClient http, IConfiguration configuration, ILogger<EmbeddingService> logger)
    {
        _http      = http;
        _logger    = logger;
        _batchSize = configuration.GetValue("SAGIDE:Rag:EmbeddingBatchSize", 32);

        // Resolve embedding model and its server URL from SAGIDE:Ollama:Servers
        // by finding the first model whose name contains "embed".
        (_model, _baseUrl) = ResolveEmbeddingFromServers(configuration);

        if (string.IsNullOrEmpty(_model))
            _logger.LogWarning("No embedding model found in SAGIDE:Ollama:Servers — " +
                "RAG embed/search calls will return empty results. " +
                "Add a model whose name contains 'embed' to fix this.");
        else
            _logger.LogInformation("Embedding model resolved: {Model} at {BaseUrl}", _model, _baseUrl);
    }

    /// <summary>True when an embedding model was found in configuration.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_model);

    private static (string Model, string BaseUrl) ResolveEmbeddingFromServers(IConfiguration cfg)
    {
        // Servers with a numeric RagOrder are scanned first (ascending), then
        // remaining servers in array order. Returns the first embed model found.
        var all = cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren().ToList();

        var ragServers = all
            .Where(s => int.TryParse(s["RagOrder"], out _))
            .OrderBy(s => int.Parse(s["RagOrder"]!));

        var otherServers = all
            .Where(s => !int.TryParse(s["RagOrder"], out _));

        foreach (var server in ragServers.Concat(otherServers))
        {
            var baseUrl = server["BaseUrl"] ?? string.Empty;
            foreach (var entry in server.GetSection("Models").GetChildren())
            {
                var id = entry.Value ?? string.Empty;
                if (id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    return (id, baseUrl);
            }
        }
        return (string.Empty, string.Empty);
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

    // ── Wire types ────────────────────────────────────────────────────────────

    private sealed class OllamaEmbeddingResponse
    {
        public float[]? Embedding { get; set; }
    }
}
