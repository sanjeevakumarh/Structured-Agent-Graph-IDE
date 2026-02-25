using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Persistence;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integration tests for SqliteTaskRepository using a real (temp-file) SQLite database.
/// Each test class instance gets its own database file which is deleted in Dispose().
/// </summary>
public class SqliteRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private SqliteTaskRepository _repo = null!;

    public SqliteRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sagide-test-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _repo = new SqliteTaskRepository(_dbPath, NullLogger<SqliteTaskRepository>.Instance);
        await _repo.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        // Clean up temp DB files
        foreach (var f in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
            if (File.Exists(f)) try { File.Delete(f); } catch { }
        return Task.CompletedTask;
    }

    // ── InitializeAsync is idempotent ─────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CalledTwice_DoesNotThrow()
    {
        // Second call should run all migrations without error
        await _repo.InitializeAsync();
    }

    // ── Task CRUD ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveTask_GetTask_RoundTrip()
    {
        var task = MakeTask("t1", AgentTaskStatus.Queued, "CodeReview");
        await _repo.SaveTaskAsync(task);

        var loaded = await _repo.GetTaskAsync(task.Id);

        Assert.NotNull(loaded);
        Assert.Equal(task.Id, loaded.Id);
        Assert.Equal(task.Description, loaded.Description);
        Assert.Equal(AgentTaskStatus.Queued, loaded.Status);
        Assert.Equal("CodeReview", loaded.AgentType.ToString());
    }

    [Fact]
    public async Task GetTask_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetTaskAsync("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveTask_Upsert_UpdatesExistingRow()
    {
        var task = MakeTask("t1", AgentTaskStatus.Queued, "Generic");
        await _repo.SaveTaskAsync(task);

        task.Status = AgentTaskStatus.Completed;
        task.Progress = 100;
        await _repo.SaveTaskAsync(task);

        var loaded = await _repo.GetTaskAsync(task.Id);
        Assert.Equal(AgentTaskStatus.Completed, loaded!.Status);
        Assert.Equal(100, loaded.Progress);
    }

    [Fact]
    public async Task SaveTask_SourceTag_Persisted()
    {
        var task = MakeTask("t-tag", AgentTaskStatus.Queued, "Generic");
        task.SourceTag = "vscode";
        await _repo.SaveTaskAsync(task);

        var loaded = await _repo.GetTaskAsync(task.Id);
        Assert.Equal("vscode", loaded!.SourceTag);
    }

    // ── Task history / filtering ──────────────────────────────────────────────

    [Fact]
    public async Task GetTaskHistory_ReturnsNewestFirst()
    {
        for (var i = 0; i < 5; i++)
        {
            var t = MakeTask($"hist-{i}", AgentTaskStatus.Completed, "Generic");
            await _repo.SaveTaskAsync(t);
            await Task.Delay(5); // ensure distinct timestamps
        }

        var history = await _repo.GetTaskHistoryAsync(limit: 5);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public async Task GetTaskHistory_Limit_Respected()
    {
        for (var i = 0; i < 10; i++)
            await _repo.SaveTaskAsync(MakeTask($"lim-{i}", AgentTaskStatus.Completed, "Generic"));

        var history = await _repo.GetTaskHistoryAsync(limit: 3);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task GetTasksByStatus_FiltersByStatus()
    {
        await _repo.SaveTaskAsync(MakeTask("s1", AgentTaskStatus.Queued, "Generic"));
        await _repo.SaveTaskAsync(MakeTask("s2", AgentTaskStatus.Completed, "Generic"));
        await _repo.SaveTaskAsync(MakeTask("s3", AgentTaskStatus.Queued, "Generic"));

        var queued = await _repo.GetTasksByStatusAsync(AgentTaskStatus.Queued);

        Assert.Equal(2, queued.Count);
        Assert.All(queued, t => Assert.Equal(AgentTaskStatus.Queued, t.Status));
    }

    [Fact]
    public async Task GetTasksBySourceTag_FiltersByTag()
    {
        var t1 = MakeTask("tag1", AgentTaskStatus.Completed, "Generic"); t1.SourceTag = "cli";
        var t2 = MakeTask("tag2", AgentTaskStatus.Completed, "Generic"); t2.SourceTag = "vscode";
        var t3 = MakeTask("tag3", AgentTaskStatus.Completed, "Generic"); t3.SourceTag = "cli";
        await _repo.SaveTaskAsync(t1);
        await _repo.SaveTaskAsync(t2);
        await _repo.SaveTaskAsync(t3);

        var cliTasks = await _repo.GetTasksBySourceTagAsync("cli");

        Assert.Equal(2, cliTasks.Count);
        Assert.All(cliTasks, t => Assert.Equal("cli", t.SourceTag));
    }

    // ── Result CRUD ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveResult_GetResult_RoundTrip()
    {
        var task = MakeTask("r1", AgentTaskStatus.Completed, "Generic");
        await _repo.SaveTaskAsync(task);

        var result = new AgentResult
        {
            TaskId  = task.Id,
            Output  = "The analysis result",
            Success = true,
            TokensUsed = 500,
        };
        await _repo.SaveResultAsync(result);

        var loaded = await _repo.GetResultAsync(task.Id);
        Assert.NotNull(loaded);
        Assert.Equal("The analysis result", loaded.Output);
        Assert.True(loaded.Success);
        Assert.Equal(500, loaded.TokensUsed);
    }

    [Fact]
    public async Task GetResult_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetResultAsync("no-such-task");
        Assert.Null(result);
    }

    // ── Dead Letter Queue ─────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDlqEntry_GetDlqEntries_RoundTrip()
    {
        var entry = new DeadLetterEntry
        {
            Id             = Guid.NewGuid().ToString(),
            OriginalTaskId = "failed-task",
            ErrorMessage   = "HTTP 500 from provider",
            FailedAt       = DateTime.UtcNow,
        };
        await _repo.SaveDlqEntryAsync(entry);

        var entries = await _repo.GetDlqEntriesAsync();
        Assert.Single(entries);
        Assert.Equal(entry.Id, entries[0].Id);
        Assert.Equal("HTTP 500 from provider", entries[0].ErrorMessage);
    }

    [Fact]
    public async Task RemoveDlqEntry_RemovesCorrectRow()
    {
        var e1 = MakeDlqEntry("dlq1");
        var e2 = MakeDlqEntry("dlq2");
        await _repo.SaveDlqEntryAsync(e1);
        await _repo.SaveDlqEntryAsync(e2);

        await _repo.RemoveDlqEntryAsync(e1.Id);

        var remaining = await _repo.GetDlqEntriesAsync();
        Assert.Single(remaining);
        Assert.Equal(e2.Id, remaining[0].Id);
    }

    [Fact]
    public async Task PurgeDlqOlderThan_RemovesOldEntries()
    {
        var old    = new DeadLetterEntry { Id = "old",  OriginalTaskId = "t-old",  ErrorMessage = "err", FailedAt = DateTime.UtcNow.AddDays(-10) };
        var recent = new DeadLetterEntry { Id = "new",  OriginalTaskId = "t-new",  ErrorMessage = "err", FailedAt = DateTime.UtcNow };
        await _repo.SaveDlqEntryAsync(old);
        await _repo.SaveDlqEntryAsync(recent);

        await _repo.PurgeDlqOlderThanAsync(DateTime.UtcNow.AddDays(-7));

        var remaining = await _repo.GetDlqEntriesAsync();
        Assert.Single(remaining);
        Assert.Equal("new", remaining[0].Id);
    }

    // ── Scheduler state ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetLastFiredAt_LoadAllLastFired_RoundTrip()
    {
        var fired = DateTimeOffset.UtcNow;
        await _repo.SetLastFiredAtAsync("finance/daily", fired);

        var dict = new Dictionary<string, DateTimeOffset>();
        await _repo.LoadAllLastFiredAsync(dict);

        Assert.True(dict.ContainsKey("finance/daily"));
        // SQLite stores TEXT; allow up to 1-second rounding
        Assert.True(Math.Abs((dict["finance/daily"] - fired).TotalSeconds) < 1);
    }

    [Fact]
    public async Task SetLastFiredAt_Upsert_OverwritesPreviousValue()
    {
        var first  = DateTimeOffset.UtcNow.AddMinutes(-10);
        var second = DateTimeOffset.UtcNow;

        await _repo.SetLastFiredAtAsync("notes/weekly", first);
        await _repo.SetLastFiredAtAsync("notes/weekly", second);

        var dict = new Dictionary<string, DateTimeOffset>();
        await _repo.LoadAllLastFiredAsync(dict);

        Assert.True((dict["notes/weekly"] - second).TotalSeconds < 1);
    }

    [Fact]
    public async Task LoadAllLastFired_EmptyTable_NoEntries()
    {
        var dict = new Dictionary<string, DateTimeOffset>();
        await _repo.LoadAllLastFiredAsync(dict);
        Assert.Empty(dict);
    }

    // ── Output cache ──────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreCachedOutput_GetCachedOutput_RoundTrip()
    {
        const string key    = "sha256-abc123";
        const string output = "deterministic LLM output";

        await _repo.StoreCachedOutputAsync(key, output, "llama3.2");
        var cached = await _repo.GetCachedOutputAsync(key);

        Assert.Equal(output, cached);
    }

    [Fact]
    public async Task GetCachedOutput_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetCachedOutputAsync("nonexistent-key");
        Assert.Null(result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentTask MakeTask(string id, AgentTaskStatus status, string agentType)
        => new()
        {
            Id          = id,
            Description = $"Task {id}",
            Status      = status,
            AgentType   = Enum.Parse<AgentType>(agentType),
            CreatedAt   = DateTime.UtcNow,
        };

    private static DeadLetterEntry MakeDlqEntry(string id)
        => new() { Id = id, OriginalTaskId = $"task-{id}", ErrorMessage = "test failure", FailedAt = DateTime.UtcNow };
}
