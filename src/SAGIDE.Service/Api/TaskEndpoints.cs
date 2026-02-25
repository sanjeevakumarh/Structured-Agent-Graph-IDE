using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Api;

internal static class TaskEndpoints
{
    internal static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/health — quick liveness check
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status  = "ok",
            service = "SAGExtension",
            utc     = DateTime.UtcNow,
        }));

        // POST /api/tasks — submit a task from any frontend
        app.MapPost("/api/tasks", async (SubmitTaskRequest request, AgentOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Description))
                return Results.BadRequest(new { error = "Task description is required" });

            var task = new AgentTask
            {
                AgentType         = request.AgentType,
                ModelProvider     = request.ModelProvider,
                ModelId           = request.ModelId,
                Description       = request.Description,
                FilePaths         = request.FilePaths,
                Priority          = request.Priority,
                Metadata          = request.Metadata ?? [],
                ScheduledFor      = request.ScheduledFor,
                ComparisonGroupId = request.ComparisonGroupId,
                SourceTag         = request.SourceTag,
            };

            if (!string.IsNullOrEmpty(request.ModelEndpoint))
                task.Metadata["modelEndpoint"] = request.ModelEndpoint;

            var taskId = await orchestrator.SubmitTaskAsync(task, ct);
            return Results.Created($"/api/tasks/{taskId}", new { taskId, sourceTag = task.SourceTag });
        });

        // GET /api/tasks?tag={tag}&status={status}&limit={limit}&offset={offset}
        app.MapGet("/api/tasks", async (
            ITaskRepository repo,
            string? tag, string? status, int limit = 100, int offset = 0) =>
        {
            var effectiveLimit = limit > 0 ? limit : 100;

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<AgentTaskStatus>(status, true, out var parsedStatus))
            {
                var byStatus = await repo.GetTasksByStatusAsync(parsedStatus);
                var filtered = string.IsNullOrEmpty(tag)
                    ? byStatus
                    : (IReadOnlyList<AgentTask>)byStatus.Where(t => t.SourceTag == tag).ToList();
                return Results.Ok(filtered);
            }

            var tasks = string.IsNullOrEmpty(tag)
                ? await repo.GetTaskHistoryAsync(effectiveLimit, offset)
                : await repo.GetTasksBySourceTagAsync(tag, effectiveLimit, offset);
            return Results.Ok(tasks);
        });

        // GET /api/tasks/{id}
        app.MapGet("/api/tasks/{id}", (string id, AgentOrchestrator orchestrator) =>
        {
            var taskStatus = orchestrator.GetTaskStatus(id);
            return taskStatus is not null ? Results.Ok(taskStatus) : Results.NotFound();
        });

        // DELETE /api/tasks/{id} — cancel
        app.MapDelete("/api/tasks/{id}", async (string id, AgentOrchestrator orchestrator, CancellationToken ct) =>
        {
            await orchestrator.CancelTaskAsync(id, ct);
            return Results.Ok(new { cancelled = true, taskId = id });
        });

        return app;
    }
}
