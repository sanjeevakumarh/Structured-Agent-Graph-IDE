using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Api;

internal static class ToolsEndpoints
{
    internal static IEndpointRouteBuilder MapToolsEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/tools — list all registered tools
        app.MapGet("/api/tools", (IToolRegistry? registry) =>
        {
            if (registry is null)
                return Results.Ok(new { message = "Tool registry not enabled." });

            return Results.Ok(new
            {
                count = registry.All.Count,
                tools = registry.All.Select(t => new
                {
                    name        = t.Name,
                    description = t.Description,
                })
            });
        });

        // POST /api/tools/{name}/execute
        // Body: Dictionary<string,string> of parameters
        // Executes the named tool and returns the result string.
        app.MapPost("/api/tools/{name}/execute", async (
            string name,
            Dictionary<string, string>? parameters,
            IToolRegistry? registry,
            CancellationToken ct) =>
        {
            if (registry is null)
                return Results.Problem("Tool registry not enabled.", statusCode: 503);

            var tool = registry.Get(name);
            if (tool is null)
                return Results.NotFound(new { error = $"Tool '{name}' not found." });

            try
            {
                var result = await registry.ExecuteAsync(name, parameters ?? [], ct);
                return Results.Ok(new { tool = name, result });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
