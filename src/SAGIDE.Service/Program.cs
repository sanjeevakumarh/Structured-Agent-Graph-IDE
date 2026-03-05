using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Serilog;
using SAGIDE.Service.Api;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;
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

    var restBearerToken    = builder.Configuration["SAGIDE:RestApi:BearerToken"];
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

    // ── Config singletons ──────────────────────────────────────────────────────
    builder.Services.AddConfiguredSingleton<LoggingConfig>(builder.Configuration, "SAGIDE:Logging");
    builder.Services.AddConfiguredSingleton<AgentLimitsConfig>(builder.Configuration, "SAGIDE:AgentLimits");
    builder.Services.AddConfiguredSingleton<WorkflowPolicyConfig>(builder.Configuration, "SAGIDE:WorkflowPolicy");
    builder.Services.AddSingleton<WorkflowPolicyEngine>();
    builder.Services.AddSingleton(new TaskAffinitiesConfig());

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
    builder.Services.AddSagideRagPipeline(dbPath);

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

    // Bearer-token guard — only active when SAGIDE:RestApi:BearerToken is set.
    // Applies to /api/* paths only; uses constant-time comparison to prevent timing attacks.
    if (!string.IsNullOrEmpty(restBearerToken))
    {
        var expectedBytes = Encoding.UTF8.GetBytes($"Bearer {restBearerToken}");
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var header      = ctx.Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
                var headerBytes = Encoding.UTF8.GetBytes(header);
                if (!CryptographicOperations.FixedTimeEquals(headerBytes, expectedBytes))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"SAGIDE\"";
                    return;
                }
            }
            await next();
        });
    }

    // OpenAPI document — /openapi/v1.json (development only)
    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    // REST API endpoints
    app.MapTaskEndpoints();
    app.MapResultEndpoints();
    app.MapPromptEndpoints();
    app.MapReportsEndpoints(app.Configuration);
    app.MapMetricsEndpoints();
    app.MapModelMetricsEndpoints();
    app.MapSkillsEndpoints();

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
