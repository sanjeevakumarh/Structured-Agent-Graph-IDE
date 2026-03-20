using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Security;

/// <summary>
/// DI + middleware registration for the SAGIDE security module.
///
/// Usage in <c>Program.cs</c>:
/// <code>
///   builder.Services.AddSagideSecurity(builder.Configuration, dbPath);
///   ...
///   app.UseSagideSecurity();
/// </code>
/// </summary>
public static class SecurityExtensions
{
    // ── DI ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <see cref="ISecurityPolicy"/> and <see cref="IAuditLog"/> singletons.
    ///
    /// <see cref="ISecurityPolicy"/>: <see cref="BearerTokenPolicy"/> — active only when
    /// <c>SAGIDE:RestApi:BearerToken</c> is set; passthrough otherwise.
    ///
    /// <see cref="IAuditLog"/>: <see cref="SqliteAuditLog"/> when
    /// <c>SAGIDE:Security:AuditLog:Enabled</c> is true (default); <see cref="NullAuditLog"/>
    /// otherwise.
    /// </summary>
    public static IServiceCollection AddSagideSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        string dbPath)
    {
        // BearerTokenPolicy — disabled (passthrough) when no token is configured
        var token = configuration["SAGIDE:RestApi:BearerToken"];
        services.AddSingleton<ISecurityPolicy>(_ => new BearerTokenPolicy(token));

        // Audit log — SQLite by default, NullAuditLog when disabled
        var auditEnabled = configuration.GetValue("SAGIDE:Security:AuditLog:Enabled", true);
        if (auditEnabled)
        {
            services.AddSingleton<IAuditLog>(sp =>
                new SqliteAuditLog(dbPath, sp.GetRequiredService<ILogger<SqliteAuditLog>>()));
        }
        else
        {
            services.AddSingleton<IAuditLog>(_ => NullAuditLog.Instance);
        }

        return services;
    }

    // ── Middleware ────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds the SAGIDE security middleware to the pipeline.
    ///
    /// Guards all <c>/api/*</c> paths using the registered <see cref="ISecurityPolicy"/>.
    /// Rejected requests are logged via <see cref="IAuditLog.RecordAuthFailureAsync"/>.
    /// </summary>
    public static IApplicationBuilder UseSagideSecurity(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api"))
            {
                await next(ctx);
                return;
            }

            var policy   = ctx.RequestServices.GetRequiredService<ISecurityPolicy>();
            var header   = ctx.Request.Headers.Authorization.FirstOrDefault();

            if (!policy.IsAuthorised(header))
            {
                // Record auth failure fire-and-forget — never block the rejection response
                var auditLog = ctx.RequestServices.GetService<IAuditLog>();
                if (auditLog is not null)
                    _ = auditLog.RecordAuthFailureAsync(
                        ctx.Request.Path.Value ?? "/",
                        ctx.Connection.RemoteIpAddress?.ToString());

                ctx.Response.StatusCode = policy.UnauthorisedStatusCode;
                ctx.Response.Headers.WWWAuthenticate = policy.WwwAuthenticateChallenge;
                return;
            }

            await next(ctx);
        });

        return app;
    }
}
