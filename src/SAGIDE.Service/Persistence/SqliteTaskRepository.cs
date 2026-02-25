using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

public class SqliteTaskRepository : ITaskRepository, IActivityRepository, IWorkflowRepository, ISchedulerRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteTaskRepository> _logger;

    public SqliteTaskRepository(string dbPath, ILogger<SqliteTaskRepository> logger)
    {
        _connectionString = $"Data Source={dbPath}";
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // WAL allows concurrent reads while a write is in progress.
        // busy_timeout=5000 makes writers wait up to 5 s instead of failing immediately.
        var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = SqlQueries.Pragmas;
        await pragmaCmd.ExecuteNonQueryAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.CreateCoreTables;
        await cmd.ExecuteNonQueryAsync();

        var wfCmd = conn.CreateCommand();
        wfCmd.CommandText = SqlQueries.CreateWorkflowTable;
        await wfCmd.ExecuteNonQueryAsync();

        var cacheTableCmd = conn.CreateCommand();
        cacheTableCmd.CommandText = SqlQueries.CreateOutputCacheTable;
        await cacheTableCmd.ExecuteNonQueryAsync();

        var schedulerTableCmd = conn.CreateCommand();
        schedulerTableCmd.CommandText = SqlQueries.CreateSchedulerStateTable;
        await schedulerTableCmd.ExecuteNonQueryAsync();

        // Schema migrations — ADD COLUMN is idempotent (SQLite throws on duplicate, we catch it)
        foreach (var migrationSql in SqlQueries.Migrations)
        {
            try
            {
                var mc = conn.CreateCommand();
                mc.CommandText = migrationSql;
                await mc.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column"))
            {
                // Column already exists from a previous run — safe to ignore
            }
        }

        _logger.LogInformation("SQLite database initialized at {DbPath}", _connectionString);
    }

    public async Task SaveTaskAsync(AgentTask task)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertTask;

        cmd.Parameters.AddWithValue("@id", task.Id);
        cmd.Parameters.AddWithValue("@agentType", task.AgentType.ToString());
        cmd.Parameters.AddWithValue("@modelProvider", task.ModelProvider.ToString());
        cmd.Parameters.AddWithValue("@modelId", task.ModelId);
        cmd.Parameters.AddWithValue("@description", task.Description);
        cmd.Parameters.AddWithValue("@filePaths", JsonSerializer.Serialize(task.FilePaths));
        cmd.Parameters.AddWithValue("@status", task.Status.ToString());
        cmd.Parameters.AddWithValue("@progress", task.Progress);
        cmd.Parameters.AddWithValue("@statusMessage", (object?)task.StatusMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@priority", task.Priority);
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(task.Metadata));
        cmd.Parameters.AddWithValue("@createdAt", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@startedAt", task.StartedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt", task.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@scheduledFor", task.ScheduledFor?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@comparisonGroupId", (object?)task.ComparisonGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceTag", (object?)task.SourceTag ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveResultAsync(AgentResult result)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertResult;

        cmd.Parameters.AddWithValue("@taskId", result.TaskId);
        cmd.Parameters.AddWithValue("@success", result.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@output", result.Output);
        cmd.Parameters.AddWithValue("@issues", JsonSerializer.Serialize(result.Issues));
        cmd.Parameters.AddWithValue("@changes", JsonSerializer.Serialize(result.Changes));
        cmd.Parameters.AddWithValue("@tokensUsed", result.TokensUsed);
        cmd.Parameters.AddWithValue("@estimatedCost", result.EstimatedCost);
        cmd.Parameters.AddWithValue("@latencyMs", result.LatencyMs);
        cmd.Parameters.AddWithValue("@errorMessage", (object?)result.ErrorMessage ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTaskById;
        cmd.Parameters.AddWithValue("@id", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    public async Task<AgentResult?> GetResultAsync(string taskId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectResultByTaskId;
        cmd.Parameters.AddWithValue("@taskId", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AgentResult
        {
            TaskId = reader.GetString(reader.GetOrdinal("task_id")),
            Success = reader.GetInt32(reader.GetOrdinal("success")) == 1,
            Output = reader.GetString(reader.GetOrdinal("output")),
            Issues = JsonSerializer.Deserialize<List<Issue>>(reader.GetString(reader.GetOrdinal("issues"))) ?? [],
            Changes = JsonSerializer.Deserialize<List<FileChange>>(reader.GetString(reader.GetOrdinal("changes"))) ?? [],
            TokensUsed = reader.GetInt32(reader.GetOrdinal("tokens_used")),
            EstimatedCost = reader.GetDouble(reader.GetOrdinal("estimated_cost")),
            LatencyMs = reader.GetInt64(reader.GetOrdinal("latency_ms")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message"))
        };
    }

    public async Task<IReadOnlyList<AgentTask>> GetTaskHistoryAsync(int limit = 100, int offset = 0)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTaskHistoryPaged;
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tasks.Add(ReadTask(reader));
        }
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> GetTasksByStatusAsync(AgentTaskStatus status)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTasksByStatus;
        cmd.Parameters.AddWithValue("@status", status.ToString());

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tasks.Add(ReadTask(reader));
        }
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> GetTasksBySourceTagAsync(string sourceTag, int limit = 100, int offset = 0)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTasksBySourceTag;
        cmd.Parameters.AddWithValue("@sourceTag", sourceTag);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tasks.Add(ReadTask(reader));
        }
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> LoadPendingTasksAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Reload tasks that were Queued or Running (Running = crashed mid-execution, reset to Queued)
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectPendingTasks;

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var t = ReadTask(reader);
            // Tasks that were Running when the service died are reset to Queued in memory only
            // (we do NOT write back to DB here — ExecuteTaskAsync will update it correctly on re-run)
            if (t.Status == AgentTaskStatus.Running)
            {
                t.Status = AgentTaskStatus.Queued;
                t.StartedAt = null;
            }
            tasks.Add(t);
        }
        return tasks;
    }

    public async Task SaveDlqEntryAsync(DeadLetterEntry entry)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertDlqEntry;

        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@originalTaskId", entry.OriginalTaskId);
        cmd.Parameters.AddWithValue("@agentType", entry.AgentType.ToString());
        cmd.Parameters.AddWithValue("@modelProvider", entry.ModelProvider.ToString());
        cmd.Parameters.AddWithValue("@modelId", entry.ModelId);
        cmd.Parameters.AddWithValue("@description", entry.Description);
        cmd.Parameters.AddWithValue("@filePaths", JsonSerializer.Serialize(entry.FilePaths));
        cmd.Parameters.AddWithValue("@errorMessage", entry.ErrorMessage);
        cmd.Parameters.AddWithValue("@errorCode", (object?)entry.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retryCount", entry.RetryCount);
        cmd.Parameters.AddWithValue("@failedAt", entry.FailedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@originalCreatedAt", entry.OriginalCreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entry.Metadata));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> GetDlqEntriesAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectAllDlq;

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(ReadDlqEntry(reader));
        }
        return entries;
    }

    public async Task RemoveDlqEntryAsync(string dlqId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.DeleteDlqById;
        cmd.Parameters.AddWithValue("@id", dlqId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PurgeDlqOlderThanAsync(DateTime cutoff)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.PurgeDlqOlderThan;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        var deleted = await cmd.ExecuteNonQueryAsync();

        if (deleted > 0)
            _logger.LogInformation("Purged {Count} expired DLQ entries from database", deleted);
    }

    private static AgentTask ReadTask(SqliteDataReader reader)
    {
        return new AgentTask
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            AgentType = Enum.Parse<AgentType>(reader.GetString(reader.GetOrdinal("agent_type"))),
            ModelProvider = Enum.Parse<ModelProvider>(reader.GetString(reader.GetOrdinal("model_provider"))),
            ModelId = reader.GetString(reader.GetOrdinal("model_id")),
            Description = reader.GetString(reader.GetOrdinal("description")),
            FilePaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
            Status = Enum.Parse<AgentTaskStatus>(reader.GetString(reader.GetOrdinal("status"))),
            Progress = reader.GetInt32(reader.GetOrdinal("progress")),
            StatusMessage = reader.IsDBNull(reader.GetOrdinal("status_message")) ? null : reader.GetString(reader.GetOrdinal("status_message")),
            Priority = reader.GetInt32(reader.GetOrdinal("priority")),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? [],
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            StartedAt = reader.IsDBNull(reader.GetOrdinal("started_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
            CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at"))),
            ScheduledFor = reader.IsDBNull(reader.GetOrdinal("scheduled_for")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("scheduled_for"))),
            ComparisonGroupId = reader.IsDBNull(reader.GetOrdinal("comparison_group_id")) ? null : reader.GetString(reader.GetOrdinal("comparison_group_id")),
            SourceTag = reader.IsDBNull(reader.GetOrdinal("source_tag")) ? null : reader.GetString(reader.GetOrdinal("source_tag"))
        };
    }

    private static DeadLetterEntry ReadDlqEntry(SqliteDataReader reader)
    {
        return new DeadLetterEntry
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            OriginalTaskId = reader.GetString(reader.GetOrdinal("original_task_id")),
            AgentType = Enum.Parse<AgentType>(reader.GetString(reader.GetOrdinal("agent_type"))),
            ModelProvider = Enum.Parse<ModelProvider>(reader.GetString(reader.GetOrdinal("model_provider"))),
            ModelId = reader.GetString(reader.GetOrdinal("model_id")),
            Description = reader.GetString(reader.GetOrdinal("description")) ?? "",
            FilePaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
            ErrorMessage = reader.GetString(reader.GetOrdinal("error_message")),
            ErrorCode = reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")),
            RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
            FailedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("failed_at"))),
            OriginalCreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("original_created_at"))),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? []
        };
    }

    // IActivityRepository implementation

    public async Task SaveActivityAsync(ActivityEntry entry)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertActivity;

        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@workspacePath", entry.WorkspacePath);
        cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@hourBucket", entry.HourBucket);
        cmd.Parameters.AddWithValue("@activityType", entry.ActivityType.ToString());
        cmd.Parameters.AddWithValue("@actor", entry.Actor);
        cmd.Parameters.AddWithValue("@summary", entry.Summary);
        cmd.Parameters.AddWithValue("@details", (object?)entry.Details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@taskId", (object?)entry.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filePaths", JsonSerializer.Serialize(entry.FilePaths));
        cmd.Parameters.AddWithValue("@gitCommitHash", (object?)entry.GitCommitHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(entry.Metadata));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetActivitiesByHourAsync(string workspacePath, string hourBucket)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivitiesByHour;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@hourBucket", hourBucket);

        var activities = new List<ActivityEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            activities.Add(ReadActivityEntry(reader));
        }
        return activities;
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetActivitiesByTimeRangeAsync(string workspacePath, DateTime start, DateTime end)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivitiesByTimeRange;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@start", start.ToString("O"));
        cmd.Parameters.AddWithValue("@end", end.ToString("O"));

        var activities = new List<ActivityEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            activities.Add(ReadActivityEntry(reader));
        }
        return activities;
    }

    public async Task<IReadOnlyList<string>> GetHourBucketsAsync(string workspacePath, int limit = 100)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectHourBuckets;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);
        cmd.Parameters.AddWithValue("@limit", limit);

        var buckets = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            buckets.Add(reader.GetString(0));
        }
        return buckets;
    }

    public async Task<ActivityLogConfig?> GetConfigAsync(string workspacePath)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectActivityConfig;
        cmd.Parameters.AddWithValue("@workspacePath", workspacePath);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new ActivityLogConfig
        {
            WorkspacePath = reader.GetString(reader.GetOrdinal("workspace_path")),
            Enabled = reader.GetInt32(reader.GetOrdinal("enabled")) == 1,
            GitIntegrationMode = Enum.Parse<GitIntegrationMode>(reader.GetString(reader.GetOrdinal("git_integration_mode"))),
            MarkdownEnabled = reader.GetInt32(reader.GetOrdinal("markdown_enabled")) == 1,
            CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };
    }

    public async Task SaveConfigAsync(ActivityLogConfig config)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertActivityConfig;

        cmd.Parameters.AddWithValue("@workspacePath", config.WorkspacePath);
        cmd.Parameters.AddWithValue("@enabled", config.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@gitIntegrationMode", config.GitIntegrationMode.ToString());
        cmd.Parameters.AddWithValue("@markdownEnabled", config.MarkdownEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", config.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt", config.UpdatedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    // ── IWorkflowRepository implementation ────────────────────────────────────

    private static readonly JsonSerializerOptions _wfJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertWorkflowInstance;

        cmd.Parameters.AddWithValue("@id",           instance.InstanceId);
        cmd.Parameters.AddWithValue("@definitionId", instance.DefinitionId);
        cmd.Parameters.AddWithValue("@status",       instance.Status.ToString());
        cmd.Parameters.AddWithValue("@json",         JsonSerializer.Serialize(instance, _wfJsonOptions));
        cmd.Parameters.AddWithValue("@workspacePath",(object?)instance.WorkspacePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt",    instance.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@completedAt",  instance.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt",    DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<WorkflowInstance>> LoadRunningInstancesAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Recover both Running and Paused instances — they may have in-flight steps
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectRunningWorkflowInstances;

        var results = new List<WorkflowInstance>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var inst = JsonSerializer.Deserialize<WorkflowInstance>(json, _wfJsonOptions);
            if (inst is not null)
                results.Add(inst);
        }
        return results;
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.DeleteWorkflowInstance;
        cmd.Parameters.AddWithValue("@id", instanceId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Determinism — output cache ──────────────────────────────────────

    public async Task<string?> GetCachedOutputAsync(string cacheKey)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.GetCachedOutput;
        cmd.Parameters.AddWithValue("@cacheKey", cacheKey);

        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? s : null;
    }

    public async Task StoreCachedOutputAsync(string cacheKey, string output, string modelId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertCachedOutput;
        cmd.Parameters.AddWithValue("@cacheKey",  cacheKey);
        cmd.Parameters.AddWithValue("@output",    output);
        cmd.Parameters.AddWithValue("@modelId",   modelId);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    private static ActivityEntry ReadActivityEntry(SqliteDataReader reader)
    {
        return new ActivityEntry
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            WorkspacePath = reader.GetString(reader.GetOrdinal("workspace_path")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            HourBucket = reader.GetString(reader.GetOrdinal("hour_bucket")),
            ActivityType = Enum.Parse<ActivityType>(reader.GetString(reader.GetOrdinal("activity_type"))),
            Actor = reader.GetString(reader.GetOrdinal("actor")),
            Summary = reader.GetString(reader.GetOrdinal("summary")),
            Details = reader.IsDBNull(reader.GetOrdinal("details")) ? null : reader.GetString(reader.GetOrdinal("details")),
            TaskId = reader.IsDBNull(reader.GetOrdinal("task_id")) ? null : reader.GetString(reader.GetOrdinal("task_id")),
            FilePaths = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
            GitCommitHash = reader.IsDBNull(reader.GetOrdinal("git_commit_hash")) ? null : reader.GetString(reader.GetOrdinal("git_commit_hash")),
            Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? []
        };
    }

    // ── ISchedulerRepository ──────────────────────────────────────────────────

    public async Task<DateTimeOffset?> GetLastFiredAtAsync(string promptKey)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectLastFiredAt;
        cmd.Parameters.AddWithValue("@promptKey", promptKey);

        var raw = await cmd.ExecuteScalarAsync();
        return raw is string s ? DateTimeOffset.Parse(s) : null;
    }

    public async Task SetLastFiredAtAsync(string promptKey, DateTimeOffset firedAt)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertSchedulerState;
        cmd.Parameters.AddWithValue("@promptKey", promptKey);
        cmd.Parameters.AddWithValue("@lastFiredAt", firedAt.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task LoadAllLastFiredAsync(IDictionary<string, DateTimeOffset> target)
    {
        await using var conn = new SqliteConnection(_connectionString);
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
