using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists agent tasks, results, dead-letter queue entries, and the deterministic
/// output cache (ITaskRepository).
///
/// <see cref="SqliteActivityRepository"/>, <see cref="SqliteWorkflowRepository"/>, and
/// <see cref="SqliteSchedulerRepository"/> handle the other three persistence concerns
/// in separate, independently-testable files that share the same SQLite database file.
///
/// This class also owns <see cref="InitializeAsync"/> — the one-time schema bootstrap
/// that creates all tables (including those owned by the sibling repositories).
/// </summary>
public class SqliteTaskRepository : SqliteRepositoryBase, ITaskRepository
{
    private readonly ILogger<SqliteTaskRepository> _logger;

    public SqliteTaskRepository(string dbPath, ILogger<SqliteTaskRepository> logger)
        : base(dbPath)
    {
        _logger = logger;
    }

    // ── Schema bootstrap ──────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await using var conn = OpenConnection();
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

        var perfTableCmd = conn.CreateCommand();
        perfTableCmd.CommandText = SqlQueries.CreateModelPerfTable;
        await perfTableCmd.ExecuteNonQueryAsync();

        var qualityTableCmd = conn.CreateCommand();
        qualityTableCmd.CommandText = SqlQueries.CreateModelQualityTable;
        await qualityTableCmd.ExecuteNonQueryAsync();

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

        // Purge stale task history and DLQ on startup for a clean slate.
        // Order matters: child tables first (FK constraints).
        var purgeResultsCmd = conn.CreateCommand();
        purgeResultsCmd.CommandText = "DELETE FROM task_results";
        await purgeResultsCmd.ExecuteNonQueryAsync();

        var purgeDlqCmd = conn.CreateCommand();
        purgeDlqCmd.CommandText = "DELETE FROM dead_letter_tasks";
        var deletedDlq = await purgeDlqCmd.ExecuteNonQueryAsync();

        var purgeTasksCmd = conn.CreateCommand();
        purgeTasksCmd.CommandText = "DELETE FROM task_history";
        var deletedTasks = await purgeTasksCmd.ExecuteNonQueryAsync();

        var purgeWorkflowsCmd = conn.CreateCommand();
        purgeWorkflowsCmd.CommandText = "DELETE FROM workflow_instances";
        await purgeWorkflowsCmd.ExecuteNonQueryAsync();

        if (deletedTasks > 0 || deletedDlq > 0)
            _logger.LogInformation("Startup purge: cleared {Tasks} tasks, {Dlq} DLQ entries", deletedTasks, deletedDlq);

