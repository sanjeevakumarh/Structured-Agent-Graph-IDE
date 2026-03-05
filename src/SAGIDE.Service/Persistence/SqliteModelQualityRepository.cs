using Microsoft.Data.Sqlite;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists and queries LLM output quality samples (IModelQualityRepository).
/// Shares the same SQLite file as SqliteTaskRepository.
/// </summary>
public sealed class SqliteModelQualityRepository : SqliteRepositoryBase, IModelQualityRepository
{
    public SqliteModelQualityRepository(string dbPath) : base(dbPath) { }

    public async Task InsertSampleAsync(ModelQualitySample sample)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertModelQualitySample;
        cmd.Parameters.AddWithValue("@id",              sample.Id);
        cmd.Parameters.AddWithValue("@provider",        sample.Provider);
        cmd.Parameters.AddWithValue("@modelId",         sample.ModelId);
        cmd.Parameters.AddWithValue("@serverAlias",     sample.ServerAlias);
        cmd.Parameters.AddWithValue("@scoredAt",        sample.ScoredAt.ToString("O"));
        cmd.Parameters.AddWithValue("@score",           sample.Score);
        cmd.Parameters.AddWithValue("@referenceTaskId", sample.ReferenceTaskId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ModelQualitySample>> GetRecentScoresAsync(
        string? modelId, string? serverAlias, int limit = 20)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectRecentQualitySamples;
        cmd.Parameters.AddWithValue("@modelId",     (object?)modelId     ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@serverAlias", (object?)serverAlias ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit",       limit);

        var results = new List<ModelQualitySample>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ModelQualitySample(
                Id:              reader.GetString(0),
                Provider:        reader.GetString(1),
                ModelId:         reader.GetString(2),
                ServerAlias:     reader.GetString(3),
                ScoredAt:        DateTime.Parse(reader.GetString(4)),
                Score:           reader.GetDouble(5),
                ReferenceTaskId: reader.GetString(6)));
        }

        return results;
    }

    public async Task PruneOldSamplesAsync(int retentionDays)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("O");

        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.PruneOldQualitySamples;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        await cmd.ExecuteNonQueryAsync();
    }
}
