using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Prompts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Integrity tests that scan the real <c>skills/</c> and <c>protocols/</c> directories
/// in the repository and fail if:
/// <list type="bullet">
///   <item>Any skill YAML cannot be deserialised</item>
///   <item>Any skill is missing required fields (name, domain, version)</item>
///   <item>Any skill has no implementation steps</item>
///   <item>Any skill or protocol YAML uses unapproved machine aliases in @notation</item>
/// </list>
/// These tests act as a guard rail during refactoring: break a skill contract → CI fails.
/// </summary>
public class SkillYamlIntegrityTests
{
    // Approved machine aliases — must match CLAUDE.md and EnvironmentLeakTests
    private static readonly HashSet<string> ApprovedAliases =
    [
        "localhost",
        "workstation",
        "gmini",
        "mini",
        "edge",
        "orin",
    ];

    // Regex for @machine notation in model specs
    private static readonly Regex AtMachine = new(@"@([\w][\w\-]*)", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (.git not found).");
    }

    // ── File-level parsing ────────────────────────────────────────────────────

    [Fact]
    public void AllSkillYamls_ParseWithoutError()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return; // skills dir not created yet — skip

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                deserializer.Deserialize<SkillDefinition>(text);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetRelativePath(skillsDir, file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "Skill YAMLs failed to parse:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AllProtocolYamls_ParseAsDictionary_WithoutError()
    {
        var protocolsDir = Path.Combine(RepoRoot(), "protocols");
        if (!Directory.Exists(protocolsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(protocolsDir, "*.yaml", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(file);
                // Protocols are semi-free-form — just ensure they parse as a dict
                deserializer.Deserialize<Dictionary<string, object>>(text);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetRelativePath(protocolsDir, file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "Protocol YAMLs failed to parse:\n" + string.Join("\n", failures));
    }

    // ── Required fields ───────────────────────────────────────────────────────

    [Fact]
    public void AllSkillYamls_HaveRequiredFields_Name_Domain_Version()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; } // parse failures caught by AllSkillYamls_ParseWithoutError

            var rel = Path.GetRelativePath(skillsDir, file);
            if (string.IsNullOrWhiteSpace(def.Name))    violations.Add($"{rel}: missing 'name'");
            if (string.IsNullOrWhiteSpace(def.Domain))  violations.Add($"{rel}: missing 'domain'");
            if (def.Version < 1)                        violations.Add($"{rel}: 'version' must be >= 1");
        }

        Assert.True(violations.Count == 0,
            "Skill YAMLs have missing required fields:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void AllSkillYamls_HaveAtLeastOneImplementationStep()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; }

            // prompt-blocks.yaml is a block library, not a real skill
            if (def.Name.Equals("prompt-blocks", StringComparison.OrdinalIgnoreCase))
                continue;

            if (def.Implementation.Count == 0)
                violations.Add(Path.GetRelativePath(skillsDir, file));
        }

        Assert.True(violations.Count == 0,
            "Skill YAMLs have no implementation steps:\n" + string.Join("\n", violations));
    }

    // ── Contract: name matches domain from directory path ─────────────────────

    [Fact]
    public void AllSkillYamls_DomainMatchesFilePrefix()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; }

            // Flat layout: domain.name.yaml — domain is the prefix before the first dot
            var fileName = Path.GetFileNameWithoutExtension(file);
            var dotIndex = fileName.IndexOf('.');
            if (dotIndex > 0)
            {
                var filePrefix = fileName[..dotIndex];
                if (!string.Equals(def.Domain, filePrefix, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetRelativePath(skillsDir, file)}: domain='{def.Domain}' but filename prefix='{filePrefix}'");
            }
            else
            {
                // Legacy subdirectory layout: domain/name.yaml
                var dirName = Path.GetFileName(Path.GetDirectoryName(file))!;
                if (!string.Equals(def.Domain, dirName, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetRelativePath(skillsDir, file)}: domain='{def.Domain}' but parent dir='{dirName}'");
            }
        }

        Assert.True(violations.Count == 0,
            "Skill domain field does not match file prefix or parent directory:\n" + string.Join("\n", violations));
    }

    // ── Machine alias policy ──────────────────────────────────────────────────

