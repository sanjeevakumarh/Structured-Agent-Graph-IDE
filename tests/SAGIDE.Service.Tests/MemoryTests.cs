using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Interfaces;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Persistence;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="InMemorySessionMemory"/> and <see cref="SqliteProjectMemory"/>.
/// </summary>
public class MemoryTests : IDisposable
{
    // ══════════════════════════════════════════════════════════════════════════
    // InMemorySessionMemory
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SessionMemory_SetAndGet_RoundTrips()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("key1", "value1");
        Assert.Equal("value1", mem.Get("key1"));
    }

    [Fact]
    public void SessionMemory_Get_MissingKey_ReturnsNull()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        Assert.Null(mem.Get("nonexistent"));
    }

    [Fact]
    public void SessionMemory_Contains_TrueAfterSet()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("x", "y");
        Assert.True(mem.Contains("x"));
        Assert.False(mem.Contains("z"));
    }

    [Fact]
    public void SessionMemory_All_ReflectsCurrentState()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("a", "1");
        mem.Set("b", "2");

        Assert.Equal(2, mem.All.Count);
        Assert.Equal("1", mem.All["a"]);
        Assert.Equal("2", mem.All["b"]);
    }

    [Fact]
    public void SessionMemory_Set_OverwritesExistingValue()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("k", "first");
        mem.Set("k", "second");
        Assert.Equal("second", mem.Get("k"));
        Assert.Single(mem.All);
    }

    [Fact]
    public void SessionMemory_Clear_RemovesAllEntries()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("a", "1");
        mem.Set("b", "2");
        mem.Clear();

        Assert.Empty(mem.All);
        Assert.Null(mem.Get("a"));
        Assert.False(mem.Contains("b"));
    }

    [Fact]
    public void SessionMemory_KeyLookup_IsCaseInsensitive()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        mem.Set("SearchResults", "some data");

        Assert.Equal("some data", mem.Get("searchresults"));
        Assert.Equal("some data", mem.Get("SEARCHRESULTS"));
        Assert.True(mem.Contains("searchResults"));
    }

    [Fact]
    public async Task SessionMemory_ConcurrentWrites_ThreadSafe()
    {
        ISessionMemory mem = new InMemorySessionMemory();

        // 50 parallel writes to distinct keys
        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => mem.Set($"key{i}", $"val{i}"))));

        Assert.Equal(50, mem.All.Count);
    }

    [Fact]
    public void SessionMemory_FreshInstance_IsEmpty()
    {
        ISessionMemory mem = new InMemorySessionMemory();
        Assert.Empty(mem.All);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SqliteProjectMemory
    // ══════════════════════════════════════════════════════════════════════════

    private readonly string _dbPath;

    public MemoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"projmem-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch { /* best-effort */ }
    }

    private SqliteProjectMemory MakeProjectMemory() =>
        new(_dbPath, NullLogger<SqliteProjectMemory>.Instance);

    [Fact]
    public async Task ProjectMemory_SetAndGet_RoundTrips()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50); // allow table init

        await mem.SetAsync("/repo", "git_summary", "100 commits");
        await Task.Delay(30);

        var result = await mem.GetAsync("/repo", "git_summary");
        Assert.Equal("100 commits", result);
    }

    [Fact]
    public async Task ProjectMemory_Get_MissingKey_ReturnsNull()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        var result = await mem.GetAsync("/repo", "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task ProjectMemory_Set_UpsertUpdatesExistingValue()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        await mem.SetAsync("/ws", "key", "first");
        await mem.SetAsync("/ws", "key", "second");
        await Task.Delay(50);

        var result = await mem.GetAsync("/ws", "key");
        Assert.Equal("second", result);
    }

    [Fact]
    public async Task ProjectMemory_GetAll_ReturnsAllKeysForWorkspace()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        await mem.SetAsync("/ws", "a", "1");
        await mem.SetAsync("/ws", "b", "2");
        await mem.SetAsync("/ws", "c", "3");
        await Task.Delay(50);

        var all = await mem.GetAllAsync("/ws");
        Assert.Equal(3, all.Count);
        Assert.Equal("1", all["a"]);
        Assert.Equal("2", all["b"]);
        Assert.Equal("3", all["c"]);
    }

    [Fact]
    public async Task ProjectMemory_GetAll_WorkspacesAreIsolated()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        await mem.SetAsync("/ws-a", "key", "alpha");
        await mem.SetAsync("/ws-b", "key", "beta");
        await Task.Delay(50);

        var a = await mem.GetAllAsync("/ws-a");
        var b = await mem.GetAllAsync("/ws-b");

        Assert.Equal("alpha", a["key"]);
        Assert.Equal("beta",  b["key"]);
    }

    [Fact]
    public async Task ProjectMemory_Delete_RemovesEntry()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        await mem.SetAsync("/ws", "removeme", "value");
        await mem.SetAsync("/ws", "keepme",   "value");
        await Task.Delay(50);

        await mem.DeleteAsync("/ws", "removeme");
        await Task.Delay(30);

        var all = await mem.GetAllAsync("/ws");
        Assert.Single(all);
        Assert.False(all.ContainsKey("removeme"));
        Assert.True(all.ContainsKey("keepme"));
    }

    [Fact]
    public async Task ProjectMemory_Delete_NonExistentKey_DoesNotThrow()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        // Should complete without exception
        await mem.DeleteAsync("/ws", "ghost");
    }

    [Fact]
    public async Task ProjectMemory_GetAll_EmptyWorkspace_ReturnsEmptyDict()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        var all = await mem.GetAllAsync("/nonexistent-workspace");
        Assert.Empty(all);
    }

    [Fact]
    public async Task ProjectMemory_PersistsAcrossInstances()
    {
        // Write with first instance
        var mem1 = MakeProjectMemory();
        await Task.Delay(50);
        await mem1.SetAsync("/ws", "persistent_key", "persistent_value");
        await Task.Delay(50);

        // Read with second instance (same DB path)
        var mem2 = MakeProjectMemory();
        await Task.Delay(50);
        var result = await mem2.GetAsync("/ws", "persistent_key");

        Assert.Equal("persistent_value", result);
    }

    [Fact]
    public async Task ProjectMemory_ConcurrentWrites_AllPersisted()
    {
        var mem = MakeProjectMemory();
        await Task.Delay(50);

        var writes = Enumerable.Range(0, 20)
            .Select(i => mem.SetAsync("/ws", $"key{i}", $"val{i}"))
            .ToArray();
        await Task.WhenAll(writes);
        await Task.Delay(100);

        var all = await mem.GetAllAsync("/ws");
        Assert.Equal(20, all.Count);
    }
}
