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
/// Integration tests for the <c>/api/skills</c> REST endpoints.
/// Uses an isolated <see cref="WebApplicationFactory{TEntryPoint}"/> with a temp skills
/// directory containing known test YAMLs so assertions are deterministic.
/// </summary>
public class SkillsEndpointsTests : IClassFixture<SkillsEndpointsTests.TestFactory>, IDisposable
{
    private readonly TestFactory _factory;
    private readonly HttpClient  _client;

    public SkillsEndpointsTests(TestFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    // ── GET /api/skills ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSkills_ReturnsOkWithJsonArray()
    {
        var resp = await _client.GetAsync("/api/skills");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.StartsWith("[", body.Trim());
    }

    [Fact]
    public async Task GetSkills_ContainsBothTestSkills()
    {
        var resp = await _client.GetAsync("/api/skills");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var names = json.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("test-collector", names);
        Assert.Contains("test-analyzer",  names);
    }

    [Fact]
    public async Task GetSkills_SummaryFields_Present()
    {
        var resp  = await _client.GetAsync("/api/skills");
        var items = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();

        Assert.NotEmpty(items);
        var first = items[0];
        // Confirm the summary shape expected by SkillLibraryProvider
        Assert.True(first.TryGetProperty("name",               out _), "missing 'name'");
        Assert.True(first.TryGetProperty("domain",             out _), "missing 'domain'");
        Assert.True(first.TryGetProperty("version",            out _), "missing 'version'");
        Assert.True(first.TryGetProperty("protocolImplements", out _), "missing 'protocolImplements'");
        Assert.True(first.TryGetProperty("capabilitySlots",    out _), "missing 'capabilitySlots'");
        Assert.True(first.TryGetProperty("implementationSteps",out _), "missing 'implementationSteps'");
    }

    // ── GET /api/skills/{domain}/{name} ───────────────────────────────────────

    [Fact]
    public async Task GetSkillByKey_KnownSkill_ReturnsFullDefinition()
    {
        var resp = await _client.GetAsync("/api/skills/test/test-collector");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("test-collector", json.GetProperty("name").GetString());
        Assert.Equal("test",           json.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task GetSkillByKey_CaseInsensitive()
    {
        var resp = await _client.GetAsync("/api/skills/Test/Test-Collector");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetSkillByKey_UnknownSkill_Returns404()
    {
        var resp = await _client.GetAsync("/api/skills/test/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetSkillByKey_UnknownDomain_Returns404()
    {
        var resp = await _client.GetAsync("/api/skills/ghost/test-collector");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── GET /api/skills/graph ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSkillGraph_MissingPromptParam_Returns400()
    {
        var resp = await _client.GetAsync("/api/skills/graph");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSkillGraph_MalformedPromptParam_Returns400()
    {
        // Missing "/" separator
        var resp = await _client.GetAsync("/api/skills/graph?prompt=nodomain");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSkillGraph_UnknownPrompt_Returns404()
    {
        var resp = await _client.GetAsync("/api/skills/graph?prompt=ghost/no-such-prompt");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetSkillGraph_KnownPrompt_ReturnsNodesAndEdges()
    {
        var resp = await _client.GetAsync("/api/skills/graph?prompt=test/skill-graph-prompt");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.TryGetProperty("nodes", out var nodes), "missing 'nodes'");
        Assert.True(json.TryGetProperty("edges", out var edges), "missing 'edges'");
        Assert.True(json.TryGetProperty("promptName",   out _),  "missing 'promptName'");
        Assert.True(json.TryGetProperty("promptDomain", out _),  "missing 'promptDomain'");
        Assert.True(nodes.GetArrayLength() > 0, "expected at least one node");
    }

    [Fact]
    public async Task GetSkillGraph_KnownPrompt_NodeHasRequiredShape()
    {
        var resp  = await _client.GetAsync("/api/skills/graph?prompt=test/skill-graph-prompt");
        var json  = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var first = json.GetProperty("nodes").EnumerateArray().First();

        Assert.True(first.TryGetProperty("id",       out _), "node missing 'id'");
        Assert.True(first.TryGetProperty("label",    out _), "node missing 'label'");
        Assert.True(first.TryGetProperty("type",     out _), "node missing 'type'");
        Assert.True(first.TryGetProperty("skillRef", out _), "node missing 'skillRef'");
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Test factory that wires up an isolated skills directory and an isolated prompts
    /// directory — each containing exactly the YAMLs needed for these tests.
    /// All background services (pipe server, scheduler, Ollama monitor) are disabled.
    /// </summary>
    public sealed class TestFactory : WebApplicationFactory<Program>, IDisposable
    {
        private readonly string _dbPath;
        private readonly string _promptsDir;
        private readonly string _skillsDir;

        public TestFactory()
        {
            var uid     = Guid.NewGuid().ToString("N");
            _dbPath     = Path.Combine(Path.GetTempPath(), $"sagide-skills-api-test-{uid}.db");
            _promptsDir = Path.Combine(Path.GetTempPath(), $"sagide-skills-prompts-{uid}");
            _skillsDir  = Path.Combine(Path.GetTempPath(), $"sagide-skills-lib-{uid}");

            Directory.CreateDirectory(_promptsDir);
            Directory.CreateDirectory(_skillsDir);

            WriteSkills();
            WritePrompts();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Add(new MemoryConfigurationSource
                {
                    InitialData = new Dictionary<string, string?>
                    {
                        ["SAGIDE:Database:Path"]            = _dbPath,
                        ["SAGIDE:PromptsPath"]               = _promptsDir,
                        ["SAGIDE:SkillsPath"]                = _skillsDir,
                        ["SAGIDE:Scheduler:Enabled"]         = "false",
                        ["SAGIDE:Ollama:Servers:0:BaseUrl"]  = null,
                    }
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // Remove all background hosted services so the test host starts quickly
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
                try { if (File.Exists(_dbPath))         File.Delete(_dbPath);         } catch { }
                try { if (Directory.Exists(_promptsDir)) Directory.Delete(_promptsDir, true); } catch { }
                try { if (Directory.Exists(_skillsDir))  Directory.Delete(_skillsDir,  true); } catch { }
            }
        }

        // ── Seed data ─────────────────────────────────────────────────────────

        private void WriteSkills()
        {
            WriteFile(Path.Combine(_skillsDir, "test", "test-collector.yaml"), """
                name: test-collector
                domain: test
                version: 1
                protocol_implements:
                  - Collectible
                parameters:
                  topic: ""
                  num_queries: "3"
                implementation:
                  - type: web_search_batch
                    output_var: search_results
                """);

            WriteFile(Path.Combine(_skillsDir, "test", "test-analyzer.yaml"), """
                name: test-analyzer
                domain: test
                version: 1
                protocol_implements:
                  - Analyzable
                capability_requirements:
                  deep_analyst:
                    needs:
                      - deep_reasoning
                implementation:
                  - type: llm_per_section
                    section_analysis_prompt: "Analyze the evidence."
                    output_var: analysis
                """);
        }

        private void WritePrompts()
        {
            // A simple prompt with data_collection steps so the graph endpoint has something to show
            WriteFile(Path.Combine(_promptsDir, "test", "skill-graph-prompt.yaml"), """
                name: skill-graph-prompt
                domain: test
                version: 1
                description: "Test prompt for skill graph endpoint"
                model_preference:
                  primary: "ollama/test-model"
                data_collection:
                  steps:
                    - name: collect_data
                      type: skill
                      skill: test/test-collector
                      output_var: collected_results
                      parameters:
                        topic: "test topic"
                subtasks:
                  - name: analyze_data
                    model: "capability:deep_analyst"
                    input_vars:
                      - collected_results
                    depends_on: []
                    prompt_template: "Analyze: {{collected_results}}"
                synthesis:
                  prompt_template: "{{analyze_data_result}}"
                """);
        }

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
