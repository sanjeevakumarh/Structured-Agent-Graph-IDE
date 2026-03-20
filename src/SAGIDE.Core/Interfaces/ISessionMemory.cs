namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Ephemeral key-value store scoped to one agent session (one SubtaskCoordinator run,
/// one workflow instance, or one named-pipe connection).
///
/// Session memory lives in-process and is discarded when the session ends.
/// It is the mechanism for passing intermediate step outputs between data_collection
/// steps and subtask prompts without threading the dictionary through every call.
///
/// The default implementation is <c>InMemorySessionMemory</c> — a thread-safe
/// dictionary with no persistence. A scoped DI lifetime is recommended so each
/// session gets a fresh instance.
/// </summary>
public interface ISessionMemory
{
    /// <summary>Stores a value under <paramref name="key"/>.</summary>
    void Set(string key, string value);

    /// <summary>Returns the value for <paramref name="key"/>, or null if absent.</summary>
    string? Get(string key);

    /// <summary>Returns true when <paramref name="key"/> is present.</summary>
    bool Contains(string key);

    /// <summary>All key-value pairs currently in session memory.</summary>
    IReadOnlyDictionary<string, string> All { get; }

    /// <summary>Removes all entries (called at the start of each run).</summary>
    void Clear();
}
