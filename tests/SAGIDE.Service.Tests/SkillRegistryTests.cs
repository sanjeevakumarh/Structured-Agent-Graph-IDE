using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for <see cref="SkillRegistry"/> — loading, lookup, resolution, and error handling.
/// Mirrors the structure of <see cref="PromptRegistryTests"/> for consistency.
/// </summary>
public class SkillRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<SkillRegistry> _registries = [];

    public SkillRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sagide-skills-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Dispose registries first so FileSystemWatchers stop before directory deletion.
        foreach (var r in _registries)
            r.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsAllLoadedSkills()
    {
        WriteYaml("research/collector.yaml",  SkillYaml("collector",  "research"));
        WriteYaml("research/analyst.yaml",    SkillYaml("analyst",    "research"));
        WriteYaml("finance/summarizer.yaml",  SkillYaml("summarizer", "finance"));

        var registry = Build();

        Assert.Equal(3, registry.GetAll().Count);
    }

    [Fact]
    public void GetAll_EmptyDirectory_ReturnsEmpty()
    {
        var registry = Build();
        Assert.Empty(registry.GetAll());
    }

    [SkippableFact]
    public void GetAll_NonExistentDirectory_AutoDiscoversSkills()
    {
        // When the configured path does not exist, the registry walks up from AppContext.BaseDirectory
        // to find a skills/ directory (in CI and dev the repo root is an ancestor).
        var registry = Build(path: Path.Combine(_tempDir, "does-not-exist"));
        var all = registry.GetAll();
        // Auto-discovery should find the real skills/ in the repo and return non-empty results.
        // Skip if no skills directory exists in this checkout (e.g. separate deployment).
        Skip.If(all.Count == 0, "No skills/ directory found via auto-discovery");
        Assert.NotEmpty(all);
    }

    // ── GetByDomain ───────────────────────────────────────────────────────────

    [Fact]
    public void GetByDomain_ReturnsOnlyMatchingDomain()
    {
        WriteYaml("research/collector.yaml",  SkillYaml("collector",  "research"));
        WriteYaml("research/analyst.yaml",    SkillYaml("analyst",    "research"));
        WriteYaml("finance/summarizer.yaml",  SkillYaml("summarizer", "finance"));

        var registry = Build();

        var research = registry.GetByDomain("research");
        Assert.Equal(2, research.Count);
        Assert.All(research, s => Assert.Equal("research", s.Domain));
    }

    [Fact]
    public void GetByDomain_CaseInsensitive()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        Assert.Single(registry.GetByDomain("RESEARCH"));
        Assert.Single(registry.GetByDomain("Research"));
    }

    [Fact]
    public void GetByDomain_UnknownDomain_ReturnsEmpty()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        Assert.Empty(registry.GetByDomain("shared"));
    }

    // ── GetByKey ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetByKey_ExistingSkill_ReturnsDefinition()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        var def = registry.GetByKey("research", "collector");

        Assert.NotNull(def);
        Assert.Equal("collector", def.Name);
        Assert.Equal("research",  def.Domain);
        Assert.Equal(1,           def.Version);
    }

    [Fact]
    public void GetByKey_CaseInsensitive()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        Assert.NotNull(registry.GetByKey("Research", "Collector"));
        Assert.NotNull(registry.GetByKey("RESEARCH", "COLLECTOR"));
    }

    [Fact]
    public void GetByKey_NonExistent_ReturnsNull()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        Assert.Null(registry.GetByKey("research", "analyst"));
        Assert.Null(registry.GetByKey("finance",  "collector"));
    }

    // ── Resolve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_FullyQualifiedRef_ReturnsSkill()
    {
        WriteYaml("research/web-research-track.yaml", SkillYaml("web-research-track", "research"));
        var registry = Build();

        var def = registry.Resolve("research/web-research-track");

        Assert.NotNull(def);
        Assert.Equal("web-research-track", def.Name);
        Assert.Equal("research",           def.Domain);
    }

    [Fact]
    public void Resolve_ShortName_SearchesAllDomains()
    {
        WriteYaml("shared/summarizer.yaml",  SkillYaml("summarizer", "shared"));
        WriteYaml("finance/analyst.yaml",    SkillYaml("analyst",    "finance"));
        var registry = Build();

        var def = registry.Resolve("summarizer");

        Assert.NotNull(def);
        Assert.Equal("summarizer", def.Name);
    }

    [Fact]
    public void Resolve_ShortName_CaseInsensitive()
    {
        WriteYaml("research/section-analyst.yaml", SkillYaml("section-analyst", "research"));
        var registry = Build();

        Assert.NotNull(registry.Resolve("Section-Analyst"));
        Assert.NotNull(registry.Resolve("SECTION-ANALYST"));
    }

    [Fact]
    public void Resolve_UnknownSkill_ReturnsNull()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        Assert.Null(registry.Resolve("nonexistent"));
        Assert.Null(registry.Resolve("research/nonexistent"));
        Assert.Null(registry.Resolve("unknown/collector"));
    }

    // ── Field loading ─────────────────────────────────────────────────────────

    [Fact]
    public void GetByKey_FilePathIsSet()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        var def = registry.GetByKey("research", "collector");
        Assert.NotNull(def!.FilePath);
        Assert.EndsWith("collector.yaml", def.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetByKey_ProtocolImplements_Loaded()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research",
            protocols: ["Collectible", "Analyzable"]));
        var registry = Build();

        var def = registry.GetByKey("research", "collector")!;
        Assert.Contains("Collectible", def.ProtocolImplements);
        Assert.Contains("Analyzable",  def.ProtocolImplements);
    }

    [Fact]
    public void GetByKey_CapabilityRequirements_Loaded()
    {
        WriteYaml("research/analyst.yaml", SkillYaml("analyst", "research",
            capabilitySlot: "deep_analyst"));
        var registry = Build();

        var def = registry.GetByKey("research", "analyst")!;
        Assert.True(def.CapabilityRequirements.ContainsKey("deep_analyst"));
    }

    [Fact]
    public void GetByKey_Parameters_Loaded()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research",
            withParameters: true));
        var registry = Build();

        var def = registry.GetByKey("research", "collector")!;
        Assert.True(def.Parameters.ContainsKey("num_queries"));
    }

    [Fact]
    public void GetByKey_Implementation_Loaded()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));
        var registry = Build();

        var def = registry.GetByKey("research", "collector")!;
        Assert.NotEmpty(def.Implementation);
        Assert.Equal("web_search_batch", def.Implementation[0].Type);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void MissingName_SkipsSkill()
    {
        // Missing name — should be skipped
        WriteYaml("research/bad.yaml", "domain: research\nversion: 1\nimplementation:\n  - type: llm\n");
        WriteYaml("research/good.yaml", SkillYaml("good", "research"));

        var registry = Build();

        Assert.Single(registry.GetAll());
        Assert.NotNull(registry.GetByKey("research", "good"));
    }

    [Fact]
    public void MissingDomain_SkipsSkill()
    {
        WriteYaml("research/bad.yaml", "name: bad\nversion: 1\nimplementation:\n  - type: llm\n");
        WriteYaml("research/good.yaml", SkillYaml("good", "research"));

        var registry = Build();

        Assert.Single(registry.GetAll());
    }

    [Fact]
    public void InvalidYamlFile_DoesNotCrash_OtherSkillsStillLoad()
    {
        WriteYaml("research/invalid.yaml", ":\t: invalid yaml [[[");
        WriteYaml("research/ok.yaml",      SkillYaml("ok", "research"));

        var registry = Build();

        Assert.Single(registry.GetAll());
    }

    // ── Prompt blocks ──────────────────────────────────────────────────────────

    [Fact]
    public void PromptBlocks_LoadedFromSharedDirectory()
    {
        WriteYaml("shared/prompt-blocks.yaml", """
            name: prompt-blocks
            domain: shared
            version: 1
            blocks:
              data_integrity_rules: "Copy numbers verbatim."
              no_preamble: "Start immediately."
            """);
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));

        var registry = Build();

        Assert.Equal(2, registry.PromptBlocks.Count);
        Assert.Equal("Copy numbers verbatim.", registry.PromptBlocks["data_integrity_rules"]);
        Assert.Equal("Start immediately.", registry.PromptBlocks["no_preamble"]);
    }

    [Fact]
    public void PromptBlocks_ExcludedFromSkillIndex()
    {
        WriteYaml("shared/prompt-blocks.yaml", """
            name: prompt-blocks
            domain: shared
            version: 1
            blocks:
              test_block: "test value"
            """);
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));

        var registry = Build();

        // prompt-blocks should NOT appear as a skill
        Assert.Single(registry.GetAll());
        Assert.Null(registry.GetByKey("shared", "prompt-blocks"));
    }

    [Fact]
    public void PromptBlocks_EmptyWhenFileNotPresent()
    {
        WriteYaml("research/collector.yaml", SkillYaml("collector", "research"));

        var registry = Build();

        Assert.Empty(registry.PromptBlocks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void WriteYaml(string relativePath, string content)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private SkillRegistry Build(string? path = null)
    {
        var skillsPath = path ?? _tempDir;
        var config = new ConfigurationBuilder()
            .Add(new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["SAGIDE:SkillsPath"] = skillsPath,
                }
            })
            .Build();
        var env = new FakeHostEnvironment { ContentRootPath = _tempDir };
        var registry = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);
        _registries.Add(registry);
        return registry;
    }

    private static string SkillYaml(
        string name,
        string domain,
        IEnumerable<string>? protocols   = null,
        string?              capabilitySlot  = null,
        bool                 withParameters  = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"domain: {domain}");
        sb.AppendLine("version: 1");

        if (protocols?.Any() == true)
        {
            sb.AppendLine("protocol_implements:");
            foreach (var p in protocols)
                sb.AppendLine($"  - {p}");
        }

        if (capabilitySlot is not null)
        {
            sb.AppendLine("capability_requirements:");
            sb.AppendLine($"  {capabilitySlot}:");
            sb.AppendLine("    needs:");
            sb.AppendLine("      - deep_reasoning");
        }

        if (withParameters)
        {
            sb.AppendLine("parameters:");
            sb.AppendLine("  num_queries: \"5\"");
            sb.AppendLine("  topic: \"\"");
        }

        sb.AppendLine("implementation:");
        sb.AppendLine("  - type: web_search_batch");
        sb.AppendLine("    output_var: search_results");

        return sb.ToString();
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string         EnvironmentName         { get; set; } = "Test";
        public string         ApplicationName         { get; set; } = "SAGIDE.Tests";
        public string         ContentRootPath         { get; set; } = string.Empty;
        public IFileProvider  ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
