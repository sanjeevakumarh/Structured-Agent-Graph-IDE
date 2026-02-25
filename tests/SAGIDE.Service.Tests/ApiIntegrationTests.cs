using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integration tests for the REST API endpoints.
/// Uses <see cref="WebApplicationFactory{TEntryPoint}"/> with configuration overrides
/// to redirect SQLite to a temp file and disable all background hosted services.
/// </summary>
public class ApiIntegrationTests : IClassFixture<ApiIntegrationTests.TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests(TestFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // ── Health ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var resp = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostTask_ValidBody_Returns201WithTaskId()
    {
        // AgentType enum defaults to 0 (CodeReview) when omitted; only description is required.
        var payload = new { description = "Unit test task" };

        var resp = await _client.PostAsJsonAsync("/api/tasks", payload);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("taskId", body);
    }

    [Fact]
    public async Task PostTask_MissingDescription_Returns400()
    {
        var payload = new { agentType = "Generic" };

        var resp = await _client.PostAsJsonAsync("/api/tasks", payload);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetTasks_ReturnsOkWithList()
    {
        var resp = await _client.GetAsync("/api/tasks");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body.Trim());
    }

    [Fact]
    public async Task GetTaskById_UnknownId_Returns404()
    {
        var resp = await _client.GetAsync("/api/tasks/does-not-exist-00000");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteTask_UnknownId_ReturnsOk()
    {
        // Cancel is idempotent — unknown IDs are silently ignored
        var resp = await _client.DeleteAsync("/api/tasks/phantom-task-99");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PostTask_SourceTagPreserved_AppearInList()
    {
        var uniqueTag = $"test-tag-{Guid.NewGuid():N}";
        var payload   = new { description = "Tagged task", sourceTag = uniqueTag };

        var post = await _client.PostAsJsonAsync("/api/tasks", payload);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var list = await _client.GetAsync($"/api/tasks?tag={uniqueTag}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var tasks = JsonDocument.Parse(await list.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        Assert.True(tasks.Count >= 1, "Expected at least one task with the unique tag");
    }

    // ── Results ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResults_ReturnsOkWithList()
    {
        var resp = await _client.GetAsync("/api/results");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body.Trim());
    }

    [Fact]
    public async Task GetResultById_Unknown_Returns404()
    {
        var resp = await _client.GetAsync("/api/results/no-such-task");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Prompts ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPrompts_ReturnsOkWithList()
    {
        var resp = await _client.GetAsync("/api/prompts");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body.Trim());
    }

    [Fact]
    public async Task GetPromptsByDomain_EmptyDomain_ReturnsOkWithEmptyList()
    {
        var resp = await _client.GetAsync("/api/prompts/nonexistent-domain");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body.Trim());
    }

    [Fact]
    public async Task GetPrompt_UnknownDomainAndName_Returns404()
    {
        var resp = await _client.GetAsync("/api/prompts/ghost/phantom");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task RunPrompt_UnknownPrompt_Returns404()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/prompts/ghost/phantom/run",
            new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that:
    /// <list type="bullet">
    ///   <item>Redirects SQLite to a temporary file</item>
    ///   <item>Points the prompt registry at an empty temp directory</item>
    ///   <item>Removes all background hosted services (pipe server, scheduler, Ollama monitor)</item>
    /// </list>
    /// </summary>
    public sealed class TestFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dbPath;
        private readonly string _promptsDir;

        public TestFactory()
        {
            _dbPath     = Path.Combine(Path.GetTempPath(), $"sagide-api-test-{Guid.NewGuid():N}.db");
            _promptsDir = Path.Combine(Path.GetTempPath(), $"sagide-prompts-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_promptsDir);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Override configuration BEFORE the app reads it
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Add(new MemoryConfigurationSource
                {
                    InitialData = new Dictionary<string, string?>
                    {
                        ["SAGIDE:Database:Path"]     = _dbPath,
                        ["SAGIDE:PromptsPath"]        = _promptsDir,
                        ["SAGIDE:Scheduler:Enabled"] = "false",
                        // Empty Ollama server list — no HTTP polls
                        ["SAGIDE:Ollama:Servers:0:BaseUrl"] = null,
                    }
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // Remove ALL background hosted services so tests don't start
                // named pipes, the orchestrator processing loop, or the scheduler.
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IHostedService))
                    .ToList();
                foreach (var desc in toRemove)
                    services.Remove(desc);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
                try { if (Directory.Exists(_promptsDir)) Directory.Delete(_promptsDir, true); } catch { }
            }
        }
    }
}
