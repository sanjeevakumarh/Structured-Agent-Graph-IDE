using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IProjectMemory"/>.
///
/// Stores key-value facts per workspace in the <c>project_memory</c> table.
/// The table is created on first use (idempotent). All write operations are
/// async and safe to call fire-and-forget from agent code.
///
/// Schema:
/// <code>
///   project_memory (workspace_path, key, value, updated_at)
///   PK: (workspace_path, key)
/// </code>
/// </summary>
public sealed class SqliteProjectMemory : SqliteRepositoryBase, IProjectMemory
{
    private readonly ILogger<SqliteProjectMemory> _logger;

    private const string CreateTable = """
        CREATE TABLE IF NOT EXISTS project_memory (
            workspace_path TEXT NOT NULL,
            key            TEXT NOT NULL,
            value          TEXT NOT NULL DEFAULT '',
            updated_at     TEXT NOT NULL,
            PRIMARY KEY (workspace_path, key)
        );
        CREATE INDEX IF NOT EXISTS idx_project_memory_workspace ON project_memory(workspace_path);
        """;

    public SqliteProjectMemory(string dbPath, ILogger<SqliteProjectMemory> logger)
        : base(dbPath)
    {
        _logger = logger;
        _ = Task.Run(InitializeAsync);
    }

    // ── IProjectMemory ────────────────────────────────────────────────────────

    public async Task SetAsync(
        string workspacePath, string key, string value,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO project_memory (workspace_path, key, value, updated_at)
                VALUES (@wp, @key, @value, @now)
                ON CONFLICT(workspace_path, key) DO UPDATE
                    SET value = @value, updated_at = @now
                """;
            cmd.Parameters.AddWithValue("@wp",    workspacePath);
            cmd.Parameters.AddWithValue("@key",   key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@now",   DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectMemory.SetAsync failed for workspace '{Ws}', key '{Key}'",
                workspacePath, key);
        }
    }

    public async Task<string?> GetAsync(
        string workspacePath, string key,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM project_memory WHERE workspace_path = @wp AND key = @key";
            cmd.Parameters.AddWithValue("@wp",  workspacePath);
            cmd.Parameters.AddWithValue("@key", key);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result as string;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectMemory.GetAsync failed for workspace '{Ws}', key '{Key}'",
                workspacePath, key);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        string workspacePath,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM project_memory WHERE workspace_path = @wp ORDER BY key";
            cmd.Parameters.AddWithValue("@wp", workspacePath);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetString(0)] = reader.GetString(1);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectMemory.GetAllAsync failed for workspace '{Ws}'", workspacePath);
            return new Dictionary<string, string>();
        }
    }

    public async Task DeleteAsync(
        string workspacePath, string key,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM project_memory WHERE workspace_path = @wp AND key = @key";
            cmd.Parameters.AddWithValue("@wp",  workspacePath);
            cmd.Parameters.AddWithValue("@key", key);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectMemory.DeleteAsync failed for workspace '{Ws}', key '{Key}'",
                workspacePath, key);
        }
    }

    // ── Schema init ───────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        try
        {
            await using var conn = OpenConnection();
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = CreateTable;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialise project_memory table");
        }
    }
}
