// DomainEvents have been promoted to SAGIDE.Core.Events.
// Re-export them from this namespace so existing code compiles without changes.
global using TaskUpdatedEvent          = SAGIDE.Core.Events.TaskUpdatedEvent;
global using StreamingOutputEvent      = SAGIDE.Core.Events.StreamingOutputEvent;
global using WorkflowUpdatedEvent      = SAGIDE.Core.Events.WorkflowUpdatedEvent;
global using WorkflowApprovalNeededEvent = SAGIDE.Core.Events.WorkflowApprovalNeededEvent;

namespace SAGIDE.Service.Events;
