using SAGIDE.Service.Communication;
using SAGIDE.Service.Observability;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Api;

internal static class MetricsEndpoints
{
    internal static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/metrics — point-in-time counter/gauge snapshot (O2)
        // Returns cumulative counters since startup and current live gauge values.
        app.MapGet("/api/metrics", (
            SagideMetrics   metrics,
            TaskQueue       taskQueue,
            DeadLetterQueue dlq,
            WorkflowEngine  workflows,
            NamedPipeServer pipeServer) =>
        {
            var snapshot = metrics.GetSnapshot(
                queuePending    : taskQueue.PendingCount,
                queueRunning    : taskQueue.RunningCount,
                dlqDepth        : dlq.Count,
                activeWorkflows : workflows.ActiveInstanceCount,
                ipcDropped      : pipeServer.DroppedMessageCount);

            return Results.Ok(new
            {
                tasks = new
                {
                    submitted = snapshot.TasksSubmitted,
                    completed = snapshot.TasksCompleted,
                    failed    = snapshot.TasksFailed,
                    queuePending  = snapshot.QueuePending,
                    queueRunning  = snapshot.QueueRunning,
                },
                llm = new
                {
                    calls          = snapshot.LlmCalls,
                    inputTokens    = snapshot.LlmInputTokens,
                    outputTokens   = snapshot.LlmOutputTokens,
                    avgLatencyMs   = Math.Round(snapshot.LlmAvgLatencyMs, 1),
                },
                dlq = new
                {
                    depth = snapshot.DlqDepth,
                },
                workflows = new
                {
                    active = snapshot.ActiveWorkflows,
                },
                ipc = new
                {
                    droppedMessages = snapshot.IpcDroppedMessages,
                },
                serviceStartedAt = snapshot.ServiceStartedAt,
                sampledAt        = snapshot.SampledAt,
            });
        });

        return app;
    }
}
