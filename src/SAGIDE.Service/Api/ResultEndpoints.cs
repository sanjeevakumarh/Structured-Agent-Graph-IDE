using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Api;

internal static class ResultEndpoints
{
    internal static IEndpointRouteBuilder MapResultEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/results?tag={tag}&since={datetime}&limit={limit}&offset={offset}
        // Returns completed/failed tasks with their results inline, filtered by source tag.
        app.MapGet("/api/results", async (
            ITaskRepository repo,
            string? tag, DateTime? since, int limit = 100, int offset = 0) =>
        {
            var effectiveLimit = limit > 0 ? limit : 100;
            var tasks = string.IsNullOrEmpty(tag)
                ? await repo.GetTaskHistoryAsync(effectiveLimit, offset)
                : await repo.GetTasksBySourceTagAsync(tag, effectiveLimit, offset);

            // Optional time filter
            if (since.HasValue)
                tasks = tasks.Where(t => t.CompletedAt >= since.Value).ToList();

            var results = new List<object>();
            foreach (var t in tasks.Where(t =>
                t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed))
            {
                var result = await repo.GetResultAsync(t.Id);
                results.Add(new
                {
                    taskId        = t.Id,
                    sourceTag     = t.SourceTag,
                    agentType     = t.AgentType.ToString(),
                    description   = t.Description,
                    status        = t.Status.ToString(),
                    createdAt     = t.CreatedAt,
                    completedAt   = t.CompletedAt,
                    success       = result?.Success,
                    output        = result?.Output,
                    tokensUsed    = result?.TokensUsed,
                    estimatedCost = result?.EstimatedCost,
                    latencyMs     = result?.LatencyMs,
                    errorMessage  = result?.ErrorMessage,
                });
            }

            return Results.Ok(results);
        });

        // GET /api/results/{taskId} — full AgentResult for a specific task
        app.MapGet("/api/results/{taskId}", async (string taskId, ITaskRepository repo) =>
        {
            var result = await repo.GetResultAsync(taskId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        });

        return app;
    }
}
