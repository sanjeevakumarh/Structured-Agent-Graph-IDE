namespace SAGIDE.Core.Models;

/// <summary>
/// A piece of content retrieved from an external data source (HTTP, RSS, local file).
/// </summary>
public record FetchedDocument(
    string Url,
    string Title,
    string Body,
    DateTime FetchedAt,
    string SourceType   // "http" | "rss" | "local_file"
);

/// <summary>
/// A chunk of text produced by <c>TextChunker</c>, ready for embedding.
/// </summary>
public record TextChunk(
    string Text,
    string SourceUrl,
    int ChunkIndex,
    string? SourceType = null
);
