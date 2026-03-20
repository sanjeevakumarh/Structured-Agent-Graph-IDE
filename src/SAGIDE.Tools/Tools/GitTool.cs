using SAGIDE.Core.Interfaces;

namespace SAGIDE.Tools.Tools;

/// <summary>
/// Tool wrapper for read-only git operations.
///
/// Parameters:
///   workspace  (required) — absolute path to the git repository root
///   command    (required) — git subcommand and args, e.g. "log --oneline -10" or "diff HEAD~1"
///
/// Only read-only commands are allowed (log, diff, show, status, branch, tag).
/// Write operations (commit, push, reset, checkout) are blocked — use GitService directly
/// for those, as they require additional context and auth.
///
/// Backed by a delegate so this assembly has no direct reference to GitService.
/// </summary>
public sealed class GitTool : ITool
{
    public string Name        => "git";
    public string Description => "Runs a read-only git command in a workspace and returns the output.";

    private static readonly HashSet<string> _allowedSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "diff", "show", "status", "branch", "tag",
        "shortlog", "blame", "ls-files", "ls-tree", "rev-list",
        "describe", "reflog", "stash",
    };

    /// <param name="runDelegate">
    /// Delegate: (workspacePath, commandLine, ct) → (stdout, exitCode).
    /// Typically wraps GitService.RunGitAsync or a Process invocation.
    /// </param>
    private readonly Func<string, string, CancellationToken, Task<(string Output, int ExitCode)>> _runDelegate;

    public GitTool(Func<string, string, CancellationToken, Task<(string Output, int ExitCode)>> runDelegate)
    {
        _runDelegate = runDelegate;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("workspace", out var workspace) || string.IsNullOrWhiteSpace(workspace))
            throw new ArgumentException("Parameter 'workspace' is required for git tool.");

        if (!parameters.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Parameter 'command' is required for git tool.");

        // Safety guard — only allow read-only subcommands
        var subcommand = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!_allowedSubcommands.Contains(subcommand))
            throw new InvalidOperationException(
                $"Git subcommand '{subcommand}' is not permitted via the tool registry. " +
                $"Allowed: {string.Join(", ", _allowedSubcommands)}");

        var (output, exitCode) = await _runDelegate(workspace, command, ct);

        return exitCode == 0
            ? output
            : $"[git exited with code {exitCode}]\n{output}";
    }
}
