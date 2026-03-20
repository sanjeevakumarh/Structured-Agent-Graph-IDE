using System.Collections.Concurrent;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Thread-safe in-process implementation of <see cref="ISessionMemory"/>.
///
/// Each session (SubtaskCoordinator run, workflow instance) creates its own
/// instance via scoped DI so sessions never share state.
///
/// This is intentionally the simplest possible implementation — no persistence,
/// no eviction, no serialization overhead. Session data is discarded when the
/// instance is GC'd.
/// </summary>
public sealed class InMemorySessionMemory : ISessionMemory
{
    private readonly ConcurrentDictionary<string, string> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string value)     => _store[key] = value;
    public string? Get(string key)                => _store.TryGetValue(key, out var v) ? v : null;
    public bool Contains(string key)              => _store.ContainsKey(key);
    public IReadOnlyDictionary<string, string> All => _store;
    public void Clear()                           => _store.Clear();
}
