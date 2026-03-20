using SAGIDE.Core.Models;
using SAGIDE.Service.Persistence;
using SAGIDE.Service.Providers;
using SAGIDE.Memory;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Api;

public static class NotesEndpoints
{
    public static void MapNotesEndpoints(this WebApplication app)
    {
        app.MapPost("/api/notes/reindex", (NotesIndexerService indexer, CancellationToken ct) =>
        {
            _ = Task.Run(() => indexer.ReindexAsync(false, ct), ct);
            return Results.Accepted(value: new { message = "Reindex triggered" });
        });

        app.MapPost("/api/notes/reindex/full", (NotesIndexerService indexer, CancellationToken ct) =>
        {
            _ = Task.Run(() => indexer.ReindexAsync(true, ct), ct);
            return Results.Accepted(value: new { message = "Full reindex triggered — all chunks will be re-embedded" });
        });

        app.MapGet("/api/notes/stats", async (NotesFileIndexRepository repo) =>
        {
            var stats = await repo.GetStatsAsync();
            return Results.Ok(stats);
        });

        app.MapPost("/api/notes/search", async (
            NotesSearchRequest req,
            RagPipeline rag,
            NotesFileIndexRepository fileIndex,
            NotesConfig config,
            ProviderFactory providerFactory,
            EndpointAliasResolver aliasResolver,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new { error = "Query is required" });

            var topK = req.TopK > 0 ? req.TopK : 10;

            var queryVector = await rag.Embedder.EmbedAsync(req.Query, ct);
            if (queryVector.Length == 0)
                return Results.Ok(new { query = req.Query, results = Array.Empty<object>(),
                    message = "Embedding model not available" });

            var ranked = await rag.Store.SearchAsync(queryVector, topK, config.SourceTag, ct);
            var index = await fileIndex.GetAllAsync();

            var results = ranked.Select((r, i) =>
            {
                var filePath = r.Chunk.SourceUrl;
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                index.TryGetValue(filePath, out var entry);
                return new NotesSearchResult(
                    rank: i + 1,
                    file: fileName,
                    path: filePath,
                    score: Math.Round(r.Score, 4),
                    lastModified: entry?.LastModified,
                    hasTasks: entry?.HasTasks ?? false,
                    snippet: r.Chunk.Text.Length > 500
                        ? r.Chunk.Text[..500] + "…"
                        : r.Chunk.Text
                );
            }).ToArray();

            // Generate LLM summary if a summary model is configured
            string? summary = null;
            if (results.Length > 0 && !string.IsNullOrEmpty(config.SummaryModel))
            {
                summary = await GenerateSummaryAsync(
                    req.Query, results, config, providerFactory, aliasResolver, ct);
            }

            return Results.Ok(new { query = req.Query, count = results.Length, summary, results });
        });
    }

    private static async Task<string?> GenerateSummaryAsync(
        string query,
        NotesSearchResult[] results,
        NotesConfig config,
        ProviderFactory providerFactory,
        EndpointAliasResolver aliasResolver,
        CancellationToken ct)
    {
        try
        {
            // Try primary, then fallback
            foreach (var modelSpec in new[] { config.SummaryModel, config.SummaryModelFallback })
            {
                if (string.IsNullOrEmpty(modelSpec)) continue;

                var (provider, modelId, endpoint) = ParseModelSpec(modelSpec, aliasResolver);
                var agentProvider = providerFactory.GetProvider(provider);
                if (agentProvider is null) continue;

                var context = string.Join("\n\n", results.Select(r =>
                    $"[{r.file}] (score: {r.score}, modified: {r.lastModified ?? "unknown"})\n{r.snippet}"));

                var prompt = $"""
                    You are a knowledge base assistant. The user searched their personal notes for: "{query}"

                    Below are the top {results.Length} matching note excerpts. Write a concise summary (3-5 sentences)
                    that synthesizes the key information found across these notes relevant to the query.
                    Mention specific note names when referencing information. Be direct and factual.
                    Do NOT list the results — synthesize them into a coherent answer.

                    --- Search Results ---
                    {context}
                    --- End Results ---

                    Summary:
                    """;

                var modelConfig = new ModelConfig(provider, modelId, Endpoint: endpoint);
                var response = await agentProvider.CompleteAsync(prompt, modelConfig, ct);
                if (!string.IsNullOrWhiteSpace(response))
                    return response.Trim();
            }
        }
        catch
        {
            // Summary is best-effort — silently return null on failure
        }

        return null;
    }

    /// <summary>Parses "ollama/model@machine" into (Provider, ModelId, Endpoint).</summary>
    private static (ModelProvider Provider, string ModelId, string? Endpoint) ParseModelSpec(
        string spec, EndpointAliasResolver aliasResolver)
    {
        string? endpoint = null;

        var atIdx = spec.LastIndexOf('@');
        if (atIdx > 0)
        {
            var machine = spec[(atIdx + 1)..].Trim();
            spec = spec[..atIdx].Trim();
            endpoint = aliasResolver.Resolve(machine);
        }

        if (spec.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Ollama, spec[7..], endpoint);

        if (spec.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            return (ModelProvider.Claude, spec, endpoint);

        return (ModelProvider.Ollama, spec, endpoint);
    }

    public record NotesSearchRequest(string Query, int TopK = 10);

    public record NotesSearchResult(
        int rank, string file, string path, double score,
        string? lastModified, bool hasTasks, string snippet);
}