    [Fact]
    public void SkillYamls_MachineNames_AreApprovedAliases()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in AtMachine.Matches(lines[i]))
                {
                    var alias = m.Groups[1].Value;
                    if (!ApprovedAliases.Contains(alias))
                        violations.Add($"{Path.GetRelativePath(skillsDir, file)}:{i + 1} — unapproved alias '@{alias}'");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Skill YAMLs reference unapproved machine aliases:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ProtocolYamls_MachineNames_AreApprovedAliases()
    {
        var protocolsDir = Path.Combine(RepoRoot(), "protocols");
        if (!Directory.Exists(protocolsDir)) return;

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(protocolsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in AtMachine.Matches(lines[i]))
                {
                    var alias = m.Groups[1].Value;
                    if (!ApprovedAliases.Contains(alias))
                        violations.Add($"{Path.GetRelativePath(protocolsDir, file)}:{i + 1} — unapproved alias '@{alias}'");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Protocol YAMLs reference unapproved machine aliases:\n" + string.Join("\n", violations));
    }

    // ── SkillRegistry loads all real skills successfully ──────────────────────

    [Fact]
    public void SkillRegistry_LoadsAllRealSkills_CountAboveZero()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

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

        using var registry = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);

        Assert.NotEmpty(registry.GetAll());
    }

    [Fact]
    public void SkillRegistry_AllRealSkills_CanBeResolvedByShortName()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

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

        using var registry = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);

        // Every loaded skill should be resolvable by its short name
        var unresolvable = new List<string>();
        foreach (var skill in registry.GetAll())
        {
            if (registry.Resolve(skill.Name) is null)
                unresolvable.Add($"{skill.Domain}/{skill.Name}");
        }

        Assert.True(unresolvable.Count == 0,
            "Skills not resolvable by short name:\n" + string.Join("\n", unresolvable));
    }

    [Fact]
    public void SkillRegistry_AllRealSkills_CanBeResolvedByFullRef()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

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

        using var registry = new SkillRegistry(config, env, NullLogger<SkillRegistry>.Instance);

        var unresolvable = new List<string>();
        foreach (var skill in registry.GetAll())
        {
            if (registry.Resolve($"{skill.Domain}/{skill.Name}") is null)
                unresolvable.Add($"{skill.Domain}/{skill.Name}");
        }

        Assert.True(unresolvable.Count == 0,
            "Skills not resolvable by full domain/name ref:\n" + string.Join("\n", unresolvable));
    }

    // ── Capability vocabulary enforcement ─────────────────────────────────────

    private static readonly HashSet<string> ApprovedCapabilitySlots =
    [
        "fast_general",
        "deep_analyst",
        "coder",
        "extractor",
        "critic",
    ];

    [Fact]
    public void AllSkillYamls_CapabilitySlots_AreFromApprovedVocabulary()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; }

            var rel = Path.GetRelativePath(skillsDir, file);
            foreach (var slot in def.CapabilityRequirements.Keys)
            {
                if (!ApprovedCapabilitySlots.Contains(slot))
                    violations.Add($"{rel}: unapproved capability slot '{slot}' (use one of: {string.Join(", ", ApprovedCapabilitySlots)})");
            }
        }

        Assert.True(violations.Count == 0,
            "Skill YAMLs use capability slots outside the approved vocabulary:\n"
            + string.Join("\n", violations));
    }

    // ── Scriban anti-patterns ──────────────────────────────────────────────────

    /// <summary>
    /// Regression test: <c>{{ if var }}</c> where var="" is truthy in Scriban.
    /// Model fields must use <c>{{ if var != "" }}</c> instead.
    /// Scans all skill AND prompt YAMLs for bare <c>{{ if model_override }}</c> patterns
    /// on lines containing "model:" — catches the empty-string truthiness bug.
    /// </summary>
    [Fact]
    public void AllYamlFiles_NoBareScribanTruthinessOnModelFields()
    {
        var root = RepoRoot();
        var dirs = new[] { Path.Combine(root, "skills"), Path.Combine(root, "prompts") };
        var modelFieldTruthiness = new Regex(
            @"model:\s*.*\{\{\s*if\s+\w+\s*\}\}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var violations = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (modelFieldTruthiness.IsMatch(lines[i]))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — bare {{ if var }} on model field (use {{ if var != \"\" }})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "YAML files use bare Scriban truthiness on model fields (empty string is truthy!):\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Regression test: <c>| string.default "value"</c> is NOT valid Scriban.
    /// It causes a silent parse error in RenderRaw (returns raw template text).
    /// All occurrences should be replaced with <c>?? "value"</c>.
    /// </summary>
    [Fact]
    public void AllSkillYamls_NoStringDefaultFilter()
    {
        var root = RepoRoot();
        var dirs = new[] { Path.Combine(root, "skills"), Path.Combine(root, "prompts") };

        var violations = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("string.default", StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — invalid '| string.default' (use '?? \"value\"' instead)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "YAML files use invalid Scriban filter '| string.default':\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Validates that all skills using section_title also set max_sections to "1".
    /// Using section_title with max_sections > 1 is a configuration error.
    /// </summary>
    [Fact]
    public void AllSkillYamls_SectionTitleRequiresMaxSectionsOne()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; }

            if (def.Name.Equals("prompt-blocks", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var step in def.Implementation)
            {
                if (!string.IsNullOrWhiteSpace(step.SectionTitle) && step.MaxSections != "1")
                    violations.Add($"{Path.GetRelativePath(skillsDir, file)}: step '{step.Name}' has section_title but max_sections='{step.MaxSections}' (must be '1')");
            }
        }

        Assert.True(violations.Count == 0,
            "Skills with section_title must have max_sections: '1':\n"
            + string.Join("\n", violations));
    }

    /// <summary>
    /// Validates that single-section skills use section_title instead of a trivial planning_prompt.
    /// Catches the old pattern to prevent regression.
    /// </summary>
    [Fact]
    public void AllSkillYamls_SingleSectionSkillsUseSectionTitle()
    {
        var skillsDir = Path.Combine(RepoRoot(), "skills");
        if (!Directory.Exists(skillsDir)) return;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var trivialPlanningPrompt = new Regex(
            @"Return exactly this JSON array",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.yaml", SearchOption.AllDirectories))
        {
            SkillDefinition def;
            try   { def = deserializer.Deserialize<SkillDefinition>(File.ReadAllText(file)); }
            catch { continue; }

            if (def.Name.Equals("prompt-blocks", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var step in def.Implementation)
            {
                if (step.MaxSections == "1"
                    && !string.IsNullOrWhiteSpace(step.PlanningPrompt)
                    && trivialPlanningPrompt.IsMatch(step.PlanningPrompt))
                {
                    violations.Add($"{Path.GetRelativePath(skillsDir, file)}: step '{step.Name}' has max_sections='1' with trivial planning_prompt — use section_title instead");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Single-section skills should use section_title instead of trivial planning_prompt:\n"
            + string.Join("\n", violations));
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string        EnvironmentName         { get; set; } = "Test";
        public string        ApplicationName         { get; set; } = "SAGIDE.Tests";
        public string        ContentRootPath         { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
