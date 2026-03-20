using SAGIDE.Core.Events;

namespace SAGIDE.Workflows;

/// <summary>No-op event bus used as a constructor default when no IEventBus is registered.</summary>
internal sealed class NullEventBus : IEventBus
{
    public void Publish<TEvent>(TEvent evt) where TEvent : class { }
    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class { }
    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class { }
}
