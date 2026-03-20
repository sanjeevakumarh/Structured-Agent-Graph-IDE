using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Observability;

namespace SAGIDE.Security;

/// <summary>
/// SQLite-backed audit log. All writes are fire-and-forget background tasks so
/// they never block the hot path. Reads are synchronous (small result sets only).
///
/// Schema: <c>security_audit</c> table — created on first use, idempotent.
/// </summary>
public sealed class SqliteAuditLog : IAuditLog
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteAuditLog> _logger;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
    };

    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS security_audit (
            id          TEXT PRIMARY KEY,
            event_type  TEXT NOT NULL,
            subject     TEXT NOT NULL,
            actor       TEXT NOT NULL,
            detail      TEXT NOT NULL DEFAULT '{}',
            occurred_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_audit_occurred_at ON security_audit(occurred_at);
        CREATE INDEX IF NOT EXISTS idx_audit_event_type  ON security_audit(event_type);
        """;

    public SqliteAuditLog(string dbPath, ILogger<SqliteAuditLog> logger)
    {
        _connectionString = $"Data Source={dbPath};Pooling=True;Foreign Keys=False";
        _logger           = logger;
        _ = Task.Run(InitializeAsync);
    }

    // ── IAuditLog ─────────────────────────────────────────────────────────────

    public Task RecordTaskSubmittedAsync(
        string taskId, string agentType, string modelProvider,
        string modelId, string sourceTag, CancellationToken ct = default)
    {
        var detail = JsonSerializer.Serialize(new
        {
            agentType,
            modelProvider,
            modelId,
            sourceTag,
        }, _jsonOpts);

        // Tag the current trace activity so the audit record correlates with the span
        using var activity = SagideActivitySource.Start(
            SagideActivitySource.Api, "audit.task_submitted");
        activity?.SetTag("task.id",      taskId);
        activity?.SetTag("sagide.source_tag", sourceTag);

        _ = WriteAsync("task_submitted", taskId, sourceTag, detail);
        return Task.CompletedTask;
    }

    public Task RecordToolCallAsync(
        string toolName, IReadOnlyDictionary<string, string> parameters,
        string callerTag, CancellationToken ct = default)
    {
        var detail = JsonSerializer.Serialize(new { parameters }, _jsonOpts);

        using var activity = SagideActivitySource.Start(
            SagideActivitySource.Tools, "audit.tool_call");
        activity?.SetTag("tool.name",    toolName);
        activity?.SetTag("sagide.source_tag", callerTag);

        _ = WriteAsync("tool_call", toolName, callerTag, detail);
        return Task.CompletedTask;
    }

    public Task RecordAuthFailureAsync(
        string path, string? remoteIp, CancellationToken ct = default)
    {
        var detail = JsonSerializer.Serialize(new { path, remoteIp }, _jsonOpts);

        using var activity = SagideActivitySource.Start(
            SagideActivitySource.Api, "audit.auth_failure");
        activity?.SetTag("http.path", path);

        _ = WriteAsync("auth_failure", path, remoteIp ?? "unknown", detail);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAsync(
        int limit = 100, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, event_type, subject, actor, detail, occurred_at
                FROM security_audit
                ORDER BY occurred_at DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<AuditEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new AuditEntry(
                    Id:         reader.GetString(0),
                    EventType:  reader.GetString(1),
                    Subject:    reader.GetString(2),
                    Actor:      reader.GetString(3),
                    Detail:     reader.GetString(4),
                    OccurredAt: DateTime.Parse(reader.GetString(5))));
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read audit log");
            return [];
        }
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task WriteAsync(
        string eventType, string subject, string actor, string detail)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO security_audit (id, event_type, subject, actor, detail, occurred_at)
                VALUES (@id, @eventType, @subject, @actor, @detail, @occurredAt)
                """;
            cmd.Parameters.AddWithValue("@id",          Guid.NewGuid().ToString("N")[..16]);
            cmd.Parameters.AddWithValue("@eventType",   eventType);
            cmd.Parameters.AddWithValue("@subject",     subject);
            cmd.Parameters.AddWithValue("@actor",       actor);
            cmd.Parameters.AddWithValue("@detail",      detail);
            cmd.Parameters.AddWithValue("@occurredAt",  DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write audit entry [{EventType}] {Subject}", eventType, subject);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = CreateTable;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialise security_audit table");
        }
    }
}
