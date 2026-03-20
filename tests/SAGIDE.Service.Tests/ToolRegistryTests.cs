using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Interfaces;
using SAGIDE.Security;
using SAGIDE.Tools;
using SAGIDE.Tools.Tools;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="InProcessToolRegistry"/>, <see cref="WebFetchTool"/>,
/// <see cref="WebSearchTool"/>, and <see cref="GitTool"/> covering:
/// - Registration and retrieval
/// - ExecuteAsync dispatches to the correct tool
/// - Missing tool throws with helpful message listing available tools
/// - Missing required parameter throws ArgumentException
/// - WebFetchTool delegates to the provided fetch function
/// - WebSearchTool delegates to the provided search function
/// - GitTool blocks write subcommands
/// - GitTool allows read subcommands and forwards output
/// - GitTool forwards non-zero exit code as error prefix
/// - Audit log is called on each tool execution
/// </summary>
public class ToolRegistryTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static InProcessToolRegistry MakeRegistry(IAuditLog? auditLog = null)
        => new(NullLogger<InProcessToolRegistry>.Instance, auditLog);

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void Register_And_Get_RoundTrips()
    {
        var registry = MakeRegistry();
        var tool     = new WebFetchTool((_, _) => Task.FromResult("body"));
        registry.Register(tool);

        var retrieved = registry.Get("web_fetch");
        Assert.NotNull(retrieved);
        Assert.Equal("web_fetch", retrieved.Name);
    }

    [Fact]
    public void Get_UnknownName_ReturnsNull()
    {
        var registry = MakeRegistry();
        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void Register_Twice_Overwrites()
    {
        var registry = MakeRegistry();
        registry.Register(new WebFetchTool((_, _) => Task.FromResult("first")));
        registry.Register(new WebFetchTool((_, _) => Task.FromResult("second")));

        // Only one entry — count manually to avoid xUnit2031
        Assert.Equal(1, registry.All.Count(t => t.Name == "web_fetch"));
    }

    [Fact]
    public void All_ListsAllRegisteredTools()
    {
        var registry = MakeRegistry();
        registry.Register(new WebFetchTool((_, _) => Task.FromResult("")));
        registry.Register(new WebSearchTool((_, _) => Task.FromResult("")));

        Assert.Equal(2, registry.All.Count);
        Assert.Contains(registry.All, t => t.Name == "web_fetch");
        Assert.Contains(registry.All, t => t.Name == "web_search");
    }

    // ── ExecuteAsync dispatch ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DispatchesToCorrectTool()
    {
        var registry = MakeRegistry();
        registry.Register(new WebFetchTool((url, _) => Task.FromResult($"fetched:{url}")));

        var result = await registry.ExecuteAsync("web_fetch",
            new Dictionary<string, string> { ["url"] = "http://example.com" });

        Assert.Equal("fetched:http://example.com", result);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_ThrowsWithAvailableList()
    {
        var registry = MakeRegistry();
        registry.Register(new WebFetchTool((_, _) => Task.FromResult("")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ExecuteAsync("nonexistent", new Dictionary<string, string>()));

        Assert.Contains("nonexistent", ex.Message);
        Assert.Contains("web_fetch",   ex.Message); // lists available
    }

    // ── Audit log integration ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_RecordsToolCallInAuditLog()
    {
        var audit    = new RecordingAuditLog();
        var registry = MakeRegistry(audit);
        registry.Register(new WebSearchTool((_, _) => Task.FromResult("results")));

        await registry.ExecuteAsync("web_search",
            new Dictionary<string, string> { ["query"] = "test query" });

        Assert.Single(audit.ToolCalls);
        Assert.Equal("web_search", audit.ToolCalls[0].ToolName);
    }

    // ── WebFetchTool ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WebFetchTool_MissingUrl_ThrowsArgumentException()
    {
        var tool = new WebFetchTool((_, _) => Task.FromResult(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task WebFetchTool_EmptyUrl_ThrowsArgumentException()
    {
        var tool = new WebFetchTool((_, _) => Task.FromResult(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(new Dictionary<string, string> { ["url"] = "   " }));
    }

    [Fact]
    public async Task WebFetchTool_ValidUrl_ReturnsDelegateResult()
    {
        var tool   = new WebFetchTool((url, _) => Task.FromResult($"content of {url}"));
        var result = await tool.ExecuteAsync(
            new Dictionary<string, string> { ["url"] = "http://test.com" });

        Assert.Equal("content of http://test.com", result);
    }

    // ── WebSearchTool ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WebSearchTool_MissingQuery_ThrowsArgumentException()
    {
        var tool = new WebSearchTool((_, _) => Task.FromResult(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task WebSearchTool_ValidQuery_ReturnsDelegateResult()
    {
        var tool   = new WebSearchTool((q, _) => Task.FromResult($"results for {q}"));
        var result = await tool.ExecuteAsync(
            new Dictionary<string, string> { ["query"] = "ollama models" });

        Assert.Equal("results for ollama models", result);
    }

    // ── GitTool ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GitTool_ReadCommand_Succeeds()
    {
        var tool = new GitTool((_, cmd, _) =>
            Task.FromResult(($"log output for '{cmd}'", 0)));

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["workspace"] = "/repo",
            ["command"]   = "log --oneline -5",
        });

        Assert.Contains("log output", result);
    }

    [Fact]
    public async Task GitTool_WriteCommand_ThrowsInvalidOperation()
    {
        var tool = new GitTool((_, _, _) => Task.FromResult(("", 0)));

        foreach (var blocked in new[] { "commit", "push", "reset", "checkout", "rebase" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.ExecuteAsync(new Dictionary<string, string>
                {
                    ["workspace"] = "/repo",
                    ["command"]   = $"{blocked} something",
                }));
        }
    }

    [Fact]
    public async Task GitTool_NonZeroExitCode_ReturnsPrefixedOutput()
    {
        var tool = new GitTool((_, _, _) =>
            Task.FromResult(("fatal: not a git repository", 128)));

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["workspace"] = "/not-a-repo",
            ["command"]   = "status",
        });

        Assert.Contains("128",  result);
        Assert.Contains("fatal", result);
    }

    [Fact]
    public async Task GitTool_MissingWorkspace_ThrowsArgumentException()
    {
        var tool = new GitTool((_, _, _) => Task.FromResult(("", 0)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(new Dictionary<string, string> { ["command"] = "log" }));
    }

    [Fact]
    public async Task GitTool_MissingCommand_ThrowsArgumentException()
    {
        var tool = new GitTool((_, _, _) => Task.FromResult(("", 0)));
        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.ExecuteAsync(new Dictionary<string, string> { ["workspace"] = "/repo" }));
    }

    // ── Tool metadata ─────────────────────────────────────────────────────────

    [Fact]
    public void Tools_HaveNonEmptyDescriptions()
    {
        ITool[] tools =
        [
            new WebFetchTool((_, _)  => Task.FromResult("")),
            new WebSearchTool((_, _) => Task.FromResult("")),
            new GitTool((_, _, _)    => Task.FromResult(("", 0))),
        ];

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name),        $"{tool.GetType().Name}.Name is empty");
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.GetType().Name}.Description is empty");
        }
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    private sealed class RecordingAuditLog : IAuditLog
    {
        public record ToolCallRecord(string ToolName, IReadOnlyDictionary<string, string> Params);
        public List<ToolCallRecord> ToolCalls { get; } = [];

        public Task RecordTaskSubmittedAsync(string taskId, string agentType, string modelProvider,
            string modelId, string sourceTag, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordToolCallAsync(string toolName,
            IReadOnlyDictionary<string, string> parameters, string callerTag,
            CancellationToken ct = default)
        {
            ToolCalls.Add(new ToolCallRecord(toolName, parameters));
            return Task.CompletedTask;
        }

        public Task RecordAuthFailureAsync(string path, string? remoteIp,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int limit = 100,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }
}
