using SAGIDE.Contracts;
using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Unified entry point for the Agent OS memory plane.
///
/// The memory system has two responsibilities:
///   1. <b>Indexing</b> — fetching data sources, chunking, embedding, and storing
///      in the vector store so they can be retrieved later.
///   2. <b>Retrieval</b> — embedding a query and returning the top-K most relevant
///      chunks as a formatted context string ready for prompt injection.
///
/// The concrete implementation (<c>RagPipeline</c>) lives in <c>SAGIDE.Service</c>
/// today and will move to <c>SAGIDE.Memory</c> in a future phase. Callers that
/// depend on this interface require no changes when that move happens.
/// </summary>
public interface IMemorySystem
{
    /// <summary>
    /// Index all data sources defined in <paramref name="prompt"/>:
    /// fetch → chunk → embed → store.
    /// </summary>
    Task IndexDataSourcesAsync(PromptDefinition prompt, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the top-<paramref name="topK"/> most relevant chunks for
    /// <paramref name="query"/> and return them as a formatted context string.
    /// </summary>
    Task<string> GetRelevantContextAsync(
        string query,
        int topK = 5,
        string? sourceTag = null,
        CancellationToken ct = default);

    /// <summary>
    /// Convenience: index sources then immediately retrieve context.
    /// Useful for on-demand prompts where freshness matters.
    /// </summary>
    Task<string> FetchAndGetContextAsync(
        PromptDefinition prompt,
        string query,
        int topK = 5,
        CancellationToken ct = default);
}
