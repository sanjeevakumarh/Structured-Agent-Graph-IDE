using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Tests;

public class PromptRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<PromptRegistry> _registries = [];

    public PromptRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sagide-prompts-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Dispose all registries FIRST so their FileSystemWatchers stop before we
        // delete the directory.  If we deleted first the watchers would fire on a
        // background thread and crash the process with DirectoryNotFoundException.
        foreach (var r in _registries)
            r.Dispose();

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── GetAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetAll_ReturnsAllLoadedPrompts()
    {
        WriteYaml("vscode/review.yaml",  YamlFor("review",  "vscode"));
        WriteYaml("finance/daily.yaml",  YamlFor("daily",   "finance"));

        var registry = Build();

        Assert.Equal(2, registry.GetAll().Count);
    }

    [Fact]
    public void GetAll_EmptyDirectory_ReturnsEmpty()
    {
        var registry = Build();
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetAll_NonExistentDirectory_ReturnsEmpty()
    {
        var registry = Build(path: Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(registry.GetAll());
    }

    // ── GetByDomain ───────────────────────────────────────────────────────────

    [Fact]
    public void GetByDomain_ReturnsOnlyMatchingDomain()
    {
        WriteYaml("vscode/review.yaml",    YamlFor("review",    "vscode"));
        WriteYaml("vscode/refactor.yaml",  YamlFor("refactor",  "vscode"));
        WriteYaml("finance/daily.yaml",    YamlFor("daily",     "finance"));

        var registry = Build();

        var vscode = registry.GetByDomain("vscode");
        Assert.Equal(2, vscode.Count);
        Assert.All(vscode, p => Assert.Equal("vscode", p.Domain));
    }

    [Fact]
    public void GetByDomain_CaseInsensitive()
    {
        WriteYaml("vscode/review.yaml", YamlFor("review", "vscode"));
        var registry = Build();

        Assert.Single(registry.GetByDomain("VSCODE"));
        Assert.Single(registry.GetByDomain("Vscode"));
    }

    [Fact]
    public void GetByDomain_UnknownDomain_ReturnsEmpty()
    {
        WriteYaml("vscode/review.yaml", YamlFor("review", "vscode"));
        var registry = Build();

        Assert.Empty(registry.GetByDomain("robotics"));
    }

    // ── GetByKey ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetByKey_ExistingPrompt_ReturnsDefinition()
    {
        WriteYaml("finance/daily.yaml", YamlFor("daily", "finance"));
        var registry = Build();

        var def = registry.GetByKey("finance", "daily");

        Assert.NotNull(def);
        Assert.Equal("daily",   def.Name);
        Assert.Equal("finance", def.Domain);
    }

    [Fact]
    public void GetByKey_CaseInsensitive()
    {
        WriteYaml("finance/daily.yaml", YamlFor("daily", "finance"));
        var registry = Build();

        Assert.NotNull(registry.GetByKey("Finance", "Daily"));
        Assert.NotNull(registry.GetByKey("FINANCE", "DAILY"));
    }

    [Fact]
    public void GetByKey_NonExistent_ReturnsNull()
    {
        WriteYaml("finance/daily.yaml", YamlFor("daily", "finance"));
        var registry = Build();

        Assert.Null(registry.GetByKey("finance", "weekly"));
    }

    // ── GetScheduled ──────────────────────────────────────────────────────────

    [Fact]
    public void GetScheduled_ReturnsOnlyPromptsWithSchedule()
    {
        WriteYaml("finance/daily.yaml",  YamlFor("daily",  "finance", schedule: "0 18 * * 1-5"));
        WriteYaml("finance/manual.yaml", YamlFor("manual", "finance")); // no schedule

        var registry = Build();

        var scheduled = registry.GetScheduled();
        Assert.Single(scheduled);
        Assert.Equal("daily", scheduled[0].Name);
    }

    [Fact]
    public void GetScheduled_NoScheduledPrompts_ReturnsEmpty()
    {
        WriteYaml("vscode/review.yaml", YamlFor("review", "vscode"));
        var registry = Build();

        Assert.Empty(registry.GetScheduled());
    }

    // ── Malformed YAML ────────────────────────────────────────────────────────

    [Fact]
    public void MissingNameOrDomain_SkipsPrompt()
    {
        // Missing name field — should be skipped
        WriteYaml("broken/bad.yaml", "domain: broken\nversion: 1\n");
        // Valid one
        WriteYaml("vscode/ok.yaml", YamlFor("ok", "vscode"));

        var registry = Build();

        Assert.Single(registry.GetAll());
        Assert.NotNull(registry.GetByKey("vscode", "ok"));
    }

    [Fact]
    public void InvalidYamlFile_DoesNotCrash_OtherPromptsStillLoad()
    {
        // Completely invalid YAML
        WriteYaml("broken/invalid.yaml", ":\t: invalid yaml [[[");
        WriteYaml("vscode/ok.yaml", YamlFor("ok", "vscode"));

        var registry = Build();

        Assert.Single(registry.GetAll());
    }

    // ── FilePath set ──────────────────────────────────────────────────────────

    [Fact]
    public void GetByKey_FilePathIsSet()
    {
        WriteYaml("vscode/review.yaml", YamlFor("review", "vscode"));
        var registry = Build();

        var def = registry.GetByKey("vscode", "review");
        Assert.NotNull(def!.FilePath);
        Assert.EndsWith("review.yaml", def.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void WriteYaml(string relativePath, string content)
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private PromptRegistry Build(string? path = null)
    {
        var promptsPath = path ?? _tempDir;

        var config = new ConfigurationBuilder()
            .Add(new MemoryConfigurationSource
            {
                InitialData = new Dictionary<string, string?>
                {
                    ["SAGIDE:PromptsPath"] = promptsPath,
                }
            })
            .Build();

        var env = new FakeHostEnvironment { ContentRootPath = _tempDir };

        var registry = new PromptRegistry(config, env, NullLogger<PromptRegistry>.Instance);
        _registries.Add(registry);
        return registry;
    }

    private static string YamlFor(string name, string domain, string? schedule = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"name: {name}");
        sb.AppendLine($"domain: {domain}");
        sb.AppendLine("version: 1");
        sb.AppendLine("description: Test prompt");
        if (schedule is not null)
            sb.AppendLine($"schedule: \"{schedule}\"");
        return sb.ToString();
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SAGIDE.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
