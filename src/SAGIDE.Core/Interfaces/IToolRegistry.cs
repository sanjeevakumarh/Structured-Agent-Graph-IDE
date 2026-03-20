namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Registry of all tools available to agents in the Agent OS.
///
/// Tools are registered at startup and resolved by name at call time.
/// The Security layer intercepts <see cref="ExecuteAsync"/> to enforce
/// per-caller permission policies before the tool actually runs.
/// </summary>
public interface IToolRegistry
{
    /// <summary>Register a tool. Replaces any existing tool with the same name.</summary>
    void Register(ITool tool);

    /// <summary>Returns the tool with the given name, or null if not registered.</summary>
    ITool? Get(string name);

    /// <summary>All currently registered tools.</summary>
    IReadOnlyList<ITool> All { get; }

    /// <summary>
    /// Execute a named tool with the given parameters.
    /// Throws <see cref="InvalidOperationException"/> when the tool is not registered.
    /// </summary>
    Task<string> ExecuteAsync(string toolName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);
}
