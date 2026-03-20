// IEventBus has been promoted to SAGIDE.Core.Events.
// This alias keeps existing code in SAGIDE.Service.* compiling without changes.
global using IEventBus = SAGIDE.Core.Events.IEventBus;

namespace SAGIDE.Service.Events;
