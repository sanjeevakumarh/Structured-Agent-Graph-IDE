using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integration tests that load the real migrated prompt YAMLs from the repository,
/// expand them through <see cref="WorkflowExpander"/> with the real <see cref="SkillRegistry"/>,
/// and assert that the resulting flat step/subtask lists are structurally correct.
///
/// Each test is a "dry run" that verifies:
/// - The correct number of data_collection steps (workflow-injected + pre-existing)
/// - The correct number of subtasks (workflow-injected + pre-existing)
/// - Output variable names match downstream references
/// - DependsOn chains are correct for sequential subtask pipelines
/// </summary>
public class PromptWorkflowIntegrationTests : IDisposable
{
    private readonly SkillRegistry _skills;
    private readonly IDeserializer _yaml;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (.git not found).");
    }

    public PromptWorkflowIntegrationTests()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        var config = new ConfigurationBuilder()
            .Add(new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["SAGIDE:SkillsPath"] = skillsDir,
                }
            })
            .Build();
        var env = new FakeHostEnvironment { ContentRootPath = skillsDir };
        _skills = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);

        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public void Dispose() => _skills.Dispose();

    // ── idea-to-product-seq ───────────────────────────────────────────────────
    // Objects: market, tech, finance, risk (web-research-track) + evidence_normalizer + qa + assembler + evaluator
    // Workflow: parallel[4×search] → evidence_normalizer.collect → qa.validate → assembler.assemble → evaluator.evaluate
    // Pre-existing data_collection: load_context + run_analyses (2 steps)
    // Expected after Expand: 5 workflow steps + 2 explicit = 7 data steps, 3 subtasks (sequential chain)

    [SkippableFact]
    public void IdeaToProductSeq_Expand_HasSixDataSteps()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        Assert.Equal(7, prompt.DataCollection!.Steps.Count);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_FourWorkflowStepsPrependedFirst()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var steps = prompt.DataCollection!.Steps;
        // First 4: workflow-injected search tracks (parallel block)
        Assert.Equal("market.search",  steps[0].Name);
        Assert.Equal("tech.search",    steps[1].Name);
        Assert.Equal("finance.search", steps[2].Name);
        Assert.Equal("risk.search",    steps[3].Name);
        // evidence_normalizer.collect runs after search, before section analysis
        Assert.Equal("evidence_normalizer.collect", steps[4].Name);
        Assert.Equal("skill",                       steps[4].Type);
        Assert.Equal("research/evidence-normalizer", steps[4].Skill);
        // Pre-existing explicit data_collection steps
        Assert.Equal("load_context",  steps[5].Name);
        Assert.Equal("run_analyses",  steps[6].Name);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_WorkflowSearchStepsAreSkillType()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var steps = prompt.DataCollection!.Steps;
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("skill", steps[i].Type);
            Assert.Equal("research/web-research-track", steps[i].Skill);
        }
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_SearchStepOutputVarsMatchSearchTrackNames()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var steps = prompt.DataCollection!.Steps;
        Assert.Equal("market_results",  steps[0].OutputVar);
        Assert.Equal("tech_results",    steps[1].OutputVar);
        Assert.Equal("finance_results", steps[2].OutputVar);
        Assert.Equal("risk_results",    steps[3].OutputVar);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_HasThreeSubtasks()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        Assert.Equal(3, prompt.Subtasks.Count);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_SubtaskNamesAreCorrect()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var names = prompt.Subtasks.Select(s => s.Name).ToList();
        Assert.Contains("qa.validate",         names);
        Assert.Contains("assembler.assemble",  names);
        Assert.Contains("evaluator.evaluate",  names);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_QaSubtask_HasNoDependsOn()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var qa = prompt.Subtasks.First(s => s.Name == "qa.validate");
        Assert.Empty(qa.DependsOn);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_AssemblerSubtask_DependsOnQa()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var assembler = prompt.Subtasks.First(s => s.Name == "assembler.assemble");
        Assert.Contains("qa.validate", assembler.DependsOn);
    }

    [SkippableFact]
    public void IdeaToProductSeq_Expand_EvaluatorSubtask_DependsOnAssembler_NotQa()
    {
        var prompt = LoadPrompt("research", "idea-to-product-seq");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var evaluator = prompt.Subtasks.First(s => s.Name == "evaluator.evaluate");
        Assert.Contains("assembler.assemble", evaluator.DependsOn);
        Assert.DoesNotContain("qa.validate",  evaluator.DependsOn);
    }

    // ── stock-analysis ────────────────────────────────────────────────────────
    // Objects: stock_data (finance/stock-data-track)
    // Workflow: stock_data.collect
    // Pre-existing data_collection: load_personal_context + run_analyses (2 steps)
    // Pre-existing subtasks: assembler (inline)
    // Expected: 1+2=3 data steps, 0 workflow subtasks (inline assembler gives 1 total)

    [SkippableFact]
    public void StockAnalysis_Expand_HasThreeDataSteps()
    {
        var prompt = LoadPrompt("finance", "stock-analysis");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        Assert.Equal(3, prompt.DataCollection!.Steps.Count);
    }

    [SkippableFact]
    public void StockAnalysis_Expand_CollectStepPrependedFirst_WithCorrectOutputVar()
    {
        var prompt = LoadPrompt("finance", "stock-analysis");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        var steps = prompt.DataCollection!.Steps;
        Assert.Equal("stock_data.collect", steps[0].Name);
        Assert.Equal("skill",              steps[0].Type);
        Assert.Equal("finance/stock-data-track", steps[0].Skill);
        Assert.Equal("all_search_results", steps[0].OutputVar);
        // Pre-existing steps follow
        Assert.Equal("load_personal_context", steps[1].Name);
        Assert.Equal("run_analyses",          steps[2].Name);
    }

    [SkippableFact]
    public void StockAnalysis_Expand_WorkflowAddsNoSubtasks_InlineAssemblerPreserved()
    {
        var prompt = LoadPrompt("finance", "stock-analysis");
        WorkflowExpander.Expand(prompt, _skills, NullLogger.Instance);

        Assert.Single(prompt.Subtasks);
        Assert.Equal("assembler", prompt.Subtasks[0].Name);
    }

    // ── Cross-prompt: all skill references resolve ────────────────────────────

    [SkippableFact]
    public void AllMigratedPrompts_AllObjectSkills_ResolveInRegistry()
    {
        var migratedPromptKeys = new[]
        {
            ("research", "idea-to-product-seq"),
            ("finance",  "stock-analysis"),
        };

        Skip.If(_skills.GetAll().Count == 0, "No skill YAMLs present — nothing to resolve against");

        var unresolved = new List<string>();
        foreach (var (domain, name) in migratedPromptKeys)
        {
            var promptPath = Path.Combine(RepoRoot(), "prompts", $"{domain}.{name}.yaml");
            if (!File.Exists(promptPath))
                promptPath = Path.Combine(RepoRoot(), "prompts", domain, $"{name}.yaml");
            if (!File.Exists(promptPath))
                continue; // not present in this checkout — skip entry, continue loop
            var prompt = LoadPrompt(domain, name);
            foreach (var obj in prompt.Objects)
            {
                if (_skills.Resolve(obj.Skill) is null)
                    unresolved.Add($"{domain}/{name} → object '{obj.Name}' → skill '{obj.Skill}'");
            }
        }

        Assert.True(unresolved.Count == 0,
            "Skill references in migrated prompts could not be resolved:\n"
            + string.Join("\n", unresolved));
    }

    [SkippableFact]
    public void AllMigratedPrompts_Parse_WithoutError()
    {
        var keys = new[]
        {
            ("research", "idea-to-product-seq"),
            ("finance",  "stock-analysis"),
        };

        var failures = new List<string>();
        var promptsRoot = Path.Combine(RepoRoot(), "prompts");

        foreach (var (domain, name) in keys)
        {
            var file = Path.Combine(promptsRoot, $"{domain}.{name}.yaml");
            if (!File.Exists(file))
                file = Path.Combine(promptsRoot, domain, $"{name}.yaml");
            if (!File.Exists(file))
                continue; // not present in this checkout — skip entry, continue loop
            try
            {
                var text = File.ReadAllText(file);
                _yaml.Deserialize<PromptDefinition>(text);
            }
            catch (Exception ex)
            {
                failures.Add($"{domain}/{name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "Migrated prompt YAMLs failed to parse:\n" + string.Join("\n", failures));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PromptDefinition LoadPrompt(string domain, string name)
    {
        var path = Path.Combine(RepoRoot(), "prompts", $"{domain}.{name}.yaml");
        if (!File.Exists(path))
            path = Path.Combine(RepoRoot(), "prompts", domain, $"{name}.yaml");
        Skip.If(!File.Exists(path), $"Prompt YAML not present in this checkout: prompts/{domain}.{name}.yaml");
        var text = File.ReadAllText(path);
        return _yaml.Deserialize<PromptDefinition>(text);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string        EnvironmentName         { get; set; } = "Test";
        public string        ApplicationName         { get; set; } = "SAGIDE.Tests";
        public string        ContentRootPath         { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
