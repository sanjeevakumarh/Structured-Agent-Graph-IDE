using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Communication.Messages;
using SAGIDE.Service.Events;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Services;

public class ServiceLifetime : BackgroundService
{
    private readonly NamedPipeServer    _pipeServer;
    private readonly AgentOrchestrator  _orchestrator;
    private readonly IWorkflowEngine    _workflowEngine;
    private readonly ILogger<ServiceLifetime> _logger;

    public ServiceLifetime(
        NamedPipeServer    pipeServer,
        AgentOrchestrator  orchestrator,
        IWorkflowEngine    workflowEngine,
        CommunicationConfig commConfig,
        IEventBus          eventBus,
        ILogger<ServiceLifetime> logger)
    {
        _pipeServer     = pipeServer;
        _orchestrator   = orchestrator;
        _workflowEngine = workflowEngine;
        _logger         = logger;

        // A6: Subscribe via the event bus — each handler runs independently;
        // an exception in one handler will not prevent others from executing.

        // Route task-status changes to the pipe client that owns the task.
        // When BroadcastAllTasks is true (debug mode), all updates go to all clients —
        // useful for watching REST-submitted workflow execution in VSCode's streaming panel.
        eventBus.Subscribe<TaskUpdatedEvent>(e =>
        {
            var msg = new PipeMessage
            {
                Type    = MessageTypes.TaskUpdate,
                Payload = JsonSerializer.SerializeToUtf8Bytes(e.Status, NamedPipeServer.JsonOptions),
            };

            if (commConfig.BroadcastAllTasks)
            {
                _ = _pipeServer.BroadcastAsync(msg);
                return;
            }

            // Only forward to the pipe client that submitted this task
            var clientId = _pipeServer.GetTaskOwner(e.Status.TaskId);
            if (clientId is not null)
                _ = _pipeServer.SendToClientAsync(clientId, msg);
            // else: REST-originated task — don't send to VSCode
        });

        // Route streaming output to the pipe client that submitted the task.
        // When BroadcastAllTasks is true, unowned tasks also go to all clients.
        eventBus.Subscribe<StreamingOutputEvent>(e =>
        {
            var msg = new PipeMessage
            {
                Type    = MessageTypes.StreamingOutput,
                Payload = JsonSerializer.SerializeToUtf8Bytes(e.Message, NamedPipeServer.JsonOptions),
            };
            var clientId = _pipeServer.GetTaskOwner(e.Message.TaskId);
            if (clientId is not null)
                _ = _pipeServer.SendToClientAsync(clientId, msg);
            else if (commConfig.BroadcastAllTasks)
                _ = _pipeServer.BroadcastAsync(msg);
            // else: REST-originated task — don't send to VSCode
        });

        // Broadcast workflow instance updates (step completions, status changes).
        eventBus.Subscribe<WorkflowUpdatedEvent>(e =>
        {
            var msg = new PipeMessage
            {
                Type    = MessageTypes.WorkflowUpdate,
                Payload = JsonSerializer.SerializeToUtf8Bytes(e.Instance, NamedPipeServer.JsonOptions),
            };
            _ = _pipeServer.BroadcastAsync(msg);
        });

        // Broadcast human approval requests to the VS Code client.
        eventBus.Subscribe<WorkflowApprovalNeededEvent>(e =>
        {
            var msg = new PipeMessage
            {
                Type    = MessageTypes.WorkflowApprovalNeeded,
                Payload = JsonSerializer.SerializeToUtf8Bytes(
                    new { instanceId = e.InstanceId, stepId = e.StepId, prompt = e.Prompt },
                    NamedPipeServer.JsonOptions),
            };
            _ = _pipeServer.BroadcastAsync(msg);
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agentic IDE Service starting...");

        // Start pipe server and orchestrator in parallel
        var pipeTask         = _pipeServer.StartAsync(stoppingToken);
        var orchestratorTask = _orchestrator.StartProcessingAsync(stoppingToken);

        // Wait for the orchestrator to finish loading persisted tasks before recovering
        // workflow instances.  Recovery resubmits steps to the task queue; if the queue
        // is not yet populated the _taskToStep reverse map will be stale after restart.
        await _orchestrator.InitializationCompleted;
        _ = _workflowEngine.RecoverRunningInstancesAsync(stoppingToken);

        _logger.LogInformation("Agentic IDE Service is ready");

        await Task.WhenAll(pipeTask, orchestratorTask);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agentic IDE Service stopping...");
        await _pipeServer.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}
