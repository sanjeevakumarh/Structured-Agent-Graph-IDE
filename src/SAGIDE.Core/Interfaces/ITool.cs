namespace SAGIDE.Core.Interfaces;

/// <summary>
/// A discrete capability that agents can invoke — file system, git, web, shell, etc.
///
/// Every tool is registered in <see cref="IToolRegistry"/> and identified by a unique
/// <see cref="Name"/>. The Security layer gates tool calls by checking the tool name
/// against the caller's permission set before dispatch.
/// </summary>
public interface ITool
{
    /// <summary>Unique name, e.g. "git", "web_fetch", "web_search", "shell".</summary>
    string Name { get; }

    /// <summary>Human-readable description shown in the tool registry API.</summary>
    string Description { get; }

    /// <summary>
    /// Execute the tool with the given named parameters.
    /// Returns a string result (markdown, JSON, or plain text depending on the tool).
    /// </summary>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);
}
