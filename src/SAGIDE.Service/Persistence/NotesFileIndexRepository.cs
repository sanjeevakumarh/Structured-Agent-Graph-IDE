// NotesFileEntry, NotesStats promoted to SAGIDE.Core.Models — aliases for back-compat
global using NotesFileEntry = SAGIDE.Core.Models.NotesFileEntry;
global using NotesStats     = SAGIDE.Core.Models.NotesStats;

using Microsoft.Data.Sqlite;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Tracks which Logseq note files have been indexed and when.
/// Used by <see cref="Rag.NotesIndexerService"/> for delta processing.
/// </summary>
public sealed class NotesFileIndexRepository : SqliteRepositoryBase, INotesFileIndexRepository
{
    public NotesFileIndexRepository(string dbPath) : base(dbPath) { }

    public async Task InitializeAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.CreateNotesFileIndex;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, NotesFileEntry>> GetAllAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectAllNotesFiles;

        var result = new Dictionary<string, NotesFileEntry>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var hashOrd = reader.GetOrdinal("content_hash");
            var entry = new NotesFileEntry(
                reader.GetString(reader.GetOrdinal("file_path")),
                reader.GetInt64(reader.GetOrdinal("file_size")),
                reader.GetString(reader.GetOrdinal("last_modified")),
                reader.GetString(reader.GetOrdinal("last_indexed")),
                reader.GetInt32(reader.GetOrdinal("chunk_count")),
                reader.GetInt32(reader.GetOrdinal("has_tasks")) == 1,
                reader.IsDBNull(hashOrd) ? "" : reader.GetString(hashOrd));
            result[entry.FilePath] = entry;
        }
        return result;
    }

    public async Task UpsertAsync(NotesFileEntry entry)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertNotesFile;
        cmd.Parameters.AddWithValue("@filePath", entry.FilePath);
        cmd.Parameters.AddWithValue("@fileSize", entry.FileSize);
        cmd.Parameters.AddWithValue("@lastModified", entry.LastModified);
        cmd.Parameters.AddWithValue("@lastIndexed", entry.LastIndexed);
        cmd.Parameters.AddWithValue("@chunkCount", entry.ChunkCount);
        cmd.Parameters.AddWithValue("@hasTasks", entry.HasTasks ? 1 : 0);
        cmd.Parameters.AddWithValue("@contentHash", entry.ContentHash);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string filePath)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.DeleteNotesFile;
        cmd.Parameters.AddWithValue("@filePath", filePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearAllAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notes_file_index";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<NotesStats> GetStatsAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectNotesStats;
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new NotesStats(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3));
        }
        return new NotesStats(0, 0, null, 0);
    }
}

// NotesFileEntry promoted to SAGIDE.Core.Models.NotesFileEntry (alias above)

// NotesStats promoted to SAGIDE.Core.Models.NotesStats (alias above)
