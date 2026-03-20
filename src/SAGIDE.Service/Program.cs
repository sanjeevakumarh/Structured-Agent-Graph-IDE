using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Serilog;
using SAGIDE.Observability;
using SAGIDE.Security;
using SAGIDE.Service.Api;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;
using SAGIDE.Core.Models;
using SAGIDE.Service.Routing;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "sagide-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting SAGIDE Service");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog
    builder.Services.AddSerilog();

    // Read top-level config values needed before calling extension methods
    var pipeName = builder.Configuration["SAGIDE:NamedPipeName"] ?? "SAGIDEPipe";

    // ── REST API security ──────────────────────────────────────────────────────
    // Default to loopback-only if no Kestrel endpoint is configured in appsettings.
    if (string.IsNullOrEmpty(builder.Configuration["Kestrel:Endpoints:Http:Url"]))
        builder.WebHost.UseUrls("http://127.0.0.1:5100");

    var rateLimitPerMinute = builder.Configuration.GetValue("SAGIDE:RestApi:RateLimitPerMinute", 300);

    // Per-IP fixed-window rate limiter — applies to all endpoint-routed requests (/api/* and /dashboard).
    // Static files served by UseStaticFiles() short-circuit before endpoint routing and are not affected.
    builder.Services.AddRateLimiter(options =>
    {
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit          = rateLimitPerMinute,
                    Window               = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit           = 0,
                }));
        options.RejectionStatusCode = 429;
    });

    // ── Observability spine ────────────────────────────────────────────────────
    builder.Services.AddSagideObservability(builder.Configuration);

    // ── Config singletons ──────────────────────────────────────────────────────
    builder.Services.AddConfiguredSingleton<LoggingConfig>(builder.Configuration, "SAGIDE:Logging");
    builder.Services.AddConfiguredSingleton<AgentLimitsConfig>(builder.Configuration, "SAGIDE:AgentLimits");
    builder.Services.AddConfiguredSingleton<CachingConfig>(builder.Configuration, "SAGIDE:Caching");
    builder.Services.AddSingleton(new TaskAffinitiesConfig());
    // WorkflowPolicyConfig + WorkflowPolicyEngine registered by AddSagideWorkflows() below

    // TimeoutConfig is also passed directly to AddSagideProviders before the container is built.
    var timeoutConfig = new TimeoutConfig();
    builder.Configuration.GetSection("SAGIDE:Timeouts").Bind(timeoutConfig);
    builder.Services.AddSingleton(timeoutConfig);

    // ── Database path ──────────────────────────────────────────────────────────
    var configuredDbPath = builder.Configuration["SAGIDE:Database:Path"];
    var dbPath = !string.IsNullOrWhiteSpace(configuredDbPath)
        ? configuredDbPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAGIDE", "sagide.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    // ── Service groups ─────────────────────────────────────────────────────────
    builder.Services.AddSagidePersistence(builder.Configuration, dbPath);
    builder.Services.AddSagideProviders(builder.Configuration, timeoutConfig);
    builder.Services.AddSagideOrchestration(builder.Configuration);
    builder.Services.AddSagideCommunication(builder.Configuration, pipeName);
    builder.Services.AddSagideRagPipeline(builder.Configuration, dbPath);
    builder.Services.AddSagideSecurity(builder.Configuration, dbPath);
    builder.Services.AddSagideTools();

    // OpenAPI — JSON schema at /openapi/v1.json; endpoint is gated on development below.
    builder.Services.AddOpenApi();

    var app = builder.Build();

    // Wire routing hints into OllamaProvider — provider is eagerly created before the
    // DI container is built; hints are resolved here and pushed in via SetRoutingHints.
    app.Services.GetRequiredService<ProviderFactory>()
        .SetRoutingHints(app.Services.GetService<ModelRoutingHints>());

    // Serve static dashboard — UseStaticFiles short-circuits before endpoint routing,
    // so these responses bypass the rate limiter and bearer-token guard below.
    app.UseDefaultFiles();
    app.UseStaticFiles();

    // Rate limiting — per-IP fixed window; applies to all endpoint-routed requests.
    app.UseRateLimiter();

    // Security middleware — bearer token guard + audit logging.
    // BearerTokenPolicy is a passthrough when no token is configured.
    app.UseSagideSecurity();

    // OpenAPI document — /openapi/v1.json (all environments; auth required like other API routes)
    app.MapOpenApi();

    // Stamp TraceContext on every /api/* request so all log lines in the handler
    // carry the W3C trace ID without any parameter threading.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var sourceTag = ctx.Request.Headers["X-Source-Tag"].FirstOrDefault() ?? "rest";
            using var _ = SAGIDE.Observability.TraceContext.Start(
                $"{ctx.Request.Method} {ctx.Request.Path}", sourceTag);
            await next();
        }
        else
        {
            await next();
        }
    });

    // REST API endpoints
    app.MapAuditEndpoints();
    app.MapToolsEndpoints();
    app.MapMemoryEndpoints();
    app.MapTaskEndpoints();
    app.MapResultEndpoints();
    app.MapPromptEndpoints();
    app.MapReportsEndpoints(app.Configuration);
    app.MapMetricsEndpoints();
    app.MapModelMetricsEndpoints();
    app.MapSkillsEndpoints();
    app.MapNotesEndpoints();
    app.MapPreflightEndpoints();

    // Redirect /dashboard → / for discoverability
    app.MapGet("/dashboard", () => Results.Redirect("/"));

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application start-up failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
