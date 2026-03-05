using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Loads all skill YAML files from the configured SkillsPath directory, indexes them by
/// (domain, name), and hot-reloads when files change on disk.
/// Mirrors the PromptRegistry pattern.
/// </summary>
public sealed class SkillRegistry : IDisposable
{
    private readonly string _skillsRoot;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly IDeserializer _yaml;
    private readonly FileSystemWatcher _watcher;

    // Keyed by "{domain}/{name}" (lower-case)
    private volatile Dictionary<string, SkillDefinition> _index = [];

    /// <summary>
    /// Shared text blocks loaded from <c>skills/shared/prompt-blocks.yaml</c>.
    /// Available in skill templates as <c>{{blocks.block_name}}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PromptBlocks { get; private set; }
        = new Dictionary<string, string>();

    public SkillRegistry(IConfiguration configuration, IHostEnvironment env, ILogger<SkillRegistry> logger)
    {
        _logger = logger;

        var configuredPath = configuration["SAGIDE:SkillsPath"] ?? "../../skills";
        _skillsRoot = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

        // If the resolved path doesn't exist (common when the release exe runs without
        // an explicit --SAGIDE:SkillsPath override), walk up from the exe location to
        // find the repo-root skills/ directory automatically.
        if (!Directory.Exists(_skillsRoot))
        {
            var discovered = WalkUpForSkillsDir(AppContext.BaseDirectory);
            if (discovered is not null)
            {
                _logger.LogInformation(
                    "SkillsPath '{Configured}' not found; auto-discovered skills directory: {Discovered}",
                    _skillsRoot, discovered);
                _skillsRoot = discovered;
            }
        }

        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadAll();

        if (Directory.Exists(_skillsRoot))
        {
            _watcher = new FileSystemWatcher(_skillsRoot, "*.yaml")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents   = true,
            };
            _watcher.Changed += (_, _) => LoadAll();
            _watcher.Created += (_, _) => LoadAll();
            _watcher.Deleted += (_, _) => LoadAll();
            _watcher.Renamed += (_, _) => LoadAll();
        }
        else
        {
            _logger.LogWarning("SkillsPath does not exist: {Path} — skill library will be empty", _skillsRoot);
            _watcher = new FileSystemWatcher { EnableRaisingEvents = false };
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public IReadOnlyList<SkillDefinition> GetAll() => [.. _index.Values];

    public IReadOnlyList<SkillDefinition> GetByDomain(string domain)
    {
        var prefix = domain.ToLowerInvariant() + "/";
        return [.. _index
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)];
    }

    public SkillDefinition? GetByKey(string domain, string name) =>
        _index.TryGetValue(MakeKey(domain, name), out var def) ? def : null;

    /// <summary>
    /// Resolves a skill reference that is either "domain/name" or just "name".
    /// When only a name is given, searches all domains and returns the first match.
    /// Returns null if not found; logs a warning.
    /// </summary>
    public SkillDefinition? Resolve(string skillRef)
    {
        // Fully-qualified: "research/web-research-track"
        if (skillRef.Contains('/'))
        {
            var slash = skillRef.IndexOf('/');
            var domain = skillRef[..slash].Trim();
            var name   = skillRef[(slash + 1)..].Trim();
            var result = GetByKey(domain, name);
            if (result is null)
                _logger.LogWarning("Skill '{Ref}' not found in registry", skillRef);
            return result;
        }

        // Short name: search all domains
        var lower = skillRef.ToLowerInvariant();
        var match = _index.Values.FirstOrDefault(s => s.Name.ToLowerInvariant() == lower);
        if (match is null)
            _logger.LogWarning("Skill '{Ref}' not found in registry (searched all domains)", skillRef);
        return match;
    }

    // ── Internal loading ────────────────────────────────────────────────────────

    private void LoadAll()
    {
        if (!Directory.Exists(_skillsRoot))
        {
            _index = [];
            return;
        }

        var next = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_skillsRoot, "*.yaml", SearchOption.AllDirectories).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            _index = [];
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var def  = _yaml.Deserialize<SkillDefinition>(text);
                if (string.IsNullOrEmpty(def.Name) || string.IsNullOrEmpty(def.Domain))
                {
                    _logger.LogWarning("Skill file missing name/domain, skipping: {File}", file);
                    continue;
                }
                // prompt-blocks.yaml is a block library, not a real skill — skip indexing
                if (def.Name.Equals("prompt-blocks", StringComparison.OrdinalIgnoreCase))
                    continue;

                def.FilePath = file;
                next[MakeKey(def.Domain, def.Name)] = def;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill YAML: {File}", file);
            }
        }

        _index = next;

        // ── Load prompt blocks from the well-known file ──────────────────────
        LoadPromptBlocks();

        _logger.LogInformation("Skill registry loaded {Count} skills from {Path}", next.Count, _skillsRoot);
    }

    private void LoadPromptBlocks()
    {
        var blocksFile = Path.Combine(_skillsRoot, "shared.prompt-blocks.yaml");
        if (!File.Exists(blocksFile))
            blocksFile = Path.Combine(_skillsRoot, "shared", "prompt-blocks.yaml");
        if (!File.Exists(blocksFile))
        {
            PromptBlocks = new Dictionary<string, string>();
            return;
        }

        try
        {
            var text = File.ReadAllText(blocksFile);
            var raw  = _yaml.Deserialize<Dictionary<string, object>>(text);
            if (raw is not null
                && raw.TryGetValue("blocks", out var blocksObj)
                && blocksObj is Dictionary<object, object> dict)
            {
                var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    if (kv.Key is string key && kv.Value is string val)
                        blocks[key] = val;
                }
                PromptBlocks = blocks;
                _logger.LogInformation("Loaded {Count} prompt blocks from {File}", blocks.Count, blocksFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompt blocks from {File}", blocksFile);
        }
    }

    private static string MakeKey(string domain, string name) =>
        $"{domain.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a <c>skills/</c> subdirectory.
    /// Returns the first match, or null if none is found before reaching the filesystem root.
    /// Used as a fallback when the configured SkillsPath doesn't exist (e.g. release exe run
    /// without the --SAGIDE:SkillsPath command-line override).
    /// </summary>
    private static string? WalkUpForSkillsDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "skills");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose() => _watcher.Dispose();
}
