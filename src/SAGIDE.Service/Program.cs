using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using SAGIDE.Core.Interfaces;
using SAGIDE.Service.Agents;
using SAGIDE.Service.Api;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Persistence;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Infrastructure;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Rag;

using ServiceLifetimeHosted = SAGIDE.Service.Services.ServiceLifetime;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/sagide-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("Starting SAGIDE Service");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Serilog
    builder.Services.AddSerilog();

    // Read config
    var pipeName      = builder.Configuration["SAGIDE:NamedPipeName"] ?? "SAGIDEPipe";
    var maxConcurrent = builder.Configuration.GetValue("SAGIDE:MaxConcurrentAgents", 5);

    // ── REST API security ──────────────────────────────────────────────────────
    // Default to loopback-only if no Kestrel endpoint is configured in appsettings.
    // appsettings.Template.json already sets 127.0.0.1; this is a defence-in-depth fallback.
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

    // Logging configuration — controls what task data appears in log output.
    var loggingConfig = new SAGIDE.Service.Infrastructure.LoggingConfig();
    builder.Configuration.GetSection("SAGIDE:Logging").Bind(loggingConfig);
    builder.Services.AddSingleton(loggingConfig);

    // Bind resilience configs from appsettings.
    // timeoutConfig is kept as a local variable because it is also passed directly to ProviderFactory
    // before the DI container is built.
    var timeoutConfig = new TimeoutConfig();
    builder.Configuration.GetSection("SAGIDE:Timeouts").Bind(timeoutConfig);
    builder.Services.AddSingleton(timeoutConfig);

    // AgentLimitsConfig and WorkflowPolicyConfig are only needed via DI — use the helper.
    builder.Services.AddConfiguredSingleton<AgentLimitsConfig>(builder.Configuration, "SAGIDE:AgentLimits");
    builder.Services.AddConfiguredSingleton<WorkflowPolicyConfig>(builder.Configuration, "SAGIDE:WorkflowPolicy");
    builder.Services.AddSingleton<WorkflowPolicyEngine>();

    builder.Services.AddSingleton(new TaskAffinitiesConfig());

    // IPC / named-pipe configuration (MaxMessageSizeBytes, backoff parameters)
    var commConfig = new CommunicationConfig();
    builder.Configuration.GetSection("SAGIDE:Communication").Bind(commConfig);
    builder.Services.AddSingleton(commConfig);

    // Register SQLite persistence — path configurable via SAGIDE:Database:Path (default: %LOCALAPPDATA%/SAGIDE/sagide.db)
    var configuredDbPath = builder.Configuration["SAGIDE:Database:Path"];
    var dbPath = !string.IsNullOrWhiteSpace(configuredDbPath)
        ? configuredDbPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAGIDE", "sagide.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    builder.Services.AddSingleton<ITaskRepository>(sp =>
    {
        var repo = new SqliteTaskRepository(dbPath, sp.GetRequiredService<ILogger<SqliteTaskRepository>>());
        repo.InitializeAsync().GetAwaiter().GetResult();
        return repo;
    });

    // Wire up the additional interfaces that SqliteTaskRepository implements (same instance)
    builder.Services.AddSingleton<IActivityRepository>(sp =>
        (IActivityRepository)sp.GetRequiredService<ITaskRepository>());
    builder.Services.AddSingleton<IWorkflowRepository>(sp =>
        (IWorkflowRepository)sp.GetRequiredService<ITaskRepository>());
    builder.Services.AddSingleton<ISchedulerRepository>(sp =>
        (ISchedulerRepository)sp.GetRequiredService<ITaskRepository>());

    // Register activity logging services
    builder.Services.AddSingleton<MarkdownGenerator>();
    builder.Services.AddSingleton<ActivityLogger>();
    builder.Services.AddSingleton<GitIntegration>();

    // Register git auto-commit service
    var gitConfig = new GitConfig();
    builder.Configuration.GetSection("SAGIDE:Git").Bind(gitConfig);
    builder.Services.AddSingleton(gitConfig);
    builder.Services.AddSingleton<GitService>();

    // Register dead-letter queue (with persistence) — retention configurable via SAGIDE:Database:DlqRetentionDays
    var dlqRetentionDays = builder.Configuration.GetValue("SAGIDE:Database:DlqRetentionDays", 7);
    builder.Services.AddSingleton(sp => new DeadLetterQueue(
        sp.GetRequiredService<ILogger<DeadLetterQueue>>(),
        sp.GetRequiredService<ITaskRepository>(),
        dlqRetentionDays));

    // Register result parser
    builder.Services.AddSingleton<ResultParser>();

    // Ollama host health monitor — polls /api/ps on each server every 30s
    var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddSerilog());
    var ollamaUrls    = BuildAllOllamaUrls(builder.Configuration);
    var ollamaMonitor = new OllamaHostHealthMonitor(
        ollamaUrls, loggerFactory.CreateLogger<OllamaHostHealthMonitor>());
    builder.Services.AddSingleton(ollamaMonitor);
    builder.Services.AddHostedService(_ => ollamaMonitor);

    // Register providers via factory (with resilience)
    var providerFactory = new ProviderFactory(builder.Configuration, loggerFactory, timeoutConfig, ollamaMonitor);

    foreach (var provider in providerFactory.GetAllProviders())
        builder.Services.AddSingleton(typeof(IAgentProvider), provider);

    builder.Services.AddSingleton(providerFactory);

    // Register orchestrator with all dependencies
    var maxTaskHistory      = builder.Configuration.GetValue("SAGIDE:Orchestration:MaxTaskHistoryInMemory", 1000);
    var broadcastThrottleMs = builder.Configuration.GetValue("SAGIDE:Orchestration:BroadcastThrottleMs", 200);
    var maxFileSizeChars    = builder.Configuration.GetValue("SAGIDE:Orchestration:MaxFileSizeChars", 32_000);
    PromptTemplate.Configure(
        builder.Configuration.GetValue("SAGIDE:Orchestration:MaxStepOutputChars", 4000));
    builder.Services.AddSingleton(new TaskQueue(maxTaskHistory));
    // Register AgentOrchestrator as both its concrete type and the ITaskSubmissionService interface.
    // WorkflowEngine depends on ITaskSubmissionService (not the concrete class) — this breaks
    // the C1 circular dependency without needing post-construction wiring.
    builder.Services.AddSingleton(sp => new AgentOrchestrator(
        sp.GetRequiredService<TaskQueue>(),
        sp,
        sp.GetRequiredService<DeadLetterQueue>(),
        sp.GetRequiredService<TimeoutConfig>(),
        sp.GetRequiredService<AgentLimitsConfig>(),
        sp.GetRequiredService<ResultParser>(),
        sp.GetRequiredService<ILogger<AgentOrchestrator>>(),
        sp.GetRequiredService<ITaskRepository>(),
        sp.GetRequiredService<ActivityLogger>(),
        maxConcurrent,
        sp.GetRequiredService<GitService>(),
        sp.GetRequiredService<GitConfig>(),
        broadcastThrottleMs,
        maxFileSizeChars,
        sp.GetRequiredService<ProviderFactory>(),
        sp.GetRequiredService<SAGIDE.Service.Infrastructure.LoggingConfig>()));
    builder.Services.AddSingleton<ITaskSubmissionService>(sp => sp.GetRequiredService<AgentOrchestrator>());

    // Register workflow engine with all dependencies (Items 1, 3, 4, 6)
    builder.Services.AddSingleton<WorkflowDefinitionLoader>();
    builder.Services.AddSingleton(sp => new WorkflowEngine(
        sp.GetRequiredService<ITaskSubmissionService>(),
        sp.GetRequiredService<WorkflowDefinitionLoader>(),
        sp.GetRequiredService<AgentLimitsConfig>(),
        sp.GetRequiredService<TaskAffinitiesConfig>(),
        sp.GetRequiredService<WorkflowPolicyEngine>(),
        sp.GetRequiredService<GitService>(),
        sp.GetRequiredService<ILogger<WorkflowEngine>>(),
        sp.GetService<IWorkflowRepository>()));

    // Register communication — passes CommunicationConfig for configurable message limits and backoff
    builder.Services.AddSingleton<MessageHandler>();
    builder.Services.AddSingleton(sp => new NamedPipeServer(
        pipeName,
        sp.GetRequiredService<MessageHandler>(),
        sp.GetRequiredService<ILogger<NamedPipeServer>>(),
        sp.GetRequiredService<CommunicationConfig>()));

    // Register hosted service
    builder.Services.AddHostedService<ServiceLifetimeHosted>();

    // Subtask Coordinator (multi-model prompt dispatch + @machine routing)
    builder.Services.AddSingleton<SubtaskCoordinator>();

    // Prompt Registry (singleton with file-watcher hot-reload)
    builder.Services.AddSingleton<PromptRegistry>();

    // Scheduler Service (cron-based task submission from prompt YAMLs)
    builder.Services.AddHostedService<SAGIDE.Service.Scheduling.SchedulerService>();

    // RAG Pipeline
    builder.Services.AddHttpClient<WebFetcher>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SAGIDE/1.0");
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
    });
    builder.Services.AddHttpClient<WebSearchAdapter>();
    builder.Services.AddHttpClient<EmbeddingService>();
    builder.Services.AddSingleton<TextChunker>();
    builder.Services.AddSingleton<VectorStore>(sp =>
    {
        var store = new VectorStore(dbPath, sp.GetRequiredService<ILogger<VectorStore>>());
        store.InitializeAsync().GetAwaiter().GetResult();
        return store;
    });
    builder.Services.AddSingleton<RagPipeline>();

    var app = builder.Build();

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

    // REST API endpoints
    app.MapTaskEndpoints();
    app.MapResultEndpoints();
    app.MapPromptEndpoints();
    app.MapReportsEndpoints(app.Configuration);

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

// ── Local helpers ──────────────────────────────────────────────────────────────

/// <summary>
/// Collects all Ollama base URLs from SAGIDE:Ollama:Servers for the health monitor to track.
/// </summary>
static IEnumerable<string> BuildAllOllamaUrls(IConfiguration cfg)
{
    var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var server in cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren())
    {
        var url = server["BaseUrl"];
        if (!string.IsNullOrEmpty(url)) urls.Add(url);
    }

    return urls;
}
