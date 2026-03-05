using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists activity log entries and workspace configuration (IActivityRepository).
/// Shares the same SQLite file as <see cref="SqliteTaskRepository"/>;
/// WAL mode allows concurrent reads during writes.
/// </summary>
public sealed class SqliteActivityRepository : SqliteRepositoryBase, IActivityRepository
{
    private readonly ILogger<SqliteActivityRepository> _logger;

    public SqliteActivityRepository(string dbPath, ILogger<SqliteActivityRepository> logger)
        : base(dbPath)
    {
        _logger = logger;
    }

    public async Task SaveActivityAsync(ActivityEntry entry)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertActivity;

        cmd.Parameters.AddWithValue("@id",               entry.Id);
        cmd.Parameters.AddWithValue("@workspacePath",    entry.WorkspacePath);
        cmd.Parameters.AddWithValue("@timestamp",        entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@hourBucket",       entry.HourBucket);
        cmd.Parameters.AddWithValue("@activityType",     entry.ActivityType.ToString());
        cmd.Parameters.AddWithValue("@actor",            entry.Actor);
        cmd.Parameters.AddWithValue("@summary",          entry.Summary);
        cmd.Parameters.AddWithValue("@details",          (object?)entry.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taskId",           (object?)entry.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filePaths",        JsonSerializer.Serialize(entry.FilePaths));
        cmd.Parameters.AddWithValue("@gitCommitHash",    (object?)entry.GitCommitHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata",         JsonSerializer.Serialize(entry.Metadata));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetActivitiesByHourAsync(
        string workspacePath, string hourBucket)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivitiesByHour;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@hourBucket",    hourBucket);

        var activities = new List<ActivityEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            activities.Add(ReadActivityEntry(reader));
        return activities;
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetActivitiesByTimeRangeAsync(
        string workspacePath, DateTime start, DateTime end)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivitiesByTimeRange;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@start",         start.ToString("O"));
        cmd.Parameters.AddWithValue("@end",           end.ToString("O"));

        var activities = new List<ActivityEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            activities.Add(ReadActivityEntry(reader));
        return activities;
    }

    public async Task<IReadOnlyList<string>> GetHourBucketsAsync(string workspacePath, int limit = 100)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectHourBuckets;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@limit",         limit);

        var buckets = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            buckets.Add(reader.GetString(0));
        return buckets;
    }

    public async Task<ActivityLogConfig?> GetConfigAsync(string workspacePath)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivityConfig;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new ActivityLogConfig
        {
            WorkspacePath     = reader.GetString(reader.GetOrdinal("workspace_path")),
            Enabled           = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            GitIntegrationMode= Enum.Parse<GitIntegrationMode>(reader.GetString(reader.GetOrdinal("git_integration_mode"))),
            MarkdownEnabled   = reader.GetInt32(reader.GetOrdinal("markdown_enabled")) == 1,
            CreatedAt         = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt         = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    public async Task SaveConfigAsync(ActivityLogConfig config)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertActivityConfig;

        cmd.Parameters.AddWithValue("@workspacePath",     config.WorkspacePath);
        cmd.Parameters.AddWithValue("@enabled",           config.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@gitIntegrationMode",config.GitIntegrationMode.ToString());
        cmd.Parameters.AddWithValue("@markdownEnabled",   config.MarkdownEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt",         config.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt",         config.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    private static ActivityEntry ReadActivityEntry(SqliteDataReader reader) => new()
    {
        Id             = reader.GetString(reader.GetOrdinal("id")),
        WorkspacePath  = reader.GetString(reader.GetOrdinal("workspace_path")),
        Timestamp      = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
        HourBucket     = reader.GetString(reader.GetOrdinal("hour_bucket")),
        ActivityType   = Enum.Parse<ActivityType>(reader.GetString(reader.GetOrdinal("activity_type"))),
        Actor          = reader.GetString(reader.GetOrdinal("actor")),
        Summary        = reader.GetString(reader.GetOrdinal("summary")),
        Details        = reader.IsDBNull(reader.GetOrdinal("details"))        ? null : reader.GetString(reader.GetOrdinal("details")),
        TaskId         = reader.IsDBNull(reader.GetOrdinal("task_id"))        ? null : reader.GetString(reader.GetOrdinal("task_id")),
        FilePaths      = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
        GitCommitHash  = reader.IsDBNull(reader.GetOrdinal("git_commit_hash"))? null : reader.GetString(reader.GetOrdinal("git_commit_hash")),
        Metadata       = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? []
    };
}
