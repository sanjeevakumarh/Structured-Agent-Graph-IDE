using System.Security.Cryptography;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Memory;

/// <summary>
/// Background service that periodically scans a Logseq graph directory,
/// identifies changed/new markdown files, and indexes them into the RAG vector store.
/// Only processes the delta — files modified since their last indexing.
/// </summary>
public sealed class NotesIndexerService : BackgroundService
{
    private readonly NotesConfig _config;
    private readonly INotesFileIndexRepository _fileIndex;
    private readonly TextChunker _chunker;
    private readonly EmbeddingService _embedder;
    private readonly VectorStore _store;
    private readonly ILogger<NotesIndexerService> _logger;
    private readonly CronExpression _cron;

    public NotesIndexerService(
        NotesConfig config,
        INotesFileIndexRepository fileIndex,
        TextChunker chunker,
        EmbeddingService embedder,
        VectorStore store,
        ILogger<NotesIndexerService> logger)
    {
        _config    = config;
        _fileIndex = fileIndex;
        _chunker   = chunker;
        _embedder  = embedder;
        _store     = store;
        _logger    = logger;
        _cron      = CronExpression.Parse(config.Schedule);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled || string.IsNullOrEmpty(_config.GraphPath))
        {
            _logger.LogInformation("Notes indexer disabled or no GraphPath configured");
            return;
        }

        _logger.LogInformation("Notes indexer started (schedule: {Cron}, path: {Path})",
            _config.Schedule, _config.GraphPath);

        // Run once on startup, then on schedule
        await RunIndexAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = _cron.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
            if (next is null) break;

