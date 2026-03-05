using Microsoft.Data.Sqlite;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists and queries per-call LLM performance samples (IModelPerfRepository).
/// Shares the same SQLite file as SqliteTaskRepository.
/// </summary>
public sealed class SqliteModelPerfRepository : SqliteRepositoryBase, IModelPerfRepository
{
    public SqliteModelPerfRepository(string dbPath) : base(dbPath) { }

    public async Task InsertSampleAsync(ModelPerfSample sample)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertModelPerfSample;
        cmd.Parameters.AddWithValue("@id",           sample.Id);
        cmd.Parameters.AddWithValue("@provider",     sample.Provider);
        cmd.Parameters.AddWithValue("@modelId",      sample.ModelId);
        cmd.Parameters.AddWithValue("@serverAlias",  sample.ServerAlias);
        cmd.Parameters.AddWithValue("@startedAt",    sample.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@latencyMs",    sample.LatencyMs);
        cmd.Parameters.AddWithValue("@tokensInput",  sample.TokensInput);
        cmd.Parameters.AddWithValue("@tokensOutput", sample.TokensOutput);
        cmd.Parameters.AddWithValue("@status",       sample.Status);
        cmd.Parameters.AddWithValue("@errorCode",    (object?)sample.ErrorCode ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ModelPerfSummary>> GetSummaryAsync(
        string? modelId, string? serverAlias, int windowMinutes)
    {
        var since = DateTime.UtcNow.AddMinutes(-windowMinutes).ToString("O");

        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();

        // Build query with optional filters
        var where = new List<string> { "started_at >= @since" };
        cmd.Parameters.AddWithValue("@since", since);
        if (!string.IsNullOrEmpty(modelId))
        {
            where.Add("model_id = @modelId");
            cmd.Parameters.AddWithValue("@modelId", modelId);
        }
        if (!string.IsNullOrEmpty(serverAlias))
        {
            where.Add("server_alias = @serverAlias");
            cmd.Parameters.AddWithValue("@serverAlias", serverAlias);
        }

        cmd.CommandText = $"""
            SELECT model_id, server_alias, latency_ms, tokens_input, tokens_output, status, started_at
            FROM model_perf_samples
            WHERE {string.Join(" AND ", where)}
            ORDER BY model_id, server_alias, latency_ms ASC
            """;

        // Collect raw rows
        var rows = new List<(string ModelId, string ServerAlias, long LatencyMs, int TokensIn, int TokensOut, string Status, DateTime StartedAt)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                DateTime.Parse(reader.GetString(6))));
        }

        if (rows.Count == 0) return [];

        // Group and compute stats in C# — avoids complex SQLite percentile math
        var summaries = new List<ModelPerfSummary>();
        foreach (var group in rows.GroupBy(r => (r.ModelId, r.ServerAlias)))
        {
            var sorted   = group.OrderBy(r => r.LatencyMs).ToList();
            var count    = sorted.Count;
            var p50      = sorted[count / 2].LatencyMs;
            var p95      = sorted[Math.Min((int)(count * 0.95), count - 1)].LatencyMs;
            var successes = group.Count(r => r.Status == "success");
            var totalTokens   = group.Sum(r => r.TokensIn + r.TokensOut);
            var totalLatencyMs = group.Sum(r => r.LatencyMs);
            var tokensPerSec = totalLatencyMs > 0
                ? (double)totalTokens / totalLatencyMs * 1000
                : 0;

            summaries.Add(new ModelPerfSummary(
                ModelId:      group.Key.ModelId,
                ServerAlias:  group.Key.ServerAlias,
                SampleCount:  count,
                SuccessCount: successes,
                P50LatencyMs: p50,
                P95LatencyMs: p95,
                SuccessRate:  (double)successes / count,
                TokensPerSec: Math.Round(tokensPerSec, 1),
                WindowStart:  group.Min(r => r.StartedAt),
                WindowEnd:    group.Max(r => r.StartedAt)));
        }

        return summaries;
    }

    public async Task PruneOldSamplesAsync(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");

        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM model_perf_samples WHERE started_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        await cmd.ExecuteNonQueryAsync();
    }
}
