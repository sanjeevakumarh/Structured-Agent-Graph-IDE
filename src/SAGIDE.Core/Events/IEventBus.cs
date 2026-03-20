namespace SAGIDE.Core.Events;

/// <summary>
/// In-process event bus: decouples publishers from subscribers and
/// isolates subscriber exceptions so one failing handler cannot
/// prevent other handlers from running.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes <paramref name="evt"/> to all registered handlers.
    /// Exceptions thrown by individual handlers are caught and logged; they do NOT
    /// propagate to the caller or to subsequent handlers.
    /// </summary>
    void Publish<TEvent>(TEvent evt) where TEvent : class;

    /// <summary>Registers a synchronous handler for events of type <typeparamref name="TEvent"/>.</summary>
    void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;

    /// <summary>
    /// Registers an asynchronous handler. The returned <see cref="Task"/> is awaited
    /// fire-and-forget; exceptions are caught and logged.
    /// </summary>
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
}
