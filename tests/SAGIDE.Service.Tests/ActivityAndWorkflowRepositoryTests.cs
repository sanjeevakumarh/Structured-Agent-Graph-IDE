using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Persistence;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integration tests for <see cref="SqliteActivityRepository"/> and
/// <see cref="SqliteWorkflowRepository"/> (the IActivityRepository and
/// IWorkflowRepository contracts).
/// Uses a fresh temp-file SQLite database per test class; <see cref="SqliteTaskRepository"/>
/// bootstraps the schema and handles the task-history FK needed by activity entries.
/// </summary>
public class ActivityAndWorkflowRepositoryTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteTaskRepository      _repo         = null!; // schema init + task FK
    private SqliteActivityRepository  _activityRepo = null!;
    private SqliteWorkflowRepository  _wfRepo       = null!;

    // ── Setup / Teardown ──────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _dbPath       = Path.Combine(Path.GetTempPath(), $"sagide-repo-test-{Guid.NewGuid():N}.db");
        _repo         = new SqliteTaskRepository(_dbPath, NullLogger<SqliteTaskRepository>.Instance);
        await _repo.InitializeAsync(); // creates all tables (including activity_log and workflow_instances)

        _activityRepo = new SqliteActivityRepository(_dbPath, NullLogger<SqliteActivityRepository>.Instance);
        _wfRepo       = new SqliteWorkflowRepository(_dbPath);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    // ── IActivityRepository — SaveActivityAsync / GetActivitiesByHourAsync ────

    [Fact]
    public async Task SaveActivity_ThenGetByHour_ReturnsEntry()
    {
        var entry = MakeActivity("2026-02-24-10", ActivityType.AgentTask, "Ran code review");

        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync(entry.WorkspacePath, "2026-02-24-10");

        Assert.Single(results);
        Assert.Equal(entry.Id,           results[0].Id);
        Assert.Equal(entry.Summary,      results[0].Summary);
        Assert.Equal(entry.ActivityType, results[0].ActivityType);
        Assert.Equal(entry.Actor,        results[0].Actor);
    }

    [Fact]
    public async Task GetActivitiesByHour_WrongHour_ReturnsEmpty()
    {
        var entry = MakeActivity("2026-02-24-10", ActivityType.HumanAction, "Edit file");
        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync(entry.WorkspacePath, "2026-02-24-11");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetActivitiesByHour_WrongWorkspace_ReturnsEmpty()
    {
        var entry = MakeActivity("2026-02-24-10", ActivityType.AgentTask, "Some action");
        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync("/other/workspace", "2026-02-24-10");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SaveActivity_PreservesFilePaths()
    {
        var entry = MakeActivity("2026-02-24-10", ActivityType.FileModified, "Edited files");
        entry.FilePaths = ["src/Foo.cs", "src/Bar.cs"];

        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync(entry.WorkspacePath, "2026-02-24-10");

        Assert.Equal(["src/Foo.cs", "src/Bar.cs"], results[0].FilePaths);
    }

    [Fact]
    public async Task SaveActivity_PreservesMetadata()
    {
        var ws    = $"/ws/{Guid.NewGuid():N}";
        var entry = MakeActivity("2026-02-24-10", ActivityType.SystemEvent, "Boot", ws);
        entry.Metadata = new Dictionary<string, string> { ["key"] = "value", ["count"] = "3" };

        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync(ws, "2026-02-24-10");

        Assert.Equal("value", results[0].Metadata["key"]);
        Assert.Equal("3",     results[0].Metadata["count"]);
    }

    [Fact]
    public async Task SaveActivity_PreservesOptionalFields()
    {
        var ws = $"/ws/{Guid.NewGuid():N}";

        // activity_log.task_id has a FK to task_history(id) — save the task first
        var task = new SAGIDE.Core.Models.AgentTask { Description = "parent task" };
        await _repo.SaveTaskAsync(task);

        var entry = MakeActivity("2026-02-24-10", ActivityType.GitCommit, "Commit", ws);
        entry.TaskId        = task.Id;
        entry.GitCommitHash = "abc1234567890";
        entry.Details       = """{"sha":"abc123"}""";

        await _activityRepo.SaveActivityAsync(entry);

        var results = await _activityRepo.GetActivitiesByHourAsync(ws, "2026-02-24-10");

        Assert.Equal(task.Id,                results[0].TaskId);
        Assert.Equal("abc1234567890",        results[0].GitCommitHash);
        Assert.Equal("""{"sha":"abc123"}""", results[0].Details);
    }

    // ── IActivityRepository — GetActivitiesByTimeRangeAsync ───────────────────

    [Fact]
    public async Task GetActivitiesByTimeRange_ReturnsEntriesWithinRange()
    {
        var ws = $"/ws/{Guid.NewGuid():N}";
        var e1 = MakeActivityAt(ws, new DateTime(2026, 2, 24, 10, 0, 0, DateTimeKind.Utc));
        var e2 = MakeActivityAt(ws, new DateTime(2026, 2, 24, 12, 0, 0, DateTimeKind.Utc));
        var e3 = MakeActivityAt(ws, new DateTime(2026, 2, 24, 14, 0, 0, DateTimeKind.Utc));

        await _activityRepo.SaveActivityAsync(e1);
        await _activityRepo.SaveActivityAsync(e2);
        await _activityRepo.SaveActivityAsync(e3);

        var results = await _activityRepo.GetActivitiesByTimeRangeAsync(ws,
            new DateTime(2026, 2, 24, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 24, 13, 0, 0, DateTimeKind.Utc));

        // e1 (10:00) and e2 (12:00) are within range; e3 (14:00) is outside
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task GetActivitiesByTimeRange_ExcludesOutsideRange()
    {
        var ws = $"/ws/{Guid.NewGuid():N}";
        var e1 = MakeActivityAt(ws, new DateTime(2026, 2, 24, 8, 0, 0, DateTimeKind.Utc));

        await _activityRepo.SaveActivityAsync(e1);

        var results = await _activityRepo.GetActivitiesByTimeRangeAsync(ws,
            new DateTime(2026, 2, 24, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.Empty(results);
    }

    // ── IActivityRepository — GetHourBucketsAsync ─────────────────────────────

    [Fact]
    public async Task GetHourBuckets_ReturnsDistinctBuckets()
    {
        var ws = $"/ws/{Guid.NewGuid():N}";
        await _activityRepo.SaveActivityAsync(MakeActivity("2026-02-24-10", ActivityType.AgentTask, "a", ws));
        await _activityRepo.SaveActivityAsync(MakeActivity("2026-02-24-10", ActivityType.AgentTask, "b", ws));
        await _activityRepo.SaveActivityAsync(MakeActivity("2026-02-24-11", ActivityType.AgentTask, "c", ws));

        var buckets = await _activityRepo.GetHourBucketsAsync(ws);

        Assert.Equal(2, buckets.Count);
        Assert.Contains("2026-02-24-10", buckets);
        Assert.Contains("2026-02-24-11", buckets);
    }

    // ── IActivityRepository — Config ──────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_ThenGetConfig_ReturnsConfig()
    {
        var config = new ActivityLogConfig
        {
            WorkspacePath      = $"/project/{Guid.NewGuid():N}",
            Enabled            = true,
            GitIntegrationMode = GitIntegrationMode.LogCommits,
            MarkdownEnabled    = true,
        };

        await _activityRepo.SaveConfigAsync(config);
        var result = await _activityRepo.GetConfigAsync(config.WorkspacePath);

        Assert.NotNull(result);
        Assert.Equal(config.WorkspacePath,      result.WorkspacePath);
        Assert.Equal(config.Enabled,            result.Enabled);
        Assert.Equal(config.GitIntegrationMode, result.GitIntegrationMode);
        Assert.Equal(config.MarkdownEnabled,    result.MarkdownEnabled);
    }

    [Fact]
    public async Task GetConfig_UnknownWorkspace_ReturnsNull()
    {
        var result = await _activityRepo.GetConfigAsync($"/does/not/exist/{Guid.NewGuid():N}");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveConfig_Upsert_UpdatesExistingConfig()
    {
        var ws = $"/project/{Guid.NewGuid():N}";
        var initial = new ActivityLogConfig
        {
            WorkspacePath      = ws,
            Enabled            = true,
            GitIntegrationMode = GitIntegrationMode.Disabled,
            MarkdownEnabled    = false,
        };
        await _activityRepo.SaveConfigAsync(initial);

        var updated = new ActivityLogConfig
        {
            WorkspacePath      = ws,
            Enabled            = false,
            GitIntegrationMode = GitIntegrationMode.GenerateMessages,
            MarkdownEnabled    = true,
        };
        await _activityRepo.SaveConfigAsync(updated);

        var result = await _activityRepo.GetConfigAsync(ws);

        Assert.NotNull(result);
        Assert.False(result.Enabled);
        Assert.Equal(GitIntegrationMode.GenerateMessages, result.GitIntegrationMode);
        Assert.True(result.MarkdownEnabled);
    }

    // ── IWorkflowRepository ───────────────────────────────────────────────────

    [Fact]
    public async Task SaveWorkflowInstance_ThenLoadRunning_ReturnsInstance()
    {
        var id = $"wf-{Guid.NewGuid():N}";
        var instance = new WorkflowInstance
        {
            InstanceId   = id,
            DefinitionId = "def-review",
            Status       = WorkflowStatus.Running,
            InputContext = new Dictionary<string, string> { ["branch"] = "main" },
        };

        await _wfRepo.SaveWorkflowInstanceAsync(instance);

        var running = await _wfRepo.LoadRunningInstancesAsync();

        Assert.Contains(running, i => i.InstanceId == id);
        var loaded = running.First(i => i.InstanceId == id);
        Assert.Equal("def-review", loaded.DefinitionId);
        Assert.Equal("main", loaded.InputContext["branch"]);
    }

    [Fact]
    public async Task SaveWorkflowInstance_Paused_AlsoReturnedByLoadRunning()
    {
        var id = $"wf-{Guid.NewGuid():N}";
        var paused = new WorkflowInstance
        {
            InstanceId = id,
            Status     = WorkflowStatus.Paused,
            IsPaused   = true,
        };

        await _wfRepo.SaveWorkflowInstanceAsync(paused);

        var running = await _wfRepo.LoadRunningInstancesAsync();

        Assert.Contains(running, i => i.InstanceId == id);
    }

    [Fact]
    public async Task SaveWorkflowInstance_Completed_NotReturnedByLoadRunning()
    {
        var id = $"wf-{Guid.NewGuid():N}";
        var completed = new WorkflowInstance
        {
            InstanceId  = id,
            Status      = WorkflowStatus.Completed,
            CompletedAt = DateTime.UtcNow,
        };

        await _wfRepo.SaveWorkflowInstanceAsync(completed);

        var running = await _wfRepo.LoadRunningInstancesAsync();

        Assert.DoesNotContain(running, i => i.InstanceId == id);
    }

    [Fact]
    public async Task DeleteWorkflowInstance_RemovesFromStorage()
    {
        var id = $"wf-{Guid.NewGuid():N}";
        var instance = new WorkflowInstance
        {
            InstanceId = id,
            Status     = WorkflowStatus.Running,
        };

        await _wfRepo.SaveWorkflowInstanceAsync(instance);
        await _wfRepo.DeleteWorkflowInstanceAsync(id);

        var running = await _wfRepo.LoadRunningInstancesAsync();

        Assert.DoesNotContain(running, i => i.InstanceId == id);
    }

    [Fact]
    public async Task SaveWorkflowInstance_Upsert_UpdatesStatus()
    {
        var id = $"wf-{Guid.NewGuid():N}";
        var instance = new WorkflowInstance
        {
            InstanceId = id,
            Status     = WorkflowStatus.Running,
        };

        await _wfRepo.SaveWorkflowInstanceAsync(instance);

        instance.Status      = WorkflowStatus.Completed;
        instance.CompletedAt = DateTime.UtcNow;
        await _wfRepo.SaveWorkflowInstanceAsync(instance);

        var running = await _wfRepo.LoadRunningInstancesAsync();

        // After update to Completed the instance should no longer be returned
        Assert.DoesNotContain(running, i => i.InstanceId == id);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ActivityEntry MakeActivity(
        string hourBucket, ActivityType type, string summary,
        string workspacePath = "/test/workspace")
    {
        var ts = ParseBucketToTimestamp(hourBucket);
        return new ActivityEntry
        {
            WorkspacePath = workspacePath,
            Timestamp     = ts,
            HourBucket    = hourBucket,
            ActivityType  = type,
            Actor         = "test",
            Summary       = summary,
        };
    }

    private static ActivityEntry MakeActivityAt(string workspacePath, DateTime timestamp)
    {
        var hourBucket = timestamp.ToString("yyyy-MM-dd-HH");
        return new ActivityEntry
        {
            WorkspacePath = workspacePath,
            Timestamp     = timestamp,
            HourBucket    = hourBucket,
            ActivityType  = ActivityType.AgentTask,
            Actor         = "test",
            Summary       = "auto",
        };
    }

    private static DateTime ParseBucketToTimestamp(string hourBucket)
    {
        var parts = hourBucket.Split('-');
        return new DateTime(
            int.Parse(parts[0]), int.Parse(parts[1]),
            int.Parse(parts[2]), int.Parse(parts[3]),
            0, 0, DateTimeKind.Utc);
    }
}
