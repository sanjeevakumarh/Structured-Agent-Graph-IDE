namespace SAGIDE.Service.Tests;

/// <summary>
/// Requirement: No environment-specific identifiers (real hostnames, usernames, local paths)
/// should appear in any shared artifact — prompt YAMLs, docs, source code, or planning docs.
///
/// All machine references in prompt YAMLs must use the logical alias names defined in
/// appsettings.json under SAGIDE:Ollama:Servers[].Name and SAGIDE:OpenAICompatible:Servers[].Name.
/// Real hostnames belong only inside appsettings.json (which is gitignored / kept private).
///
    /// Approved aliases: localhost, workstation, 
/// When you add a new machine, add its alias here AND in appsettings.json — never use the real hostname elsewhere.
/// </summary>
public class EnvironmentLeakTests
{
    // Logical aliases defined in appsettings.json — the ONLY names permitted in @machine notation
    private static readonly HashSet<string> ApprovedAliases =
    [
    ];

    // Real hostnames / usernames that must never appear in shared files
    private static readonly string[] ForbiddenPatterns =
    [

    ];

    // Directories that are shared (scanned for leaks)
    private static readonly string[] SharedDirs = ["prompts", "docs", "src", "tools"];

    // Extensions to scan in shared dirs
    private static readonly string[] ScannedExtensions = [".yaml", ".cs", ".ts", ".md", ".json"];

    // Files that are explicitly environment-specific and excluded from the scan
    private static readonly string[] ExcludedFiles =
    [
        "appsettings.json",
        "appsettings.Development.json",
        "appsettings.Production.json",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (no .git directory found).");
    }

    [Fact]
    public void SharedFiles_ContainNoForbiddenHostnames()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var sharedDir in SharedDirs)
        {
            var dirPath = Path.Combine(root, sharedDir);
            if (!Directory.Exists(dirPath)) continue;

            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                if (!ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (ExcludedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var forbidden in ForbiddenPatterns)
                    {
                        if (lines[i].Contains(forbidden, StringComparison.Ordinal))
                            violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — contains '{forbidden}'");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Environment-specific identifiers found in shared files:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void PromptYamls_MachineNames_AreApprovedAliases()
    {
        var root = RepoRoot();
        var promptsDir = Path.Combine(root, "prompts");
        if (!Directory.Exists(promptsDir)) return;

        var violations = new List<string>();
        var atMachine = new System.Text.RegularExpressions.Regex(@"@([\w][\w\-]*)");

        foreach (var file in Directory.EnumerateFiles(promptsDir, "*.yaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (System.Text.RegularExpressions.Match m in atMachine.Matches(lines[i]))
                {
                    var alias = m.Groups[1].Value;
                    if (!ApprovedAliases.Contains(alias))
                        violations.Add($"{Path.GetRelativePath(root, file)}:{i + 1} — unapproved machine alias '@{alias}' (add to ApprovedAliases and appsettings.json)");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Prompt YAMLs reference unapproved machine aliases:\n" + string.Join("\n", violations));
    }
}
