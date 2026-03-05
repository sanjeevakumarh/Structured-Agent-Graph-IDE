using System.Text.Json;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Persistence;

/// <summary>
/// Persists and recovers workflow instances (IWorkflowRepository).
/// Shares the same SQLite file as <see cref="SqliteTaskRepository"/>.
/// </summary>
public sealed class SqliteWorkflowRepository : SqliteRepositoryBase, IWorkflowRepository
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = false,
    };

    public SqliteWorkflowRepository(string dbPath) : base(dbPath) { }

    public async Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.UpsertWorkflowInstance;

        cmd.Parameters.AddWithValue("@id",           instance.InstanceId);
        cmd.Parameters.AddWithValue("@definitionId", instance.DefinitionId);
        cmd.Parameters.AddWithValue("@status",       instance.Status.ToString());
        cmd.Parameters.AddWithValue("@json",         JsonSerializer.Serialize(instance, _jsonOptions));
        cmd.Parameters.AddWithValue("@workspacePath",(object?)instance.WorkspacePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt",    instance.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@completedAt",  instance.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt",    DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<WorkflowInstance>> LoadRunningInstancesAsync()
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        // Recover both Running and Paused instances — they may have in-flight steps
        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.SelectRunningWorkflowInstances;

        var results = new List<WorkflowInstance>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var inst = JsonSerializer.Deserialize<WorkflowInstance>(json, _jsonOptions);
            if (inst is not null)
                results.Add(inst);
        }
        return results;
    }

    public async Task DeleteWorkflowInstanceAsync(string instanceId)
    {
        await using var conn = OpenConnection();
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = SqlQueries.DeleteWorkflowInstance;
        cmd.Parameters.AddWithValue("@id", instanceId);
        await cmd.ExecuteNonQueryAsync();
    }
}
