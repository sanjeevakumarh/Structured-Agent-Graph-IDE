using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Api;

internal static class AuditEndpoints
{
    internal static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/audit?limit=100 — recent audit trail entries
        app.MapGet("/api/audit", async (IAuditLog? auditLog, int limit = 100, CancellationToken ct = default) =>
        {
            if (auditLog is null)
                return Results.Ok(new { message = "Audit log not enabled." });

            var entries = await auditLog.GetRecentAsync(limit, ct);
            return Results.Ok(new
            {
                generatedAt = DateTime.UtcNow,
                count       = entries.Count,
                entries
            });
        });

        return app;
    }
}
