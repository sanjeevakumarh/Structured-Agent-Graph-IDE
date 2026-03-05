using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists and loads scheduler last-fired timestamps (ISchedulerRepository).
/// Shares the same SQLite file as <see cref="SqliteTaskRepository"/>.
/// </summary>
public sealed class SqliteSchedulerRepository : SqliteRepositoryBase, ISchedulerRepository
{
    public SqliteSchedulerRepository(string dbPath) : base(dbPath) { }

    public async Task<DateTimeOffset?> GetLastFiredAtAsync(string promptKey)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectLastFiredAt;
        cmd.Parameters.AddWithValue("@promptKey", promptKey);

        var raw = await cmd.ExecuteScalarAsync();
        return raw is string s ? DateTimeOffset.Parse(s) : null;
    }

    public async Task SetLastFiredAtAsync(string promptKey, DateTimeOffset firedAt)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertSchedulerState;
        cmd.Parameters.AddWithValue("@promptKey",   promptKey);
        cmd.Parameters.AddWithValue("@lastFiredAt", firedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task LoadAllLastFiredAsync(IDictionary<string, DateTimeOffset> target)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectAllSchedulerState;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var ts  = DateTimeOffset.Parse(reader.GetString(1));
            target[key] = ts;
        }
    }
}
