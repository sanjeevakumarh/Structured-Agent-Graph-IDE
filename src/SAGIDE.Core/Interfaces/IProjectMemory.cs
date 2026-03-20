namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persistent key-value store scoped to a workspace path.
///
/// Project memory accumulates facts about a workspace across sessions:
/// git history summaries, architecture notes, previous agent decisions, etc.
/// Values survive service restarts; the workspace path is the partition key.
///
/// The default implementation is <c>SqliteProjectMemory</c> in <c>SAGIDE.Service</c>
/// which stores entries in the existing SQLite database.
///
/// Intended usage patterns:
/// <list type="bullet">
///   <item>Store a git-log summary after a git-history indexing run.</item>
///   <item>Record which files an agent previously modified (for change-set context).</item>
///   <item>Cache expensive analysis results that don't change between runs.</item>
/// </list>
/// </summary>
public interface IProjectMemory
{
    /// <summary>Stores or updates a fact for the given workspace.</summary>
    Task SetAsync(string workspacePath, string key, string value, CancellationToken ct = default);

    /// <summary>Returns the stored value, or null if absent.</summary>
    Task<string?> GetAsync(string workspacePath, string key, CancellationToken ct = default);

    /// <summary>Returns all key-value pairs stored for the workspace.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(string workspacePath, CancellationToken ct = default);

    /// <summary>Removes a specific fact from the workspace store.</summary>
    Task DeleteAsync(string workspacePath, string key, CancellationToken ct = default);
}
