using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Orchestrates the full RAG pipeline: fetch → chunk → embed → store → retrieve.
/// Used by the PromptTemplate renderer to inject {{rag_context}} into prompts.
/// </summary>
public sealed class RagPipeline
{
    private readonly WebFetcher _fetcher;
    private readonly TextChunker _chunker;
    private readonly EmbeddingService _embedder;
    private readonly VectorStore _store;
    private readonly ILogger<RagPipeline> _logger;

    public RagPipeline(
        WebFetcher fetcher,
        TextChunker chunker,
        EmbeddingService embedder,
        VectorStore store,
        ILogger<RagPipeline> logger)
    {
        _fetcher  = fetcher;
        _chunker  = chunker;
        _embedder = embedder;
        _store    = store;
        _logger   = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full indexing pipeline for a prompt's data sources:
    /// fetch all sources → chunk text → embed chunks → store in vector DB.
    /// </summary>
    public async Task IndexDataSourcesAsync(
        PromptDefinition prompt,
        CancellationToken ct = default)
    {
        _logger.LogInformation("RAG indexing: {Domain}/{Name} ({Count} sources)",
            prompt.Domain, prompt.Name, prompt.DataSources.Count);

        var docs = await _fetcher.FetchDataSourcesAsync(prompt.DataSources, ct);
        if (docs.Count == 0)
        {
            _logger.LogWarning("RAG indexing: no documents fetched for {Domain}/{Name}", prompt.Domain, prompt.Name);
            return;
        }

        var chunks     = _chunker.ChunkAll(docs);
        var embeddings = await _embedder.EmbedChunksAsync(chunks, ct);
        await _store.UpsertAsync(chunks, embeddings, prompt.SourceTag, ct);

        _logger.LogInformation("RAG indexed {Docs} docs → {Chunks} chunks for {Domain}/{Name}",
            docs.Count, chunks.Count, prompt.Domain, prompt.Name);
    }

    /// <summary>
    /// Retrieves the top-K most relevant chunks for a query and formats them
    /// as a context string ready to inject into a Scriban template.
    /// </summary>
    public async Task<string> GetRelevantContextAsync(
        string query,
        int topK = 5,
        string? sourceTag = null,
        CancellationToken ct = default)
    {
        var queryVector = await _embedder.EmbedAsync(query, ct);
        if (queryVector.Length == 0) return string.Empty;

        var results = await _store.SearchAsync(queryVector, topK, sourceTag, ct);
        if (results.Count == 0) return string.Empty;

        // Format as numbered context blocks for the LLM
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"[{i + 1}] Source: {r.Chunk.SourceUrl}");
            sb.AppendLine(r.Chunk.Text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Convenience: index sources then immediately retrieve context for a query.
    /// Useful for on-demand prompts where freshness matters.
    /// </summary>
    public async Task<string> FetchAndGetContextAsync(
        PromptDefinition prompt,
        string query,
        int topK = 5,
        CancellationToken ct = default)
    {
        await IndexDataSourcesAsync(prompt, ct);
        return await GetRelevantContextAsync(query, topK, prompt.SourceTag, ct);
    }
}
