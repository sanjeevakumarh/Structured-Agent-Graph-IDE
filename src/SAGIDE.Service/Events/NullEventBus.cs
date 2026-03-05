namespace SAGIDE.Service.Events;

/// <summary>
/// No-op event bus used in tests and as a constructor default when no
/// <see cref="IEventBus"/> is registered.  Publishes are silently discarded.
/// </summary>
internal sealed class NullEventBus : IEventBus
{
    public void Publish<TEvent>(TEvent evt) where TEvent : class { }
    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class { }
    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class { }
}
