using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Interfaces;
using SAGIDE.Security;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="NullAuditLog"/> and <see cref="SqliteAuditLog"/> covering:
/// - NullAuditLog is truly a no-op
/// - SqliteAuditLog persists records and retrieves them in reverse-chronological order
/// - All three event types round-trip correctly
/// - GetRecentAsync respects the limit parameter
/// - Concurrent writes don't corrupt the store
/// </summary>
public class AuditLogTests : IDisposable
{
    // ── NullAuditLog ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NullAuditLog_AllOperations_CompleteWithoutError()
    {
        IAuditLog log = NullAuditLog.Instance;

        await log.RecordTaskSubmittedAsync("t1", "Generic", "Ollama", "llama3", "test");
        await log.RecordToolCallAsync("git", new Dictionary<string, string> { ["cmd"] = "status" }, "test");
        await log.RecordAuthFailureAsync("/api/tasks", "127.0.0.1");

        var entries = await log.GetRecentAsync();
        Assert.Empty(entries);
    }

    // ── SqliteAuditLog ────────────────────────────────────────────────────────

    private readonly string _dbPath;

    public AuditLogTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        // Release all pooled SQLite connections before deleting the temp file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* best-effort: temp files are cleaned up by OS eventually */ }
    }

    private SqliteAuditLog MakeLog() =>
        new(_dbPath, NullLogger<SqliteAuditLog>.Instance);

    [Fact]
    public async Task TaskSubmitted_RoundTrips()
    {
        var log = MakeLog();
        await Task.Delay(50); // allow table init to complete

        await log.RecordTaskSubmittedAsync("task-abc", "CodeReview", "Claude", "claude-3", "vscode");
        await Task.Delay(50); // allow fire-and-forget write

        var entries = await log.GetRecentAsync(10);

        Assert.Single(entries);
        Assert.Equal("task_submitted", entries[0].EventType);
        Assert.Equal("task-abc",       entries[0].Subject);
        Assert.Equal("vscode",         entries[0].Actor);
        Assert.Contains("CodeReview",  entries[0].Detail);
    }

    [Fact]
    public async Task ToolCall_RoundTrips()
    {
        var log = MakeLog();
        await Task.Delay(50);

        await log.RecordToolCallAsync(
            "git",
            new Dictionary<string, string> { ["cmd"] = "diff" },
            "scheduler");
        await Task.Delay(50);

        var entries = await log.GetRecentAsync();

        Assert.Single(entries);
        Assert.Equal("tool_call", entries[0].EventType);
        Assert.Equal("git",       entries[0].Subject);
        Assert.Equal("scheduler", entries[0].Actor);
        Assert.Contains("diff",   entries[0].Detail);
    }

    [Fact]
    public async Task AuthFailure_RoundTrips()
    {
        var log = MakeLog();
        await Task.Delay(50);

        await log.RecordAuthFailureAsync("/api/tasks", "10.0.0.1");
        await Task.Delay(50);

        var entries = await log.GetRecentAsync();

        Assert.Single(entries);
        Assert.Equal("auth_failure",  entries[0].EventType);
        Assert.Equal("/api/tasks",    entries[0].Subject);
        Assert.Contains("10.0.0.1",  entries[0].Detail);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimit()
    {
        var log = MakeLog();
        await Task.Delay(50);

        for (var i = 0; i < 10; i++)
            await log.RecordTaskSubmittedAsync($"t{i}", "Generic", "Ollama", "m", "test");

        await Task.Delay(100);

        var five = await log.GetRecentAsync(5);
        Assert.Equal(5, five.Count);
    }

    [Fact]
    public async Task GetRecentAsync_OrderedNewestFirst()
    {
        var log = MakeLog();
        await Task.Delay(50);

        await log.RecordTaskSubmittedAsync("first",  "Generic", "Ollama", "m", "test");
        await Task.Delay(20);
        await log.RecordTaskSubmittedAsync("second", "Generic", "Ollama", "m", "test");
        await Task.Delay(50);

        var entries = await log.GetRecentAsync(10);
        Assert.Equal(2, entries.Count);
        // Newest first
        Assert.Equal("second", entries[0].Subject);
        Assert.Equal("first",  entries[1].Subject);
    }

    [Fact]
    public async Task MultipleEventTypes_AllVisible()
    {
        var log = MakeLog();
        await Task.Delay(50);

        await log.RecordTaskSubmittedAsync("t1", "Generic", "Ollama", "m", "test");
        await log.RecordToolCallAsync("git", new Dictionary<string, string>(), "test");
        await log.RecordAuthFailureAsync("/api/tasks", "1.2.3.4");
        await Task.Delay(100);

        var entries = await log.GetRecentAsync(10);
        Assert.Equal(3, entries.Count);

        var types = entries.Select(e => e.EventType).ToHashSet();
        Assert.Contains("task_submitted", types);
        Assert.Contains("tool_call",      types);
        Assert.Contains("auth_failure",   types);
    }

    [Fact]
    public async Task ConcurrentWrites_AllPersisted()
    {
        var log = MakeLog();
        await Task.Delay(50);

        var tasks = Enumerable.Range(0, 20)
            .Select(i => log.RecordTaskSubmittedAsync($"t{i}", "Generic", "Ollama", "m", "test"))
            .ToArray();
        await Task.WhenAll(tasks);
        await Task.Delay(150);

        var entries = await log.GetRecentAsync(100);
        Assert.Equal(20, entries.Count);
    }
}
