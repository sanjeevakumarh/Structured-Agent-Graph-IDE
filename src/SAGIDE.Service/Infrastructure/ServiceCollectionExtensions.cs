using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Agents;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Events;
using SAGIDE.Service.Observability;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Persistence;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Prompts;
using SAGIDE.Service.Rag;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Extension methods that group related DI registrations and keep <c>Program.cs</c> focused
/// on top-level wiring rather than service-by-service registration details.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds a configuration section to a new instance of <typeparamref name="T"/> and registers
    /// it as a singleton. Eliminates the repetitive bind-then-register pattern.
    /// Usage: builder.Services.AddConfiguredSingleton&lt;TimeoutConfig&gt;(configuration, "SAGIDE:Timeouts");
    /// </summary>
    public static IServiceCollection AddConfiguredSingleton<T>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionPath) where T : class, new()
    {
        var instance = new T();
        configuration.GetSection(sectionPath).Bind(instance);
        return services.AddSingleton(instance);
    }

    // ── Persistence ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the SQLite task repository (and its four interface aliases), git services,
    /// dead-letter queue, and result parser.
    /// </summary>
    public static IServiceCollection AddSagidePersistence(
        this IServiceCollection services,
        IConfiguration cfg,
        string dbPath)
    {
        var dlqRetentionDays = cfg.GetValue("SAGIDE:Database:DlqRetentionDays", 7);

        // SqliteTaskRepository bootstraps the schema (InitializeAsync creates all tables).
        // The three sibling repositories each cover one persistence concern and share the same DB file.
        services.AddSingleton<ITaskRepository>(sp =>
        {
            var repo = new SqliteTaskRepository(dbPath, sp.GetRequiredService<ILogger<SqliteTaskRepository>>());
            repo.InitializeAsync().GetAwaiter().GetResult();
            return repo;
        });
        services.AddSingleton<IActivityRepository>(sp =>
            new SqliteActivityRepository(dbPath, sp.GetRequiredService<ILogger<SqliteActivityRepository>>()));
        services.AddSingleton<IWorkflowRepository>(_ =>
            new SqliteWorkflowRepository(dbPath));
        services.AddSingleton<ISchedulerRepository>(_ =>
            new SqliteSchedulerRepository(dbPath));

        // Activity logging
        services.AddSingleton<MarkdownGenerator>();
        services.AddSingleton<ActivityLogger>();
        services.AddSingleton<GitIntegration>();

        // Git auto-commit
        services.AddConfiguredSingleton<GitConfig>(cfg, "SAGIDE:Git");
        services.AddSingleton<GitService>();

        // Dead-letter queue (backed by SQLite)
        services.AddSingleton(sp => new DeadLetterQueue(
            sp.GetRequiredService<ILogger<DeadLetterQueue>>(),
            sp.GetRequiredService<ITaskRepository>(),
            dlqRetentionDays));

        services.AddSingleton<ResultParser>();

        // Model performance tracking — prune old samples on startup
        services.AddSingleton<IModelPerfRepository>(sp =>
        {
            var repo = new SqliteModelPerfRepository(dbPath);
            var retentionDays = cfg.GetValue("SAGIDE:Routing:PerfRetentionDays", 3);
            repo.PruneOldSamplesAsync(retentionDays).GetAwaiter().GetResult();
            return repo;
        });

        // Model quality tracking — prune old samples on startup
        services.AddSingleton<IModelQualityRepository>(sp =>
        {
            var repo = new SqliteModelQualityRepository(dbPath);
            var retentionDays = cfg.GetValue("SAGIDE:Routing:QualityRetentionDays", 7);
            repo.PruneOldSamplesAsync(retentionDays).GetAwaiter().GetResult();
            return repo;
        });

        return services;
    }

    // ── LLM Providers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the Ollama health monitor, constructs all LLM provider instances via
    /// <see cref="ProviderFactory"/>, and registers them as <see cref="IAgentProvider"/> singletons.
    /// Uses a pre-build <see cref="ILoggerFactory"/> because the monitor must start before the DI
    /// container is fully constructed.
    /// </summary>
    public static IServiceCollection AddSagideProviders(
        this IServiceCollection services,
        IConfiguration cfg,
        TimeoutConfig timeoutConfig)
    {
        // Pre-build logger factory so OllamaHostHealthMonitor can log before the container is ready.
        var loggerFactory = LoggerFactory.Create(b => b.AddSerilog());

        // EndpointAliasResolver needs only IConfiguration — safe to create before the container.
        var aliasResolver = new EndpointAliasResolver(cfg);
        services.AddSingleton(aliasResolver);

        var ollamaUrls    = CollectOllamaBaseUrls(cfg);
        var ollamaMonitor = new OllamaHostHealthMonitor(
            ollamaUrls, loggerFactory.CreateLogger<OllamaHostHealthMonitor>(), aliasResolver);
        services.AddSingleton(ollamaMonitor);
        services.AddHostedService(_ => ollamaMonitor);

        var providerFactory = new ProviderFactory(cfg, loggerFactory, timeoutConfig, ollamaMonitor);
        foreach (var provider in providerFactory.GetAllProviders())
            services.AddSingleton(typeof(IAgentProvider), provider);
        services.AddSingleton(providerFactory);

        return services;
    }

    // ── Orchestration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <see cref="AgentOrchestrator"/> (and its <see cref="ITaskSubmissionService"/> alias),
    /// <see cref="WorkflowEngine"/>, <see cref="SubtaskCoordinator"/>, <see cref="PromptRegistry"/>,
    /// and the cron <c>SchedulerService</c> hosted service.
    /// </summary>
    public static IServiceCollection AddSagideOrchestration(
        this IServiceCollection services,
        IConfiguration cfg)
    {
        var maxConcurrent       = cfg.GetValue("SAGIDE:MaxConcurrentAgents", 5);
        var maxTaskHistory      = cfg.GetValue("SAGIDE:Orchestration:MaxTaskHistoryInMemory", 1000);
        var broadcastThrottleMs = cfg.GetValue("SAGIDE:Orchestration:BroadcastThrottleMs", 200);
        var maxFileSizeChars    = cfg.GetValue("SAGIDE:Orchestration:MaxFileSizeChars", 32_000);

        // Configure Scriban step-output truncation limit once at startup.
        PromptTemplate.Configure(cfg.GetValue("SAGIDE:Orchestration:MaxStepOutputChars", 4000));

        services.AddSingleton(new TaskQueue(maxTaskHistory));

        // In-process event bus with per-handler exception isolation
        services.AddSingleton<IEventBus, InProcessEventBus>();

        // Per-provider circuit breaker (Closed → Open → HalfOpen)
        var cbConfig = new Resilience.CircuitBreakerConfig();
        cfg.GetSection("SAGIDE:Resilience:CircuitBreaker").Bind(cbConfig);
        services.AddSingleton(cbConfig);
        services.AddSingleton(sp => new Resilience.CircuitBreakerRegistry(
            sp.GetRequiredService<Resilience.CircuitBreakerConfig>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // Metrics singleton — observable gauge callbacks resolved lazily via sp to avoid circular deps
        services.AddSingleton(sp => new SagideMetrics(
            queueDepth     : () => sp.GetRequiredService<TaskQueue>().PendingCount,
            dlqDepth       : () => sp.GetRequiredService<DeadLetterQueue>().Count,
            activeWorkflows: () => sp.GetService<WorkflowEngine>()?.ActiveInstanceCount ?? 0));

        // PromptBuilder: extracted from AgentOrchestrator.BuildPrompt
        services.AddSingleton(sp => new Providers.PromptBuilder(
            maxFileSizeChars,
            sp.GetRequiredService<ILogger<Providers.PromptBuilder>>()));

        // Register AgentOrchestrator as both its concrete type and the ITaskSubmissionService alias.
        // WorkflowEngine depends on ITaskSubmissionService — this breaks the circular dependency.
        services.AddSingleton(sp => new AgentOrchestrator(
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
            sp.GetRequiredService<LoggingConfig>(),
            sp.GetRequiredService<SagideMetrics>(),
            sp.GetRequiredService<Resilience.CircuitBreakerRegistry>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<Providers.PromptBuilder>(),
            sp.GetService<Providers.OllamaHostHealthMonitor>(),
            sp.GetService<IModelPerfRepository>(),
            sp.GetService<EndpointAliasResolver>(),
            sp.GetService<QualitySampler>()));
        services.AddSingleton<ITaskSubmissionService>(sp => sp.GetRequiredService<AgentOrchestrator>());

        services.AddSingleton<WorkflowDefinitionLoader>();
        services.AddSingleton(sp => new WorkflowEngine(
            sp.GetRequiredService<ITaskSubmissionService>(),
            sp.GetRequiredService<WorkflowDefinitionLoader>(),
            sp.GetRequiredService<AgentLimitsConfig>(),
            sp.GetRequiredService<TaskAffinitiesConfig>(),
            sp.GetRequiredService<WorkflowPolicyEngine>(),
            sp.GetRequiredService<GitService>(),
            sp.GetRequiredService<ILogger<WorkflowEngine>>(),
            sp.GetService<IWorkflowRepository>(),
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ILoggerFactory>()));

        // RoutingConfig + ModelRoutingHints (hints scored from cached perf data)
        var routingConfig = new RoutingConfig();
        cfg.GetSection("SAGIDE:Routing").Bind(routingConfig);
        services.AddSingleton(routingConfig);
        services.AddSingleton<ModelRoutingHints>(sp => new ModelRoutingHints(
            sp.GetService<IModelPerfRepository>(),
            sp.GetRequiredService<EndpointAliasResolver>(),
            sp.GetRequiredService<RoutingConfig>(),
            sp.GetRequiredService<ILogger<ModelRoutingHints>>()));

        // QualitySampler — idle-capacity LLM output quality scoring via Claude Sonnet
        services.AddSingleton<QualitySampler>(sp => new QualitySampler(
            sp.GetRequiredService<RoutingConfig>(),
            sp.GetRequiredService<IModelQualityRepository>(),
            sp.GetRequiredService<ProviderFactory>(),
            sp.GetRequiredService<EndpointAliasResolver>(),
            sp.GetRequiredService<TaskQueue>(),
            sp.GetRequiredService<PromptRegistry>(),
            sp.GetRequiredService<ILogger<QualitySampler>>()));

        services.AddSingleton<SubtaskCoordinator>();
        services.AddSingleton<PromptRegistry>();
        services.AddSingleton<SkillRegistry>();
        services.AddHostedService<Scheduling.SchedulerService>();

        return services;
    }

    // ── Communication ─────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the named-pipe server, message handler, and the
    /// <see cref="Services.ServiceLifetime"/> hosted service.
    /// </summary>
    public static IServiceCollection AddSagideCommunication(
        this IServiceCollection services,
        IConfiguration cfg,
        string pipeName)
    {
        var commConfig = new CommunicationConfig();
        cfg.GetSection("SAGIDE:Communication").Bind(commConfig);
        services.AddSingleton(commConfig);

        services.AddSingleton<MessageHandler>();
        services.AddSingleton(sp => new NamedPipeServer(
            pipeName,
            sp.GetRequiredService<MessageHandler>(),
            sp.GetRequiredService<ILogger<NamedPipeServer>>(),
            sp.GetRequiredService<CommunicationConfig>()));

        services.AddHostedService<Services.ServiceLifetime>();

        return services;
    }

    // ── RAG Pipeline ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers all RAG pipeline components: web fetcher, search adapter, text chunker,
    /// vector store (SQLite), and the pipeline orchestrator.
    /// </summary>
    public static IServiceCollection AddSagideRagPipeline(
        this IServiceCollection services,
        string dbPath)
    {
        services.AddHttpClient<WebFetcher>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SAGIDE/1.0");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect        = true,
            MaxAutomaticRedirections = 10,
        });
        services.AddHttpClient<WebSearchAdapter>();
        services.AddHttpClient<EmbeddingService>();
        services.AddSingleton<TextChunker>();
        services.AddSingleton<VectorStore>(sp =>
        {
            var store = new VectorStore(dbPath, sp.GetRequiredService<ILogger<VectorStore>>());
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });
        services.AddSingleton<RagPipeline>();

        return services;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> CollectOllamaBaseUrls(IConfiguration cfg)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            var url = server["BaseUrl"];
            if (!string.IsNullOrEmpty(url)) urls.Add(url);
        }
        return urls;
    }
}
