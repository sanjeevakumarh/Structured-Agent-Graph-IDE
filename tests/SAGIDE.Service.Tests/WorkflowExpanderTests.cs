using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="WorkflowExpander.Expand"/>.
///
/// Strategy: build a <see cref="SkillRegistry"/> backed by test YAML files in a temp directory,
/// construct <see cref="PromptDefinition"/>s with <c>objects:</c> + <c>workflow:</c> sections,
/// call <c>Expand</c>, then assert the resulting flat step / subtask lists.
/// </summary>
public class WorkflowExpanderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<SkillRegistry> _registries = [];

    public WorkflowExpanderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sagide-expander-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Write two reusable test skills upfront
        WriteSkill("test/collector.yaml",  CollectorYaml);
        WriteSkill("test/analyzer.yaml",   AnalyzerYaml);
    }

    public void Dispose()
    {
        foreach (var r in _registries)
            r.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── No-op guard ───────────────────────────────────────────────────────────

    [Fact]
    public void Expand_EmptyObjectsAndWorkflow_IsNoOp()
    {
        var prompt = new PromptDefinition { Name = "test", Domain = "test" };
        var registry = BuildRegistry();

        WorkflowExpander.Expand(prompt, registry, NullLogger.Instance);

        Assert.Null(prompt.DataCollection);
        Assert.Empty(prompt.Subtasks);
    }

    [Fact]
    public void Expand_EmptyObjectsAndWorkflow_ExistingStepsPreserved()
    {
        var prompt = new PromptDefinition
        {
            Name   = "test",
            Domain = "test",
            DataCollection = new PromptDataCollection
            {
                Steps = [new PromptDataCollectionStep { Name = "existing", Type = "read_file" }]
            }
        };
        var registry = BuildRegistry();

        WorkflowExpander.Expand(prompt, registry, NullLogger.Instance);

        Assert.Single(prompt.DataCollection!.Steps);
        Assert.Equal("existing", prompt.DataCollection.Steps[0].Name);
    }

    // ── Collect method → data_collection step ─────────────────────────────────

    [Fact]
    public void Expand_CollectCall_ProducesDataCollectionStep()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("src.collect")]
        );
        var registry = BuildRegistry();

        WorkflowExpander.Expand(prompt, registry, NullLogger.Instance);

        Assert.Single(prompt.DataCollection!.Steps);
        var step = prompt.DataCollection.Steps[0];
        Assert.Equal("src.collect",   step.Name);
        Assert.Equal("skill",         step.Type);
        Assert.Equal("test/collector", step.Skill);
    }

    [Fact]
    public void Expand_SearchCall_IsAlsoMappedToDataCollectionStep()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("src.search")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);
        Assert.Single(prompt.DataCollection!.Steps);
    }

    [Fact]
    public void Expand_FetchCall_IsAlsoMappedToDataCollectionStep()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("src.fetch")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);
        Assert.Single(prompt.DataCollection!.Steps);
    }

    [Fact]
    public void Expand_CollectCall_DefaultOutputVar_IsObjectName_Results()
    {
        var prompt = MakePrompt(
            objects:  [Obj("market", "test/collector")],
            workflow: [Call("market.collect")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        var step = prompt.DataCollection!.Steps[0];
        Assert.Equal("market_results", step.OutputVar);
    }

    [Fact]
    public void Expand_CollectCall_OutputVarOverriddenByMergedArgs()
    {
        var prompt = MakePrompt(
            objects:  [Obj("market", "test/collector", args: new() { ["output_var"] = "market_data" })],
            workflow: [Call("market.collect")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal("market_data", prompt.DataCollection!.Steps[0].OutputVar);
    }

    // ── Analyze method → subtask ───────────────────────────────────────────────

    [Fact]
    public void Expand_AnalyzeCall_ProducesSubtask()
    {
        var prompt = MakePrompt(
            objects:  [Obj("analyst", "test/analyzer")],
            workflow: [Call("analyst.analyze")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Single(prompt.Subtasks);
        var subtask = prompt.Subtasks[0];
        Assert.Equal("analyst.analyze", subtask.Name);
    }

    [Fact]
    public void Expand_ValidateCall_IsAlsoMappedToSubtask()
    {
        var prompt = MakePrompt(
            objects:  [Obj("qa", "test/analyzer")],
            workflow: [Call("qa.validate")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);
        Assert.Single(prompt.Subtasks);
    }

    [Fact]
    public void Expand_AssembleCall_IsAlsoMappedToSubtask()
    {
        var prompt = MakePrompt(
            objects:  [Obj("assembler", "test/analyzer")],
            workflow: [Call("assembler.assemble")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);
        Assert.Single(prompt.Subtasks);
    }

    [Fact]
    public void Expand_AnalyzeCall_ModelSpec_UsesCapabilityFromSkill()
    {
        var prompt = MakePrompt(
            objects:  [Obj("analyst", "test/analyzer")],
            workflow: [Call("analyst.analyze")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        // Analyzer skill declares capability_requirements: analyst: needs: [deep_reasoning]
        // Model spec should use the slot KEY ("analyst"), not the needs list — resolves via config.
        var subtask = prompt.Subtasks[0];
        Assert.Equal("capability:analyst", subtask.Model, StringComparer.OrdinalIgnoreCase);
    }

    // ── Parallel block ────────────────────────────────────────────────────────

    [Fact]
    public void Expand_ParallelBlock_ExpandsAllCallsToDataSteps()
    {
        var prompt = MakePrompt(
            objects: [Obj("market", "test/collector"), Obj("tech", "test/collector")],
            workflow: [Parallel("market.collect", "tech.collect")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal(2, prompt.DataCollection!.Steps.Count);
        var names = prompt.DataCollection.Steps.Select(s => s.Name).ToList();
        Assert.Contains("market.collect", names);
        Assert.Contains("tech.collect",   names);
    }

    [Fact]
    public void Expand_ParallelBlock_MixedCollectAndAnalyze_SeparatedCorrectly()
    {
        var prompt = MakePrompt(
            objects: [Obj("src", "test/collector"), Obj("analyst", "test/analyzer")],
            workflow: [Parallel("src.collect", "analyst.analyze")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Single(prompt.DataCollection!.Steps);
        Assert.Single(prompt.Subtasks);
    }

    // ── Arg merging ───────────────────────────────────────────────────────────

    [Fact]
    public void Expand_Args_ObjectArgOverridesSkillDefault()
    {
        // Skill has param: num_queries = "5"; object overrides to "10"
        WriteSkill("test/parameterized.yaml", """
            name: parameterized
            domain: test
            version: 1
            parameters:
              num_queries: "5"
              topic: ""
            implementation:
              - type: web_search_batch
                output_var: results
            """);

        var prompt = MakePrompt(
            objects:  [Obj("src", "test/parameterized", args: new() { ["num_queries"] = "10" })],
            workflow: [Call("src.collect")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        var step = prompt.DataCollection!.Steps[0];
        Assert.Equal("10", step.Parameters["num_queries"].ToString());
    }

    [Fact]
    public void Expand_Args_CallArgOverridesObjectArg()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector", args: new() { ["topic"] = "market" })],
            workflow: [Call("src.collect", args: new() { ["topic"] = "technology" })]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        var step = prompt.DataCollection!.Steps[0];
        Assert.Equal("technology", step.Parameters["topic"].ToString());
    }

    // ── Error cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Expand_UnknownObject_SkipsCall_NoDataStep()
    {
        var prompt = MakePrompt(
            objects:  [Obj("known", "test/collector")],
            workflow: [Call("unknown.collect")]  // "unknown" not in objects
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Empty(prompt.DataCollection?.Steps ?? []);
        Assert.Empty(prompt.Subtasks);
    }

    [Fact]
    public void Expand_InvalidCallFormat_NoObjectMethod_Skipped()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("no-dot-notation")]  // missing "." separator
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Empty(prompt.DataCollection?.Steps ?? []);
    }

    [Fact]
    public void Expand_UnrecognizedMethod_Skipped()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("src.unknownmethod")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Empty(prompt.DataCollection?.Steps ?? []);
        Assert.Empty(prompt.Subtasks);
    }

    // ── Sequential depends_on chaining ────────────────────────────────────────

    [Fact]
    public void Expand_SequentialAnalyzeCalls_SecondDependsOnFirst()
    {
        var prompt = MakePrompt(
            objects:  [Obj("qa", "test/analyzer"), Obj("assembler", "test/analyzer")],
            workflow: [Call("qa.validate"), Call("assembler.assemble")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal(2, prompt.Subtasks.Count);
        var qa        = prompt.Subtasks.First(s => s.Name == "qa.validate");
        var assembler = prompt.Subtasks.First(s => s.Name == "assembler.assemble");

        Assert.Empty(qa.DependsOn);
        Assert.Contains("qa.validate", assembler.DependsOn);
    }

    [Fact]
    public void Expand_ThreeSequentialAnalyzeCalls_ChainedDependsOn()
    {
        var prompt = MakePrompt(
            objects: [Obj("qa", "test/analyzer"), Obj("assembler", "test/analyzer"), Obj("evaluator", "test/analyzer")],
            workflow: [Call("qa.validate"), Call("assembler.assemble"), Call("evaluator.evaluate")]
        );
        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal(3, prompt.Subtasks.Count);
        var qa        = prompt.Subtasks.First(s => s.Name == "qa.validate");
        var assembler = prompt.Subtasks.First(s => s.Name == "assembler.assemble");
        var evaluator = prompt.Subtasks.First(s => s.Name == "evaluator.evaluate");

        Assert.Empty(qa.DependsOn);
        Assert.Contains("qa.validate",       assembler.DependsOn);
        Assert.Contains("assembler.assemble", evaluator.DependsOn);
        Assert.DoesNotContain("qa.validate",  evaluator.DependsOn);
    }

    // ── Prepend to existing steps ─────────────────────────────────────────────

    [Fact]
    public void Expand_PrependedBeforeExistingDataSteps()
    {
        var prompt = MakePrompt(
            objects:  [Obj("src", "test/collector")],
            workflow: [Call("src.collect")]
        );
        // Pre-existing step
        prompt.DataCollection = new PromptDataCollection
        {
            Steps = [new PromptDataCollectionStep { Name = "existing", Type = "read_file" }]
        };

        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal(2, prompt.DataCollection!.Steps.Count);
        Assert.Equal("src.collect", prompt.DataCollection.Steps[0].Name);
        Assert.Equal("existing",    prompt.DataCollection.Steps[1].Name);
    }

    [Fact]
    public void Expand_PrependedBeforeExistingSubtasks()
    {
        var prompt = MakePrompt(
            objects:  [Obj("analyst", "test/analyzer")],
            workflow: [Call("analyst.analyze")]
        );
        prompt.Subtasks.Add(new PromptSubtask { Name = "existing-subtask" });

        WorkflowExpander.Expand(prompt, BuildRegistry(), NullLogger.Instance);

        Assert.Equal(2, prompt.Subtasks.Count);
        Assert.Equal("analyst.analyze", prompt.Subtasks[0].Name);
        Assert.Equal("existing-subtask", prompt.Subtasks[1].Name);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PromptDefinition MakePrompt(
        List<PromptObject>       objects,
        List<PromptWorkflowCall> workflow)
        => new()
        {
            Name     = "test",
            Domain   = "test",
            Objects  = objects,
            Workflow = workflow,
        };

    private static PromptObject Obj(
        string name,
        string skill,
        Dictionary<string, object>? args = null)
        => new() { Name = name, Skill = skill, Args = args ?? [] };

    private static PromptWorkflowCall Call(string call, Dictionary<string, object>? args = null)
        => new() { Call = call, Args = args ?? [] };

    private static PromptWorkflowCall Parallel(params string[] calls)
        => new() { Parallel = [.. calls] };

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

    // ── Fixture skill YAMLs ───────────────────────────────────────────────────

    private const string CollectorYaml = """
        name: collector
        domain: test
        version: 1
        protocol_implements:
          - Collectible
        parameters:
          topic: ""
          num_queries: "5"
        implementation:
          - type: web_search_batch
            output_var: search_results
        """;

    private const string AnalyzerYaml = """
        name: analyzer
        domain: test
        version: 1
        protocol_implements:
          - Analyzable
        capability_requirements:
          analyst:
            needs:
              - deep_reasoning
        implementation:
          - type: llm_per_section
            section_analysis_prompt: "Analyze the evidence."
            output_var: analysis
        """;

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string        EnvironmentName         { get; set; } = "Test";
        public string        ApplicationName         { get; set; } = "SAGIDE.Tests";
        public string        ContentRootPath         { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
