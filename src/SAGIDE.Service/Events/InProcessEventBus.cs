using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Events;

/// <summary>
/// Thread-safe in-process event bus backed by <see cref="ConcurrentDictionary"/>.
/// Each handler is invoked independently; an exception in one handler is logged and
/// swallowed so remaining handlers always run.
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    private readonly ILogger<InProcessEventBus> _logger;

    public InProcessEventBus(ILogger<InProcessEventBus> logger)
    {
        _logger = logger;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        AddHandler(typeof(TEvent), handler);
    }

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        AddHandler(typeof(TEvent), handler);
    }

    private void AddHandler(Type eventType, Delegate handler)
    {
        _handlers.AddOrUpdate(
            eventType,
            _ => [handler],
            (_, existing) =>
            {
                lock (existing) { existing.Add(handler); }
                return existing;
            });
    }

    // ── Publishing ────────────────────────────────────────────────────────────

    public void Publish<TEvent>(TEvent evt) where TEvent : class
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers)) return;

        List<Delegate> snapshot;
        lock (handlers) { snapshot = [..handlers]; }

        foreach (var handler in snapshot)
        {
            try
            {
                switch (handler)
                {
                    case Action<TEvent> sync:
                        sync(evt);
                        break;

                    case Func<TEvent, Task> async:
                        _ = InvokeAsync(async, evt);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Synchronous event handler for {EventType} threw; remaining handlers will still run",
                    typeof(TEvent).Name);
            }
        }
    }

    private async Task InvokeAsync<TEvent>(Func<TEvent, Task> handler, TEvent evt)
    {
        try
        {
            await handler(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Async event handler for {EventType} threw",
                typeof(TEvent).Name);
        }
    }
}