        _logger.LogInformation("SQLite database initialized at {DbPath}", _connectionString);
    }

    // ── Task persistence ──────────────────────────────────────────────────────

    public async Task SaveTaskAsync(AgentTask task)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertTask;
        BindTaskParams(cmd, task);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveResultAsync(AgentResult result)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertResult;
        BindResultParams(cmd, result);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveTaskCompletedWithResultAsync(AgentTask task, AgentResult result)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();
        await using var txn = await conn.BeginTransactionAsync();
        try
        {
            var taskCmd = conn.CreateCommand();
            taskCmd.Transaction = (SqliteTransaction)txn;
            taskCmd.CommandText = SqlQueries.UpsertTask;
            BindTaskParams(taskCmd, task);
            await taskCmd.ExecuteNonQueryAsync();

            var resultCmd = conn.CreateCommand();
            resultCmd.Transaction = (SqliteTransaction)txn;
            resultCmd.CommandText = SqlQueries.UpsertResult;
            BindResultParams(resultCmd, result);
            await resultCmd.ExecuteNonQueryAsync();

            await txn.CommitAsync();
        }
        catch
        {
            await txn.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database health check failed");
            return false;
        }
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTaskById;
        cmd.Parameters.AddWithValue("@id", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadTask(reader) : null;
    }

    public async Task<AgentResult?> GetResultAsync(string taskId)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectResultByTaskId;
        cmd.Parameters.AddWithValue("@taskId", taskId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AgentResult
        {
            TaskId        = reader.GetString(reader.GetOrdinal("task_id")),
            Success       = reader.GetInt32(reader.GetOrdinal("success")) == 1,
            Output        = reader.GetString(reader.GetOrdinal("output")),
            Issues        = JsonSerializer.Deserialize<List<Issue>>(reader.GetString(reader.GetOrdinal("issues"))) ?? [],
            Changes       = JsonSerializer.Deserialize<List<FileChange>>(reader.GetString(reader.GetOrdinal("changes"))) ?? [],
            TokensUsed    = reader.GetInt32(reader.GetOrdinal("tokens_used")),
            EstimatedCost = reader.GetDouble(reader.GetOrdinal("estimated_cost")),
            LatencyMs     = reader.GetInt64(reader.GetOrdinal("latency_ms")),
            ErrorMessage  = reader.IsDBNull(reader.GetOrdinal("error_message")) ? null : reader.GetString(reader.GetOrdinal("error_message"))
        };
    }

    public async Task<IReadOnlyList<AgentTask>> GetTaskHistoryAsync(int limit = 100, int offset = 0)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTaskHistoryPaged;
        cmd.Parameters.AddWithValue("@limit",  limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tasks.Add(ReadTask(reader));
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> GetTasksByStatusAsync(AgentTaskStatus status)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTasksByStatus;
        cmd.Parameters.AddWithValue("@status", status.ToString());

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tasks.Add(ReadTask(reader));
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> GetTasksBySourceTagAsync(
        string sourceTag, int limit = 100, int offset = 0)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectTasksBySourceTag;
        cmd.Parameters.AddWithValue("@sourceTag", sourceTag);
        cmd.Parameters.AddWithValue("@limit",     limit);
        cmd.Parameters.AddWithValue("@offset",    offset);

        var tasks = new List<AgentTask>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tasks.Add(ReadTask(reader));
        return tasks;
    }

    public async Task<IReadOnlyList<AgentTask>> LoadPendingTasksAsync()
    {
        await using var conn = OpenConnection();
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
                t.Status    = AgentTaskStatus.Queued;
                t.StartedAt = null;
            }
            tasks.Add(t);
        }
        return tasks;
    }

    // ── Dead-letter queue ─────────────────────────────────────────────────────

    public async Task SaveDlqEntryAsync(DeadLetterEntry entry)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.InsertDlqEntry;

        cmd.Parameters.AddWithValue("@id",                entry.Id);
        cmd.Parameters.AddWithValue("@originalTaskId",    entry.OriginalTaskId);
        cmd.Parameters.AddWithValue("@agentType",         entry.AgentType.ToString());
        cmd.Parameters.AddWithValue("@modelProvider",     entry.ModelProvider.ToString());
        cmd.Parameters.AddWithValue("@modelId",           entry.ModelId);
        cmd.Parameters.AddWithValue("@description",       entry.Description);
        cmd.Parameters.AddWithValue("@filePaths",         JsonSerializer.Serialize(entry.FilePaths));
        cmd.Parameters.AddWithValue("@errorMessage",      entry.ErrorMessage);
        cmd.Parameters.AddWithValue("@errorCode",         (object?)entry.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@retryCount",        entry.RetryCount);
        cmd.Parameters.AddWithValue("@failedAt",          entry.FailedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@originalCreatedAt", entry.OriginalCreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@metadata",          JsonSerializer.Serialize(entry.Metadata));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> GetDlqEntriesAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectAllDlq;

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(ReadDlqEntry(reader));
        return entries;
    }

    public async Task RemoveDlqEntryAsync(string dlqId)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.DeleteDlqById;
        cmd.Parameters.AddWithValue("@id", dlqId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task PurgeDlqOlderThanAsync(DateTime cutoff)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.PurgeDlqOlderThan;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        var deleted = await cmd.ExecuteNonQueryAsync();

        if (deleted > 0)
            _logger.LogInformation("Purged {Count} expired DLQ entries from database", deleted);
    }

    // ── Deterministic output cache ────────────────────────────────────────────

    public async Task<string?> GetCachedOutputAsync(string cacheKey)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.GetCachedOutput;
        cmd.Parameters.AddWithValue("@cacheKey", cacheKey);

        var result = await cmd.ExecuteScalarAsync();
        return result is string s ? s : null;
    }

    public async Task StoreCachedOutputAsync(string cacheKey, string output, string modelId)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertCachedOutput;
        cmd.Parameters.AddWithValue("@cacheKey",  cacheKey);
        cmd.Parameters.AddWithValue("@output",    output);
        cmd.Parameters.AddWithValue("@modelId",   modelId);
        cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void BindTaskParams(SqliteCommand cmd, AgentTask task)
    {
        cmd.Parameters.AddWithValue("@id",                task.Id);
        cmd.Parameters.AddWithValue("@agentType",         task.AgentType.ToString());
        cmd.Parameters.AddWithValue("@modelProvider",     task.ModelProvider.ToString());
        cmd.Parameters.AddWithValue("@modelId",           task.ModelId);
        cmd.Parameters.AddWithValue("@description",       task.Description);
        cmd.Parameters.AddWithValue("@filePaths",         JsonSerializer.Serialize(task.FilePaths));
        cmd.Parameters.AddWithValue("@status",            task.Status.ToString());
        cmd.Parameters.AddWithValue("@progress",          task.Progress);
        cmd.Parameters.AddWithValue("@statusMessage",     (object?)task.StatusMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@priority",          task.Priority);
        cmd.Parameters.AddWithValue("@metadata",          JsonSerializer.Serialize(task.Metadata));
        cmd.Parameters.AddWithValue("@createdAt",         task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@startedAt",         task.StartedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@completedAt",       task.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@scheduledFor",      task.ScheduledFor?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@comparisonGroupId", (object?)task.ComparisonGroupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceTag",         (object?)task.SourceTag ?? DBNull.Value);
    }

    private static void BindResultParams(SqliteCommand cmd, AgentResult result)
    {
        cmd.Parameters.AddWithValue("@taskId",        result.TaskId);
        cmd.Parameters.AddWithValue("@success",       result.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("@output",        result.Output);
        cmd.Parameters.AddWithValue("@issues",        JsonSerializer.Serialize(result.Issues));
        cmd.Parameters.AddWithValue("@changes",       JsonSerializer.Serialize(result.Changes));
        cmd.Parameters.AddWithValue("@tokensUsed",    result.TokensUsed);
        cmd.Parameters.AddWithValue("@estimatedCost", result.EstimatedCost);
        cmd.Parameters.AddWithValue("@latencyMs",     result.LatencyMs);
        cmd.Parameters.AddWithValue("@errorMessage",  (object?)result.ErrorMessage ?? DBNull.Value);
    }

    private static AgentTask ReadTask(SqliteDataReader reader) => new()
    {
        Id                = reader.GetString(reader.GetOrdinal("id")),
        AgentType         = Enum.Parse<AgentType>(reader.GetString(reader.GetOrdinal("agent_type"))),
        ModelProvider     = Enum.Parse<ModelProvider>(reader.GetString(reader.GetOrdinal("model_provider"))),
        ModelId           = reader.GetString(reader.GetOrdinal("model_id")),
        Description       = reader.GetString(reader.GetOrdinal("description")),
        FilePaths         = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
        Status            = Enum.Parse<AgentTaskStatus>(reader.GetString(reader.GetOrdinal("status"))),
        Progress          = reader.GetInt32(reader.GetOrdinal("progress")),
        StatusMessage     = reader.IsDBNull(reader.GetOrdinal("status_message"))     ? null : reader.GetString(reader.GetOrdinal("status_message")),
        Priority          = reader.GetInt32(reader.GetOrdinal("priority")),
        Metadata          = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? [],
        CreatedAt         = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        StartedAt         = reader.IsDBNull(reader.GetOrdinal("started_at"))         ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
        CompletedAt       = reader.IsDBNull(reader.GetOrdinal("completed_at"))       ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("completed_at"))),
        ScheduledFor      = reader.IsDBNull(reader.GetOrdinal("scheduled_for"))      ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("scheduled_for"))),
        ComparisonGroupId = reader.IsDBNull(reader.GetOrdinal("comparison_group_id")) ? null : reader.GetString(reader.GetOrdinal("comparison_group_id")),
        SourceTag         = reader.IsDBNull(reader.GetOrdinal("source_tag"))         ? null : reader.GetString(reader.GetOrdinal("source_tag"))
    };

    private static DeadLetterEntry ReadDlqEntry(SqliteDataReader reader) => new()
    {
        Id                = reader.GetString(reader.GetOrdinal("id")),
        OriginalTaskId    = reader.GetString(reader.GetOrdinal("original_task_id")),
        AgentType         = Enum.Parse<AgentType>(reader.GetString(reader.GetOrdinal("agent_type"))),
        ModelProvider     = Enum.Parse<ModelProvider>(reader.GetString(reader.GetOrdinal("model_provider"))),
        ModelId           = reader.GetString(reader.GetOrdinal("model_id")),
        Description       = reader.GetString(reader.GetOrdinal("description")) ?? "",
        FilePaths         = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("file_paths"))) ?? [],
        ErrorMessage      = reader.GetString(reader.GetOrdinal("error_message")),
        ErrorCode         = reader.IsDBNull(reader.GetOrdinal("error_code")) ? null : reader.GetString(reader.GetOrdinal("error_code")),
        RetryCount        = reader.GetInt32(reader.GetOrdinal("retry_count")),
        FailedAt          = DateTime.Parse(reader.GetString(reader.GetOrdinal("failed_at"))),
        OriginalCreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("original_created_at"))),
        Metadata          = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata"))) ?? []
    };
}
