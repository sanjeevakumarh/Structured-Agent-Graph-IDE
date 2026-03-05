using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Api;

internal static class ModelMetricsEndpoints
{
    internal static IEndpointRouteBuilder MapModelMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/models/performance?modelId=&server=&windowMinutes=60
        // Returns aggregated p50/p95 latency, success rate, and tokens/sec for each
        // (modelId, serverAlias) pair observed in the requested window.
        app.MapGet("/api/models/performance", async (
            IModelPerfRepository? repo,
            string? modelId, string? server, int windowMinutes = 60) =>
        {
            if (repo is null)
                return Results.Ok(new { message = "Performance tracking not enabled. Register IModelPerfRepository." });

            var summaries = await repo.GetSummaryAsync(modelId, server, windowMinutes);
            return Results.Ok(new
            {
                windowMinutes,
                generatedAt = DateTime.UtcNow,
                summaries
            });
        });

        // GET /api/models/quality?modelId=&server=&limit=20
        // Returns the most recent quality samples (scored by Claude Sonnet).
        app.MapGet("/api/models/quality", async (
            IModelQualityRepository? repo,
            string? modelId, string? server, int limit = 20) =>
        {
            if (repo is null)
                return Results.Ok(new { message = "Quality tracking not enabled. Register IModelQualityRepository." });

            var samples = await repo.GetRecentScoresAsync(modelId, server, limit);
            return Results.Ok(new
            {
                generatedAt = DateTime.UtcNow,
                count       = samples.Count,
                samples
            });
        });

        // GET /api/models/routing-hints?taskType=
        // Returns cached performance summaries with computed hint scores per (modelId, serverAlias).
        // Optional taskType filters by substring match against modelId.
        app.MapGet("/api/models/routing-hints", (ModelRoutingHints? hints, string? taskType) =>
        {
            if (hints is null)
                return Results.Ok(new { message = "Routing hints not enabled. Register ModelRoutingHints." });

            var summaries = hints.GetCachedSummaries();
            if (!string.IsNullOrEmpty(taskType))
                summaries = [.. summaries.Where(s =>
                    s.ModelId.Contains(taskType, StringComparison.OrdinalIgnoreCase))];

            return Results.Ok(new
            {
                generatedAt = DateTime.UtcNow,
                count       = summaries.Count,
                summaries
            });
        });

        return app;
    }
}
