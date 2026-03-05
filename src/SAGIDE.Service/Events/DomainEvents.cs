using SAGIDE.Core.DTOs;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Events;

/// <summary>Published by AgentOrchestrator whenever a task's status changes.</summary>
public sealed record TaskUpdatedEvent(TaskStatusResponse Status);

/// <summary>Published by AgentOrchestrator for each streaming output delta.</summary>
public sealed record StreamingOutputEvent(StreamingOutputMessage Message);

/// <summary>Published by WorkflowEngine when a workflow instance changes state.</summary>
public sealed record WorkflowUpdatedEvent(WorkflowInstance Instance);

/// <summary>
/// Published by WorkflowEngine when a human_approval step becomes active,
/// or when convergence policy escalates to HUMAN_APPROVAL.
/// </summary>
public sealed record WorkflowApprovalNeededEvent(string InstanceId, string StepId, string Prompt);
