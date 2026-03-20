using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Infrastructure;

public class GitService : IWorkflowGitService
{
    private readonly ILogger<GitService> _logger;
    private readonly SemaphoreSlim _branchSetupLock = new(1, 1);
    // Lazy<T> guarantees thread-safe one-time initialization without explicit locking.
    private readonly Lazy<bool> _available;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
        _available = new Lazy<bool>(() =>
        {
            var (ok, _) = RunGitSync(".", "--version");
            if (!ok) _logger.LogWarning("Git not found on PATH — git auto-commit disabled");
            return ok;
        });
    }

    /// <summary>True if git is available on PATH. Checked once on first use (thread-safe).</summary>
    public bool IsAvailable => _available.Value;

    public bool IsGitRepo(string workspacePath) =>
        Directory.Exists(Path.Combine(workspacePath, ".git"));

    /// <summary>Cleans up any stale worktrees left by a previous crash. Called on startup.</summary>
    public Task PruneStaleWorktreesAsync(CancellationToken ct = default)
    {
        // Find any worktree paths in temp that look like ours (commit logs and shadow workspaces)
        var tmpDir = Path.GetTempPath();
        var stale = Directory.GetDirectories(tmpDir, "sag-ide-wt-*")
            .Concat(Directory.GetDirectories(tmpDir, "sag-ide-sw-*"));
        foreach (var wt in stale)
        {
            _logger.LogInformation("Cleaning up stale worktree/shadow: {Path}", wt);
            try { Directory.Delete(wt, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not delete stale worktree/shadow {Path}; skipping", wt); }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Commits the task result to the specified branch using a temporary git worktree.
    /// The user's working tree is never touched.
    /// </summary>
    public async Task CommitTaskResultAsync(
        string workspacePath,
        string taskId,
        string agentType,
        string description,
        string modelId,
        string output,
        string targetBranch = "sag-agent-log",
        CancellationToken ct = default)
    {
        if (!IsGitRepo(workspacePath))
        {
            _logger.LogDebug("Skipping git commit: {WorkspacePath} is not a git repo", workspacePath);
            return;
        }

        var shortId = taskId[..Math.Min(8, taskId.Length)];
        var worktreePath = Path.Combine(Path.GetTempPath(), $"sag-ide-wt-{shortId}");

        try
        {
            // Ensure the target branch exists (race-safe via lock)
            await EnsureBranchExistsAsync(workspacePath, targetBranch, ct);

            // Add worktree pointing to the agent-log branch
            if (Directory.Exists(worktreePath))
                Directory.Delete(worktreePath, recursive: true);

            var (addOk, addErr) = await RunGitAsync(workspacePath,
                $"worktree add \"{worktreePath}\" {targetBranch}", ct);
            if (!addOk)
            {
                _logger.LogError("Failed to add git worktree: {Error}", addErr);
                return;
            }

            // Write result markdown
            var resultsDir = Path.Combine(worktreePath, ".sag-results");
            Directory.CreateDirectory(resultsDir);
            var mdPath = Path.Combine(resultsDir, $"{taskId}.md");
            await File.WriteAllTextAsync(mdPath, FormatResultMarkdown(taskId, agentType, description, modelId, output), ct);

            // Stage and commit
            await RunGitAsync(worktreePath, "add .sag-results/", ct);

            var safeSummary = description.Length > 60 ? description[..60] + "..." : description;
            var commitMsg = $"sag({agentType}): {safeSummary} [{shortId}]";
            var (commitOk, commitErr) = await RunGitAsync(worktreePath,
                $"commit --no-verify -m \"{commitMsg.Replace("\"", "'")}\"", ct);

            if (commitOk)
                _logger.LogInformation("Task {TaskId} result committed to branch '{Branch}'", taskId, targetBranch);
            else if (!commitErr.Contains("nothing to commit"))
                _logger.LogError("Git commit failed: {Error}", commitErr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit task result {TaskId} to git", taskId);
        }
        finally
        {
            // Always remove the worktree
            if (Directory.Exists(worktreePath))
            {
                await RunGitAsync(workspacePath, $"worktree remove --force \"{worktreePath}\"", ct);
            }
        }
    }

    // ── Shadow workspace methods () ─────────────────────────────────────────

    /// <summary>
    /// Creates an isolated git worktree in %TEMP%/sag-ide-sw-{instanceId8} branching from
    /// <paramref name="branch"/> (default HEAD). Returns the shadow path on success, or null
    /// when git is unavailable or the workspace is not a git repo.
    /// </summary>
    public async Task<string?> ProvisionShadowAsync(
        string workspacePath, string instanceId, string branch = "HEAD", CancellationToken ct = default)
    {
        if (!IsAvailable || !IsGitRepo(workspacePath))
        {
            _logger.LogWarning("ProvisionShadow: git not available or not a git repo at {Path}", workspacePath);
            return null;
        }

        var shortId = instanceId[..Math.Min(8, instanceId.Length)];
        var shadowPath = Path.Combine(Path.GetTempPath(), $"sag-ide-sw-{shortId}");

        // Remove any leftover from a prior run
        if (Directory.Exists(shadowPath))
        {
            await RunGitAsync(workspacePath, $"worktree remove --force \"{shadowPath}\"", ct);
            if (Directory.Exists(shadowPath))
                try { Directory.Delete(shadowPath, recursive: true); } catch { /* best effort */ }
        }

        var (ok, err) = await RunGitAsync(workspacePath, $"worktree add \"{shadowPath}\" {branch}", ct);
        if (!ok)
        {
            _logger.LogError("Failed to provision shadow worktree at {Path}: {Error}", shadowPath, err);
            return null;
        }

        _logger.LogInformation("Shadow worktree provisioned at {Path} (branch: {Branch})", shadowPath, branch);
        return shadowPath;
    }

    /// <summary>
    /// Removes the shadow worktree, first via <c>git worktree remove --force</c> and falling
    /// back to a recursive directory delete.
    /// </summary>
    public async Task DestroyShadowAsync(
        string workspacePath, string shadowPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(shadowPath)) return;

        var (ok, _) = await RunGitAsync(workspacePath, $"worktree remove --force \"{shadowPath}\"", ct);
        if (!ok && Directory.Exists(shadowPath))
        {
            _logger.LogWarning("git worktree remove failed; falling back to Directory.Delete for {Path}", shadowPath);
            try { Directory.Delete(shadowPath, recursive: true); } catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete shadow worktree directory {Path}", shadowPath);
            }
        }
        else
        {
            _logger.LogInformation("Shadow worktree destroyed: {Path}", shadowPath);
        }
    }

    /// <summary>
    /// Promotes changes made inside the shadow worktree back to the real workspace by
    /// computing a <c>git diff HEAD</c> patch and applying it with <c>git apply --index</c>.
    /// On success, destroys the shadow. Returns (false, errorMessage) if the apply fails.
    /// </summary>
    public async Task<(bool Success, string Summary)> PromoteShadowAsync(
        string workspacePath, string shadowPath, CancellationToken ct = default)
    {
        // Get the diff from the shadow worktree
        var (diffOk, patch) = await RunGitAsync(shadowPath, "diff HEAD", ct);
        if (!diffOk)
            return (false, $"Failed to compute shadow diff: {patch}");

        if (string.IsNullOrWhiteSpace(patch))
        {
            await DestroyShadowAsync(workspacePath, shadowPath, ct);
            return (true, "No changes to promote from shadow workspace");
        }

        // Write patch to a temp file and apply to real workspace
        var patchFile = Path.Combine(Path.GetTempPath(), $"sag-ide-sw-patch-{Guid.NewGuid():N}.patch");
        try
        {
            await File.WriteAllTextAsync(patchFile, patch, ct);
            var (applyOk, applyErr) = await RunGitAsync(workspacePath, $"apply --index \"{patchFile}\"", ct);
            if (!applyOk)
                return (false, $"git apply failed: {applyErr}");

            // Capture a short stat for the step output
            var (_, stat) = await RunGitAsync(workspacePath, "diff HEAD --stat", ct);
            await DestroyShadowAsync(workspacePath, shadowPath, ct);
            _logger.LogInformation("Shadow changes promoted to {WorkspacePath}", workspacePath);
            return (true, string.IsNullOrWhiteSpace(stat) ? "Changes promoted successfully" : stat.Trim());
        }
        finally
        {
            try { File.Delete(patchFile); } catch { /* best effort */ }
        }
    }

    private async Task EnsureBranchExistsAsync(string workspacePath, string branch, CancellationToken ct)
    {
        await _branchSetupLock.WaitAsync(ct);
        try
        {
            var (exists, _) = await RunGitAsync(workspacePath, $"rev-parse --verify {branch}", ct);
            if (exists) return;

            // Create an orphan branch with an empty initial commit
            // We do this in the main worktree, then immediately return to previous branch
            var (currentBranchOk, currentBranch) = await RunGitAsync(workspacePath, "rev-parse --abbrev-ref HEAD", ct);
            var returnTo = currentBranchOk ? currentBranch.Trim() : "HEAD";

            await RunGitAsync(workspacePath, $"checkout --orphan {branch}", ct);
            await RunGitAsync(workspacePath, "rm -rf --cached .", ct);
            await RunGitAsync(workspacePath,
                $"commit --allow-empty --no-verify -m \"sag: initialize {branch} branch\"", ct);
            await RunGitAsync(workspacePath, $"checkout {returnTo}", ct);

            _logger.LogInformation("Created orphan branch '{Branch}' for agent task results", branch);
        }
        finally
        {
            _branchSetupLock.Release();
        }
    }

    private static string FormatResultMarkdown(
        string taskId, string agentType, string description, string modelId, string output)
    {
        return $"""
            # {agentType} Result

            **Task ID:** `{taskId}`
            **Model:** {modelId}
            **Description:** {description}
            **Timestamp:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC

            ---

            {output}

            ---
            *Generated by SAG IDE*
            """;
    }

    private (bool success, string output) RunGitSync(string workingDirectory, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode == 0, stdout + stderr);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    /// <summary>
    /// Runs a read-only git command in the given workspace and returns (output, exitCode).
    /// Exposed for <c>GitTool</c> in the tool registry — write operations are rejected.
    /// </summary>
    public async Task<(string Output, int ExitCode)> RunReadOnlyAsync(
        string workspacePath, string arguments, CancellationToken ct = default)
    {
        var (success, output) = await RunGitAsync(workspacePath, arguments, ct);
        return (output, success ? 0 : 1);
    }

    private async Task<(bool success, string output)> RunGitAsync(
        string workingDirectory, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start git process");

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return (process.ExitCode == 0, stdout + stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("git {Args} in {Dir} threw: {Msg}", arguments, workingDirectory, ex.Message);
            return (false, ex.Message);
        }
    }
}