            var delay = next.Value - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            await RunIndexAsync(stoppingToken);
        }
    }

    /// <summary>Triggers an immediate re-index. Called by REST endpoint.</summary>
    /// <param name="force">When true, clears the file index and all existing chunks to force full re-embedding.</param>
    public async Task ReindexAsync(bool force = false, CancellationToken ct = default)
    {
        if (force)
        {
            _logger.LogInformation("Notes indexer: force reindex — clearing all existing data");
            await _store.DeleteBySourceTagAsync(_config.SourceTag, ct);
            await _fileIndex.ClearAllAsync();
        }
        await RunIndexAsync(ct);
    }

    private async Task RunIndexAsync(CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_config.GraphPath))
            {
                _logger.LogWarning("Notes graph path does not exist: {Path}", _config.GraphPath);
                return;
            }

            if (!_embedder.IsConfigured)
            {
                _logger.LogWarning("Notes indexer: no embedding model configured — skipping");
                return;
            }

            _logger.LogInformation("Notes indexer: scanning {Path}...", _config.GraphPath);

            var existingIndex = await _fileIndex.GetAllAsync();
            var diskFiles = ScanDiskFiles();

            var newOrChanged = 0;
            var unchanged = 0;
            var deleted = 0;
            var totalChunks = 0;

            // Process new/changed files
            foreach (var (filePath, fileInfo) in diskFiles)
            {
                var lastModified = fileInfo.LastWriteTimeUtc.ToString("O");
                existingIndex.TryGetValue(filePath, out var indexed);

                // Fast path: timestamp + size unchanged AND hash already stored → skip
                if (indexed is not null
                    && indexed.LastModified == lastModified
                    && indexed.FileSize == fileInfo.Length
                    && indexed.ContentHash.Length > 0)
                {
                    unchanged++;
                    existingIndex.Remove(filePath);
                    continue;
                }

                // Backfill path: timestamp+size match but no hash yet → compute hash, store it, skip re-embedding
                if (indexed is not null
                    && indexed.LastModified == lastModified
                    && indexed.FileSize == fileInfo.Length
                    && indexed.ContentHash.Length == 0)
                {
                    var backfillContent = await File.ReadAllTextAsync(filePath, ct);
                    var backfillHash = Convert.ToHexStringLower(
                        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(backfillContent)));
                    await _fileIndex.UpsertAsync(indexed with { ContentHash = backfillHash });
                    unchanged++;
                    existingIndex.Remove(filePath);
                    continue;
                }

                // Timestamp/size changed — read content and check hash
                // (Google Drive sync can touch timestamps without content changes)
                var content = await File.ReadAllTextAsync(filePath, ct);
                var contentHash = Convert.ToHexStringLower(
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

                if (indexed is not null && indexed.ContentHash == contentHash)
                {
                    // Content identical — update timestamp in index but skip re-embedding
                    await _fileIndex.UpsertAsync(indexed with
                    {
                        LastModified = lastModified,
                        FileSize = fileInfo.Length,
                        LastIndexed = DateTime.UtcNow.ToString("O")
                    });
                    unchanged++;
                    existingIndex.Remove(filePath);
                    continue;
                }

                var chunkCount = await IndexFileAsync(filePath, content, fileInfo, contentHash, ct);
                totalChunks += chunkCount;
                newOrChanged++;
                existingIndex.Remove(filePath);
            }

            // Remove stale entries (files deleted from disk)
            foreach (var stale in existingIndex.Keys)
            {
                await _store.DeleteBySourceUrlAsync(stale, ct);
                await _fileIndex.DeleteAsync(stale);
                deleted++;
            }

            _logger.LogInformation(
                "Notes indexer complete: {Changed} changed ({Chunks} chunks), {Unchanged} unchanged, {Deleted} deleted",
                newOrChanged, totalChunks, unchanged, deleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notes indexer failed");
        }
    }

    private const int MinChunkLength = 100;

    private async Task<int> IndexFileAsync(
        string filePath, string content, FileInfo fileInfo, string contentHash, CancellationToken ct)
    {
        try
        {
            var hasTasks = _config.TaskMarkers.Any(m =>
                content.Contains(m, StringComparison.OrdinalIgnoreCase));

            var noteTitle = Path.GetFileNameWithoutExtension(filePath);
            var cleanedContent = StripLogseqSyntax(content);
            var doc = new FetchedDocument(filePath, noteTitle, cleanedContent, DateTime.UtcNow, "local_file");
            var rawChunks = _chunker.ChunkAll([doc]);

            // Filter tiny chunks (noise like link references) and prepend note title for embedding context
            var chunks = rawChunks
                .Where(c => c.Text.Length >= MinChunkLength)
                .Select(c => c with { Text = $"{noteTitle}: {c.Text}" })
                .ToList();

            if (chunks.Count > 0)
            {
                var embeddings = await _embedder.EmbedChunksAsync(chunks, ct);
                // Delete old chunks for this file before upserting new ones
                await _store.DeleteBySourceUrlAsync(filePath, ct);
                await _store.UpsertAsync(chunks, embeddings, _config.SourceTag, ct);
            }
            else
            {
                // All chunks were filtered out — still remove old stale data
                await _store.DeleteBySourceUrlAsync(filePath, ct);
            }

            await _fileIndex.UpsertAsync(new NotesFileEntry(
                filePath,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.ToString("O"),
                DateTime.UtcNow.ToString("O"),
                chunks.Count,
                hasTasks,
                contentHash));

            return chunks.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index note: {File}", filePath);
            return 0;
        }
    }

    /// <summary>
    /// Strips Logseq-specific formatting to produce cleaner text for embedding:
    /// bullet markers (- ), wikilinks ([[...]]), block references (((...)))
    /// </summary>
    private static string StripLogseqSyntax(string content)
    {
        var sb = new System.Text.StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            // Strip wikilink brackets: [[text]] → text
            if (i + 1 < content.Length && content[i] == '[' && content[i + 1] == '[')
            {
                var end = content.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    sb.Append(content, i + 2, end - i - 2);
                    i = end + 2;
                    continue;
                }
            }

            // Strip block references: ((uuid)) → empty
            if (i + 1 < content.Length && content[i] == '(' && content[i + 1] == '(')
            {
                var end = content.IndexOf("))", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    i = end + 2;
                    continue;
                }
            }

            // Strip leading bullet markers at start of line: "- " or "  - "
            if (content[i] == '\n')
            {
                sb.Append('\n');
                i++;
                // Skip whitespace + single dash + space
                var lineStart = i;
                while (i < content.Length && content[i] == ' ') i++;
                if (i < content.Length && content[i] == '-' && i + 1 < content.Length && content[i + 1] == ' ')
                {
                    i += 2; // skip "- "
                }
                else
                {
                    i = lineStart; // not a bullet line, rewind
                }
                continue;
            }

            sb.Append(content[i]);
            i++;
        }

        return sb.ToString();
    }

    private Dictionary<string, FileInfo> ScanDiskFiles()
    {
        var result = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var excludes = new HashSet<string>(_config.ExcludeFolders, StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in _config.FilePatterns)
        {
            foreach (var file in Directory.EnumerateFiles(_config.GraphPath, pattern, SearchOption.AllDirectories))
            {
                // Skip excluded folders
                var relativePath = Path.GetRelativePath(_config.GraphPath, file);
                var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => excludes.Contains(p)))
                    continue;

                result[file] = new FileInfo(file);
            }
        }

        return result;
    }
}
