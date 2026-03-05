using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Rag;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="SubtaskCoordinator.ExpandSkillRefs"/> — specifically the direct
/// parameter override for SectionAnalysisPrompt and PlanningPrompt, parameter merging,
/// and empty-override skipping.
/// </summary>
public class SubtaskCoordinatorSkillExpansionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<SkillRegistry> _registries = [];

    public SubtaskCoordinatorSkillExpansionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sagide-expand-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var r in _registries) r.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private SubtaskCoordinator MakeCoordinator(SkillRegistry registry)
    {
        var http    = new HttpClient();
        var fetcher = new WebFetcher(http, NullLogger<WebFetcher>.Instance,
                          rateLimitDelay: TimeSpan.Zero, cacheTtl: TimeSpan.FromHours(1));
        var config  = new ConfigurationBuilder().Build();
        var search  = new WebSearchAdapter(http, config, NullLogger<WebSearchAdapter>.Instance);

        return new SubtaskCoordinator(
            null!,
            fetcher,
            search,
            config,
            NullLogger<SubtaskCoordinator>.Instance,
            skillRegistry: registry);
    }

    private void WriteSkill(string relativePath, string content)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private SkillRegistry BuildRegistry()
    {
        var config = new ConfigurationBuilder()
            .Add(new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["SAGIDE:SkillsPath"] = _tempDir,
                }
            })
            .Build();
        var env = new FakeHostEnvironment { ContentRootPath = _tempDir };
        var registry = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);
        _registries.Add(registry);
        return registry;
    }

    // ── SectionAnalysisPrompt direct override ───────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_SectionAnalysisPromptWithControlFlow_PreservedDirectlyOnClone()
    {
        WriteSkill("test/analyst.yaml", """
            name: analyst
            domain: test
            version: 1
            parameters:
              section_analysis_prompt: "default prompt"
              max_sections: "6"
            implementation:
              - name: analyze
                type: llm_per_section
                output_var: analysis_results
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        var callerOverride = """{{ if section_name == "Overview" }}Write overview{{ else }}Write details{{ end }}""";

        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "run_analyses",
                Skill = "test/analyst",
                Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["section_analysis_prompt"] = callerOverride,
                },
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        // The clone should have the caller's template set DIRECTLY — not via {{parameters.X}}
        Assert.Equal(callerOverride, expanded[0].SectionAnalysisPrompt);
    }

    // ── PlanningPrompt direct override ──────────────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_PlanningPromptWithControlFlow_PreservedDirectlyOnClone()
    {
        WriteSkill("test/planner.yaml", """
            name: planner
            domain: test
            version: 1
            parameters:
              planning_prompt: "default planning"
            implementation:
              - name: plan
                type: llm_queries
                output_var: plan_results
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        var callerOverride = """{{ if context == "deep" }}Deep plan{{ else }}Quick plan{{ end }}""";

        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "plan_step",
                Skill = "test/planner",
                Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["planning_prompt"] = callerOverride,
                },
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        Assert.Equal(callerOverride, expanded[0].PlanningPrompt);
    }

    // ── Empty override should NOT overwrite ─────────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_EmptyOverride_SkipsDirectSet()
    {
        WriteSkill("test/analyst2.yaml", """
            name: analyst2
            domain: test
            version: 1
            parameters:
              section_analysis_prompt: "skill default prompt"
            implementation:
              - name: analyze
                type: llm_per_section
                section_analysis_prompt: "from implementation"
                output_var: results
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        // Caller passes empty string — should NOT overwrite the skill's implementation value
        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "run",
                Skill = "test/analyst2",
                Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["section_analysis_prompt"] = "",
                },
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        // The clone's SectionAnalysisPrompt should come from the implementation, not be overwritten with ""
        Assert.Equal("from implementation", expanded[0].SectionAnalysisPrompt);
    }

    // ── Parameters merged onto clone ────────────────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_Parameters_MergedOntoClone()
    {
        WriteSkill("test/merger.yaml", """
            name: merger
            domain: test
            version: 1
            parameters:
              ticker: ""
              max_results: "5"
              context: "default context"
            implementation:
              - name: search
                type: llm_queries
                output_var: search_results
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "data_step",
                Skill = "test/merger",
                Parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ticker"] = "AAPL",
                    ["max_results"] = "10",
                    // "context" intentionally not overridden — should keep skill default
                },
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        var clone = expanded[0];
        Assert.Equal("AAPL", clone.Parameters["ticker"].ToString());
        Assert.Equal("10", clone.Parameters["max_results"].ToString());
        Assert.Equal("default context", clone.Parameters["context"].ToString());
    }

    // ── SectionTitle preserved on clone ──────────────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_SectionTitle_PreservedOnClone()
    {
        WriteSkill("test/single.yaml", """
            name: single
            domain: test
            version: 1
            parameters:
              output_var: result
            implementation:
              - name: analyze
                type: llm_per_section
                max_sections: "1"
                section_title: "Technical Analysis"
                output_var: "{{parameters.output_var}}"
                section_analysis_prompt: "Analyze the data."
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "run",
                Skill = "test/single",
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        Assert.Equal("Technical Analysis", expanded[0].SectionTitle);
        Assert.Equal("1", expanded[0].MaxSections);
    }

    // ── Prompt blocks injected into skill vars ────────────────────────────────

    [Fact]
    public void ExpandSkillRefs_PromptBlocks_AvailableInRenderedFields()
    {
        WriteSkill("shared/prompt-blocks.yaml", """
            name: prompt-blocks
            domain: shared
            version: 1
            blocks:
              test_rule: "RULE: Copy numbers verbatim."
            """);
        WriteSkill("test/blocker.yaml", """
            name: blocker
            domain: test
            version: 1
            parameters:
              output_var: result
            implementation:
              - name: analyze
                type: llm_per_section
                max_sections: "1"
                section_title: "Analysis"
                output_var: "{{parameters.output_var}}"
                section_analysis_prompt: "Instructions: {{blocks.test_rule}}"
            """);

        var registry = BuildRegistry();
        var coordinator = MakeCoordinator(registry);

        var steps = new List<PromptDataCollectionStep>
        {
            new()
            {
                Name = "run",
                Skill = "test/blocker",
            }
        };
        var vars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var expanded = coordinator.ExpandSkillRefs(steps, vars);

        Assert.Single(expanded);
        // blocks are NOT resolved at expansion time — section_analysis_prompt is pass-through
        // It will be resolved at execution time when blocks is in scope.
        // So the expanded clone should contain the raw template reference.
        Assert.Contains("blocks.test_rule", expanded[0].SectionAnalysisPrompt);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string        EnvironmentName         { get; set; } = "Test";
        public string        ApplicationName         { get; set; } = "SAGIDE.Tests";
        public string        ContentRootPath         { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
