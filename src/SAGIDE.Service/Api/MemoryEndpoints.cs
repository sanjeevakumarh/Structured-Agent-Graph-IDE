using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Api;

internal static class MemoryEndpoints
{
    internal static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/memory/project?workspace=<path>
        // Returns all stored project memory facts for the given workspace.
        app.MapGet("/api/memory/project", async (
            IProjectMemory? projectMemory,
            string? workspace,
            CancellationToken ct) =>
        {
            if (projectMemory is null)
                return Results.Ok(new { message = "Project memory not enabled." });

            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "Query parameter 'workspace' is required." });

            var facts = await projectMemory.GetAllAsync(workspace, ct);
            return Results.Ok(new
            {
                workspace,
                count = facts.Count,
                facts
            });
        });

        // PUT /api/memory/project?workspace=<path>
        // Body: { "key": "...", "value": "..." }
        // Upserts a fact for the workspace.
        app.MapPut("/api/memory/project", async (
            IProjectMemory? projectMemory,
            string? workspace,
            MemoryUpsertRequest? body,
            CancellationToken ct) =>
        {
            if (projectMemory is null)
                return Results.Problem("Project memory not enabled.", statusCode: 503);

            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "Query parameter 'workspace' is required." });

            if (body is null || string.IsNullOrWhiteSpace(body.Key))
                return Results.BadRequest(new { error = "Request body with 'key' is required." });

            await projectMemory.SetAsync(workspace, body.Key, body.Value ?? string.Empty, ct);
            return Results.Ok(new { workspace, key = body.Key, stored = true });
        });

        // DELETE /api/memory/project?workspace=<path>&key=<key>
        app.MapDelete("/api/memory/project", async (
            IProjectMemory? projectMemory,
            string? workspace,
            string? key,
            CancellationToken ct) =>
        {
            if (projectMemory is null)
                return Results.Problem("Project memory not enabled.", statusCode: 503);

            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(key))
                return Results.BadRequest(new { error = "Query parameters 'workspace' and 'key' are required." });

            await projectMemory.DeleteAsync(workspace, key, ct);
            return Results.Ok(new { workspace, key, deleted = true });
        });

        return app;
    }

    private sealed record MemoryUpsertRequest(string? Key, string? Value);
}
