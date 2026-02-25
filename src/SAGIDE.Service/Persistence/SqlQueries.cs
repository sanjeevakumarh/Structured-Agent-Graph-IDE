namespace SAGIDE.Service.Persistence;

/// <summary>
/// All SQL statements used by SqliteTaskRepository, centralised so they are easy to find and edit.
/// </summary>
internal static class SqlQueries
{
    // ── Pragmas ───────────────────────────────────────────────────────────────

    public const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";

    // ── DDL — table creation ──────────────────────────────────────────────────

    public const string CreateCoreTables = """
        CREATE TABLE IF NOT EXISTS task_history (
            id TEXT PRIMARY KEY,
            agent_type TEXT NOT NULL,
            model_provider TEXT NOT NULL,
            model_id TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            file_paths TEXT NOT NULL DEFAULT '[]',
            status TEXT NOT NULL,
            progress INTEGER NOT NULL DEFAULT 0,
            status_message TEXT,
            priority INTEGER NOT NULL DEFAULT 0,
            metadata TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            started_at TEXT,
            completed_at TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_task_created_at ON task_history(created_at);
        CREATE INDEX IF NOT EXISTS idx_task_status ON task_history(status);

        CREATE TABLE IF NOT EXISTS task_results (
            task_id TEXT PRIMARY KEY,
            success INTEGER NOT NULL DEFAULT 0,
            output TEXT NOT NULL DEFAULT '',
            issues TEXT NOT NULL DEFAULT '[]',
            changes TEXT NOT NULL DEFAULT '[]',
            tokens_used INTEGER NOT NULL DEFAULT 0,
            estimated_cost REAL NOT NULL DEFAULT 0,
            latency_ms INTEGER NOT NULL DEFAULT 0,
            error_message TEXT,
            FOREIGN KEY (task_id) REFERENCES task_history(id)
        );

        CREATE TABLE IF NOT EXISTS dead_letter_tasks (
            id TEXT PRIMARY KEY,
            original_task_id TEXT NOT NULL,
            agent_type TEXT NOT NULL,
            model_provider TEXT NOT NULL,
            model_id TEXT NOT NULL,
            description TEXT,
            file_paths TEXT NOT NULL DEFAULT '[]',
            error_message TEXT NOT NULL,
            error_code TEXT,
            retry_count INTEGER NOT NULL DEFAULT 0,
            failed_at TEXT NOT NULL,
            original_created_at TEXT NOT NULL,
            metadata TEXT NOT NULL DEFAULT '{}'
        );

        CREATE INDEX IF NOT EXISTS idx_dlq_failed_at ON dead_letter_tasks(failed_at);

        CREATE TABLE IF NOT EXISTS activity_log (
            id TEXT PRIMARY KEY,
            workspace_path TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            hour_bucket TEXT NOT NULL,
            activity_type TEXT NOT NULL,
            actor TEXT NOT NULL,
            summary TEXT NOT NULL,
            details TEXT,
            task_id TEXT,
            file_paths TEXT NOT NULL DEFAULT '[]',
            git_commit_hash TEXT,
            metadata TEXT NOT NULL DEFAULT '{}',
            FOREIGN KEY (task_id) REFERENCES task_history(id)
        );

        CREATE INDEX IF NOT EXISTS idx_activity_workspace ON activity_log(workspace_path);
        CREATE INDEX IF NOT EXISTS idx_activity_hour_bucket ON activity_log(hour_bucket);
        CREATE INDEX IF NOT EXISTS idx_activity_timestamp ON activity_log(timestamp);
        CREATE INDEX IF NOT EXISTS idx_activity_type ON activity_log(activity_type);
        CREATE INDEX IF NOT EXISTS idx_activity_task ON activity_log(task_id);

        CREATE TABLE IF NOT EXISTS activity_log_config (
            workspace_path TEXT PRIMARY KEY,
            enabled INTEGER NOT NULL DEFAULT 1,
            git_integration_mode TEXT NOT NULL DEFAULT 'log_commits',
            markdown_enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    // ── scheduler_state ───────────────────────────────────────────────────────

    public const string CreateSchedulerStateTable = """
        CREATE TABLE IF NOT EXISTS scheduler_state (
            prompt_key    TEXT PRIMARY KEY,
            last_fired_at TEXT NOT NULL
        );
        """;

    public const string SelectLastFiredAt =
        "SELECT last_fired_at FROM scheduler_state WHERE prompt_key = @promptKey";

    public const string SelectAllSchedulerState =
        "SELECT prompt_key, last_fired_at FROM scheduler_state";

    public const string UpsertSchedulerState = """
        INSERT INTO scheduler_state (prompt_key, last_fired_at)
        VALUES (@promptKey, @lastFiredAt)
        ON CONFLICT(prompt_key) DO UPDATE SET last_fired_at = @lastFiredAt
        """;

    // ── Determinism: output cache ───────────────────────────────────────

    public const string CreateOutputCacheTable = """
        CREATE TABLE IF NOT EXISTS node_output_cache (
            cache_key  TEXT PRIMARY KEY,
            output     TEXT NOT NULL,
            model_id   TEXT NOT NULL,
            created_at TEXT NOT NULL
        );
        """;

    public const string GetCachedOutput =
        "SELECT output FROM node_output_cache WHERE cache_key = @cacheKey";

    public const string UpsertCachedOutput = """
        INSERT INTO node_output_cache (cache_key, output, model_id, created_at)
        VALUES (@cacheKey, @output, @modelId, @createdAt)
        ON CONFLICT(cache_key) DO UPDATE SET output = @output, model_id = @modelId, created_at = @createdAt
        """;

    public const string CreateWorkflowTable = """
        CREATE TABLE IF NOT EXISTS workflow_instances (
            id           TEXT PRIMARY KEY,
            definition_id TEXT NOT NULL,
            status       TEXT NOT NULL,
            instance_json TEXT NOT NULL,
            workspace_path TEXT,
            created_at   TEXT NOT NULL,
            completed_at TEXT,
            updated_at   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_wf_status ON workflow_instances(status);
        """;

    // ── Schema migrations (idempotent ALTER TABLE) ────────────────────────────

    public static readonly string[] Migrations =
    [
        "ALTER TABLE task_history ADD COLUMN scheduled_for TEXT",
        "ALTER TABLE task_history ADD COLUMN comparison_group_id TEXT",
        "ALTER TABLE task_history ADD COLUMN source_tag TEXT",
        "CREATE INDEX IF NOT EXISTS idx_task_source_tag ON task_history(source_tag)",
    ];

    // ── task_history ──────────────────────────────────────────────────────────

    public const string UpsertTask = """
        INSERT INTO task_history (id, agent_type, model_provider, model_id, description, file_paths,
            status, progress, status_message, priority, metadata, created_at, started_at, completed_at,
            scheduled_for, comparison_group_id, source_tag)
        VALUES (@id, @agentType, @modelProvider, @modelId, @description, @filePaths,
            @status, @progress, @statusMessage, @priority, @metadata, @createdAt, @startedAt, @completedAt,
            @scheduledFor, @comparisonGroupId, @sourceTag)
        ON CONFLICT(id) DO UPDATE SET
            status = @status,
            progress = @progress,
            status_message = @statusMessage,
            started_at = @startedAt,
            completed_at = @completedAt,
            metadata = @metadata,
            scheduled_for = @scheduledFor,
            comparison_group_id = @comparisonGroupId,
            source_tag = @sourceTag
        """;

    public const string SelectTaskById = "SELECT * FROM task_history WHERE id = @id";

    public const string SelectTaskHistoryPaged =
        "SELECT * FROM task_history ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

    public const string SelectTasksByStatus =
        "SELECT * FROM task_history WHERE status = @status ORDER BY created_at DESC";

    public const string SelectPendingTasks = """
        SELECT * FROM task_history
        WHERE status IN ('Queued', 'Running')
        ORDER BY priority DESC, created_at ASC
        """;

    public const string SelectTasksBySourceTag =
        "SELECT * FROM task_history WHERE source_tag = @sourceTag ORDER BY created_at DESC LIMIT @limit OFFSET @offset";

    public const string SelectResultsBySourceTag = """
        SELECT r.*, h.source_tag FROM task_results r
        JOIN task_history h ON h.id = r.task_id
        WHERE h.source_tag = @sourceTag
        ORDER BY h.created_at DESC
        LIMIT @limit OFFSET @offset
        """;

    // ── task_results ──────────────────────────────────────────────────────────

    public const string UpsertResult = """
        INSERT INTO task_results (task_id, success, output, issues, changes, tokens_used,
            estimated_cost, latency_ms, error_message)
        VALUES (@taskId, @success, @output, @issues, @changes, @tokensUsed,
            @estimatedCost, @latencyMs, @errorMessage)
        ON CONFLICT(task_id) DO UPDATE SET
            success = @success, output = @output, issues = @issues, changes = @changes,
            tokens_used = @tokensUsed, estimated_cost = @estimatedCost, latency_ms = @latencyMs,
            error_message = @errorMessage
        """;

    public const string SelectResultByTaskId =
        "SELECT * FROM task_results WHERE task_id = @taskId";

    // ── dead_letter_tasks ─────────────────────────────────────────────────────

    public const string InsertDlqEntry = """
        INSERT INTO dead_letter_tasks (id, original_task_id, agent_type, model_provider, model_id,
            description, file_paths, error_message, error_code, retry_count, failed_at,
            original_created_at, metadata)
        VALUES (@id, @originalTaskId, @agentType, @modelProvider, @modelId,
            @description, @filePaths, @errorMessage, @errorCode, @retryCount, @failedAt,
            @originalCreatedAt, @metadata)
        ON CONFLICT(id) DO NOTHING
        """;

    public const string SelectAllDlq =
        "SELECT * FROM dead_letter_tasks ORDER BY failed_at DESC";

    public const string DeleteDlqById =
        "DELETE FROM dead_letter_tasks WHERE id = @id";

    public const string PurgeDlqOlderThan =
        "DELETE FROM dead_letter_tasks WHERE failed_at < @cutoff";

    // ── activity_log ──────────────────────────────────────────────────────────

    public const string InsertActivity = """
        INSERT INTO activity_log (id, workspace_path, timestamp, hour_bucket, activity_type,
            actor, summary, details, task_id, file_paths, git_commit_hash, metadata)
        VALUES (@id, @workspacePath, @timestamp, @hourBucket, @activityType,
            @actor, @summary, @details, @taskId, @filePaths, @gitCommitHash, @metadata)
        ON CONFLICT(id) DO NOTHING
        """;

    public const string SelectActivitiesByHour = """
        SELECT * FROM activity_log
        WHERE workspace_path = @workspacePath AND hour_bucket = @hourBucket
        ORDER BY timestamp ASC
        """;

    public const string SelectActivitiesByTimeRange = """
        SELECT * FROM activity_log
        WHERE workspace_path = @workspacePath
            AND timestamp >= @start AND timestamp <= @end
        ORDER BY timestamp ASC
        """;

    public const string SelectHourBuckets = """
        SELECT DISTINCT hour_bucket FROM activity_log
        WHERE workspace_path = @workspacePath
        ORDER BY hour_bucket DESC
        LIMIT @limit
        """;

    // ── activity_log_config ───────────────────────────────────────────────────

    public const string SelectActivityConfig =
        "SELECT * FROM activity_log_config WHERE workspace_path = @workspacePath";

    public const string UpsertActivityConfig = """
        INSERT INTO activity_log_config (workspace_path, enabled, git_integration_mode,
            markdown_enabled, created_at, updated_at)
        VALUES (@workspacePath, @enabled, @gitIntegrationMode, @markdownEnabled, @createdAt, @updatedAt)
        ON CONFLICT(workspace_path) DO UPDATE SET
            enabled = @enabled,
            git_integration_mode = @gitIntegrationMode,
            markdown_enabled = @markdownEnabled,
            updated_at = @updatedAt
        """;

    // ── workflow_instances ────────────────────────────────────────────────────

    public const string UpsertWorkflowInstance = """
        INSERT INTO workflow_instances (id, definition_id, status, instance_json,
            workspace_path, created_at, completed_at, updated_at)
        VALUES (@id, @definitionId, @status, @json, @workspacePath,
            @createdAt, @completedAt, @updatedAt)
        ON CONFLICT(id) DO UPDATE SET
            status       = @status,
            instance_json= @json,
            workspace_path = @workspacePath,
            completed_at = @completedAt,
            updated_at   = @updatedAt
        """;

    public const string SelectRunningWorkflowInstances = """
        SELECT instance_json FROM workflow_instances
        WHERE status IN ('Running', 'Paused')
        ORDER BY created_at ASC
        """;

    public const string DeleteWorkflowInstance =
        "DELETE FROM workflow_instances WHERE id = @id";
}
