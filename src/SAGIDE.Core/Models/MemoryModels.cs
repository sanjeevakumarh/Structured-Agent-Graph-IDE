namespace SAGIDE.Core.Models;

/// <summary>Tracks one indexed notes file in the Logseq graph.</summary>
public record NotesFileEntry(
    string FilePath,
    long   FileSize,
    string LastModified,
    string LastIndexed,
    int    ChunkCount,
    bool   HasTasks,
    string ContentHash = "");

/// <summary>Aggregate stats for the notes index.</summary>
public record NotesStats(
    int     TotalFiles,
    int     TotalChunks,
    string? LastIndexTime,
    int     TotalTasks);

/// <summary>One cached web search result row.</summary>
public record SearchCacheEntry(
    string QueryHash,
    string QueryText,
    string ResultText,
    int    ResultCount,
    double QualityScore,
    string Domain,
    string FetchedAt);
