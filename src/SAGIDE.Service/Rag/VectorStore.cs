using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Rag;

/// <summary>
/// SQLite-backed vector store for RAG chunks.
/// Embeddings stored as BLOB (float32[]). Retrieval uses brute-force cosine similarity.
/// Upgrade path: swap to ChromaDB if scale exceeds ~100K chunks.
/// </summary>
public sealed class VectorStore
{
    private readonly string _connectionString;
    private readonly ILogger<VectorStore> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public VectorStore(string dbPath, ILogger<VectorStore> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger           = logger;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the rag_chunks table if it doesn't exist. Safe to call multiple times;
    /// also called lazily on first use so no sync GetAwaiter().GetResult() is needed at startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initGate.WaitAsync();
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = CreateRagChunksTable;
            await cmd.ExecuteNonQueryAsync();

            _initialized = true;
            _logger.LogDebug("VectorStore initialized");
        }
        finally { _initGate.Release(); }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Insert or replace chunks. Key is (source_url, chunk_index).</summary>
    public async Task UpsertAsync(
        IReadOnlyList<TextChunk> chunks,
        IReadOnlyList<float[]> embeddings,
        string? sourceTag = null,
        CancellationToken ct = default)
    {
        await InitializeAsync();
        if (chunks.Count != embeddings.Count)
            throw new ArgumentException("chunks and embeddings must have the same length");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx = conn.BeginTransaction();
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk     = chunks[i];
            var embedding = embeddings[i];
            if (embedding.Length == 0) continue;

            var cmd = conn.CreateCommand();
            cmd.CommandText = UpsertChunk;
            cmd.Parameters.AddWithValue("@id",         Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@sourceTag",  (object?)sourceTag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sourceUrl",  chunk.SourceUrl);
            cmd.Parameters.AddWithValue("@chunkIndex", chunk.ChunkIndex);
            cmd.Parameters.AddWithValue("@chunkText",  chunk.Text);
            cmd.Parameters.AddWithValue("@embedding",  FloatsToBytes(embedding));
            cmd.Parameters.AddWithValue("@createdAt",  DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);

        _logger.LogInformation("VectorStore upserted {Count} chunks (tag={Tag})", chunks.Count, sourceTag ?? "none");
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brute-force cosine similarity search.
    /// Returns the top <paramref name="topK"/> most similar chunks.
    /// </summary>
    public async Task<IReadOnlyList<RankedChunk>> SearchAsync(
        float[] queryVector,
        int topK = 5,
        string? sourceTag = null,
        CancellationToken ct = default)
    {
        await InitializeAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrEmpty(sourceTag) ? SelectAllChunks : SelectChunksByTag;
        if (!string.IsNullOrEmpty(sourceTag))
            cmd.Parameters.AddWithValue("@sourceTag", sourceTag);

        var candidates = new List<(string text, string url, int idx, float[] vec)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var blob = (byte[])reader["embedding"];
            var vec  = BytesToFloats(blob);
            candidates.Add((
                reader.GetString(reader.GetOrdinal("chunk_text")),
                reader.GetString(reader.GetOrdinal("source_url")),
                reader.GetInt32(reader.GetOrdinal("chunk_index")),
                vec));
        }

        // Score + rank
        return candidates
            .Select(c => new RankedChunk(
                new TextChunk(c.text, c.url, c.idx),
                CosineSimilarity(queryVector, c.vec)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    // ── Delete ──────────────────────────────────────────────────────────────────

    /// <summary>Delete all chunks for a specific source URL (e.g. a single file path).</summary>
    public async Task DeleteBySourceUrlAsync(string sourceUrl, CancellationToken ct = default)
    {
        await InitializeAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rag_chunks WHERE source_url = @sourceUrl";
        cmd.Parameters.AddWithValue("@sourceUrl", sourceUrl);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("VectorStore deleted {Count} chunks for {Url}", deleted, sourceUrl);
    }

    /// <summary>Delete all chunks whose source_url starts with the given prefix.</summary>
    public async Task DeleteBySourceUrlPrefixAsync(string urlPrefix, CancellationToken ct = default)
    {
        await InitializeAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        // Escape LIKE wildcards in the caller-supplied prefix so a URL that happens
        // to contain '%' or '_' does not accidentally match unintended rows.
        // NOTE: the backslash replacement MUST come first; if it were done after the
        // '%' or '_' replacements, the newly introduced backslashes would themselves
        // be double-escaped, corrupting the pattern.
        var escapedPrefix = urlPrefix
            .Replace("\\", "\\\\")
            .Replace("%",  "\\%")
            .Replace("_",  "\\_");
        cmd.CommandText = "DELETE FROM rag_chunks WHERE source_url LIKE @prefix ESCAPE '\\'";
        cmd.Parameters.AddWithValue("@prefix", escapedPrefix + "%");
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("VectorStore deleted {Count} chunks with prefix {Prefix}", deleted, urlPrefix);
    }

    public async Task DeleteBySourceTagAsync(string sourceTag, CancellationToken ct = default)
    {
        await InitializeAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rag_chunks WHERE source_tag = @tag";
        cmd.Parameters.AddWithValue("@tag", sourceTag);
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("VectorStore deleted {Count} chunks with source_tag {Tag}", deleted, sourceTag);
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    private const string CreateRagChunksTable = """
        CREATE TABLE IF NOT EXISTS rag_chunks (
            id          TEXT NOT NULL,
            source_tag  TEXT,
            source_url  TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            chunk_text  TEXT NOT NULL,
            embedding   BLOB NOT NULL,
            created_at  TEXT NOT NULL,
            PRIMARY KEY (source_url, chunk_index)
        );
        CREATE INDEX IF NOT EXISTS idx_rag_source_tag ON rag_chunks(source_tag);
        """;

    private const string UpsertChunk = """
        INSERT INTO rag_chunks (id, source_tag, source_url, chunk_index, chunk_text, embedding, created_at)
        VALUES (@id, @sourceTag, @sourceUrl, @chunkIndex, @chunkText, @embedding, @createdAt)
        ON CONFLICT(source_url, chunk_index) DO UPDATE SET
            chunk_text = @chunkText, embedding = @embedding, source_tag = @sourceTag, created_at = @createdAt
        """;

    private const string SelectAllChunks =
        "SELECT source_url, chunk_index, chunk_text, embedding FROM rag_chunks";

    private const string SelectChunksByTag =
        "SELECT source_url, chunk_index, chunk_text, embedding FROM rag_chunks WHERE source_tag = @sourceTag";

    // ── Math helpers ──────────────────────────────────────────────────────────

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0f : dot / denom;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        MemoryMarshal.AsBytes(floats.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return floats;
    }
}

/// <summary>A chunk paired with its cosine similarity score.</summary>
public record RankedChunk(TextChunk Chunk, float Score);
