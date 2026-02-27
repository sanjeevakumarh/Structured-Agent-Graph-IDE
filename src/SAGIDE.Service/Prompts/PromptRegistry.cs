using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Loads all prompt YAML files from the configured PromptsPath directory, indexes them by
/// (domain, name), and hot-reloads when files change on disk.
/// </summary>
public sealed class PromptRegistry : IDisposable
{
    private readonly string _promptsRoot;
    private readonly ILogger<PromptRegistry> _logger;
    private readonly IDeserializer _yaml;
    private readonly FileSystemWatcher _watcher;

    // Keyed by "{domain}/{name}" (lower-case)
    private volatile Dictionary<string, PromptDefinition> _index = [];

    public PromptRegistry(IConfiguration configuration, IHostEnvironment env, ILogger<PromptRegistry> logger)
    {
        _logger = logger;

        // Resolve PromptsPath relative to ContentRootPath so that relative paths
        // like "../../prompts" work regardless of working directory.
        var configuredPath = configuration["SAGIDE:PromptsPath"] ?? "prompts";
        _promptsRoot = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadAll();

        // Watch for file changes and reload without restarting the service.
        // FileSystemWatcher throws ArgumentException if the path does not exist, so we
        // create it only when the directory is present; if it is absent we still return
        // an empty index and leave the watcher disabled.
        if (Directory.Exists(_promptsRoot))
        {
            _watcher = new FileSystemWatcher(_promptsRoot, "*.yaml")
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
            // Placeholder watcher that never raises events (path is omitted on purpose).
            _watcher = new FileSystemWatcher { EnableRaisingEvents = false };
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public IReadOnlyList<PromptDefinition> GetAll() =>
        [.. _index.Values];

    public IReadOnlyList<PromptDefinition> GetByDomain(string domain)
    {
        var prefix = domain.ToLowerInvariant() + "/";
        return [.. _index
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)];
    }

    public PromptDefinition? GetByKey(string domain, string name)
    {
        var key = MakeKey(domain, name);
        return _index.TryGetValue(key, out var def) ? def : null;
    }

    public IReadOnlyList<PromptDefinition> GetScheduled() =>
        [.. _index.Values.Where(p => !string.IsNullOrWhiteSpace(p.Schedule))];

    // ── Internal loading ──────────────────────────────────────────────────────

    private void LoadAll()
    {
        if (!Directory.Exists(_promptsRoot))
        {
            _logger.LogWarning("PromptsPath does not exist: {Path}", _promptsRoot);
            _index = [];
            return;
        }

        var next = new Dictionary<string, PromptDefinition>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            // Materialize the list immediately — the directory could be deleted between
            // the Exists check above and the enumeration (race condition in tests / deployment).
            files = Directory.EnumerateFiles(_promptsRoot, "*.yaml", SearchOption.AllDirectories).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogWarning("PromptsPath was removed before enumeration completed: {Path}", _promptsRoot);
            _index = [];
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var def  = _yaml.Deserialize<PromptDefinition>(text);
                if (string.IsNullOrEmpty(def.Name) || string.IsNullOrEmpty(def.Domain))
                {
                    _logger.LogWarning("Prompt file missing name/domain, skipping: {File}", file);
                    continue;
                }
                def.FilePath = file;
                next[MakeKey(def.Domain, def.Name)] = def;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load prompt YAML: {File}", file);
            }
        }

        _index = next;
        _logger.LogInformation("Prompt registry loaded {Count} prompts from {Path}", next.Count, _promptsRoot);
    }

    private static string MakeKey(string domain, string name) =>
        $"{domain.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    public void Dispose() => _watcher.Dispose();
}
