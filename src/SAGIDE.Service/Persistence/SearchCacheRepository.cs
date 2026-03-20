// SearchCacheEntry promoted to SAGIDE.Core.Models — alias for back-compat
global using SearchCacheEntry = SAGIDE.Core.Models.SearchCacheEntry;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persistent SQLite-backed search result cache. Survives restarts.
/// Keyed by SHA-256 hash of (query + maxResults).
/// </summary>
public sealed class SearchCacheRepository : SqliteRepositoryBase, ISearchCacheRepository
{
    public SearchCacheRepository(string dbPath) : base(dbPath) { }

    public async Task InitializeAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.CreateSearchCache;
        await cmd.ExecuteNonQueryAsync();
    }

    public static string HashQuery(string query, int maxResults) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{query}|{maxResults}")));

    public async Task<SearchCacheEntry?> GetAsync(string queryHash)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectSearchCache;
        cmd.Parameters.AddWithValue("@queryHash", queryHash);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new SearchCacheEntry(
            reader.GetString(reader.GetOrdinal("query_hash")),
            reader.GetString(reader.GetOrdinal("query_text")),
            reader.GetString(reader.GetOrdinal("result_text")),
            reader.GetInt32(reader.GetOrdinal("result_count")),
            reader.GetDouble(reader.GetOrdinal("quality_score")),
            reader.GetString(reader.GetOrdinal("domain")),
            reader.GetString(reader.GetOrdinal("fetched_at")));
    }

    public async Task UpsertAsync(SearchCacheEntry entry)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertSearchCache;
        cmd.Parameters.AddWithValue("@queryHash", entry.QueryHash);
        cmd.Parameters.AddWithValue("@queryText", entry.QueryText);
        cmd.Parameters.AddWithValue("@resultText", entry.ResultText);
        cmd.Parameters.AddWithValue("@resultCount", entry.ResultCount);
        cmd.Parameters.AddWithValue("@qualityScore", entry.QualityScore);
        cmd.Parameters.AddWithValue("@domain", entry.Domain);
        cmd.Parameters.AddWithValue("@fetchedAt", entry.FetchedAt);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PruneAsync(int retentionDays)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.PruneSearchCache;
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays).ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}

// SearchCacheEntry promoted to SAGIDE.Core.Models (alias above)
