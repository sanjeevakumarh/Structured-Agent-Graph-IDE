using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Memory;
using SAGIDE.ModelRouter;
using SAGIDE.Tools;
using SAGIDE.Workflows;
using SAGIDE.Service.Agents;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Communication;
using SAGIDE.Service.Events;
using SAGIDE.Service.Observability;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Persistence;
using SAGIDE.Service.Providers;
using SAGIDE.Contracts;
using SAGIDE.Service.Prompts;
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
        // Async initialization (table creation, pruning) runs in DatabaseInitializer hosted service.
        services.AddSingleton<ITaskRepository>(sp =>
            new SqliteTaskRepository(dbPath, sp.GetRequiredService<ILogger<SqliteTaskRepository>>()));
        services.AddSingleton<IActivityRepository>(sp =>
            new SqliteActivityRepository(dbPath, sp.GetRequiredService<ILogger<SqliteActivityRepository>>()));
        services.AddSingleton<IWorkflowRepository>(_ =>
            new SqliteWorkflowRepository(dbPath));
        services.AddSingleton<ISchedulerRepository>(_ =>
            new SqliteSchedulerRepository(dbPath));
        services.AddSingleton<Core.Interfaces.IProjectMemory>(sp =>
            new SqliteProjectMemory(dbPath, sp.GetRequiredService<ILogger<SqliteProjectMemory>>()));

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

        // Model performance tracking — pruning runs in DatabaseInitializer hosted service
        services.AddSingleton<IModelPerfRepository>(_ => new SqliteModelPerfRepository(dbPath));

        // Model quality tracking — pruning runs in DatabaseInitializer hosted service
        services.AddSingleton<IModelQualityRepository>(_ => new SqliteModelQualityRepository(dbPath));

        // Notes file index — tracks which Logseq files have been indexed
        services.AddSingleton(_ => new NotesFileIndexRepository(dbPath));
        services.AddSingleton<Core.Interfaces.INotesFileIndexRepository>(
            sp => sp.GetRequiredService<NotesFileIndexRepository>());

        // Persistent search cache — survives restarts, quality-gated
        services.AddSingleton(_ => new SearchCacheRepository(dbPath));
        services.AddSingleton<Core.Interfaces.ISearchCacheRepository>(
            sp => sp.GetRequiredService<SearchCacheRepository>());

        // Async DB initialization — registered here so it starts before all other hosted services.
        services.AddHostedService<DatabaseInitializer>();

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

        var healthPollSec = cfg.GetValue("SAGIDE:Caching:HealthPollIntervalSeconds", 30);
        var ollamaUrls    = CollectOllamaBaseUrls(cfg);
        var ollamaMonitor = new OllamaHostHealthMonitor(
            ollamaUrls, loggerFactory.CreateLogger<OllamaHostHealthMonitor>(), aliasResolver,
            healthPollSec);
        services.AddSingleton(ollamaMonitor);
        services.AddHostedService(_ => ollamaMonitor);

        var providerFactory = new ProviderFactory(cfg, loggerFactory, timeoutConfig, ollamaMonitor);
        foreach (var provider in providerFactory.GetAllProviders())
            services.AddSingleton(typeof(IAgentProvider), provider);
        services.AddSingleton(providerFactory);

        // IModelRouter — registered after all IAgentProvider singletons are in the container.
        services.AddSagideModelRouter();

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
        // ICircuitBreakerRegistry alias — lets SAGIDE.ModelRouter query breaker state
        // without a direct reference to SAGIDE.Service.Resilience.
        services.AddSingleton<Core.Interfaces.ICircuitBreakerRegistry>(
            sp => sp.GetRequiredService<Resilience.CircuitBreakerRegistry>());

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
            sp.GetService<QualitySampler>(),
            sp.GetService<CachingConfig>(),
            sp.GetService<IModelRouter>(),
            sp.GetService<IAuditLog>()));
        services.AddSingleton<ITaskSubmissionService>(sp => sp.GetRequiredService<AgentOrchestrator>());

        // IEventBus is already SAGIDE.Core.Events.IEventBus via global using alias.
        // The registration above covers both — no separate alias needed.

        // Workflow step renderer — adapts PromptTemplate.RenderWorkflowStep for injection
        services.AddSingleton<Core.Interfaces.IWorkflowStepRenderer, Prompts.WorkflowStepRenderer>();

        // IWorkflowGitService — GitService already implements it; just register the alias
        services.AddSingleton<Core.Interfaces.IWorkflowGitService>(
            sp => sp.GetRequiredService<GitService>());

        // WorkflowEngine + sub-services — registered via SAGIDE.Workflows module
        services.AddSagideWorkflows(cfg);

        // RoutingConfig + ModelRoutingHints (hints scored from cached perf data)
        var routingConfig = new RoutingConfig();
        cfg.GetSection("SAGIDE:Routing").Bind(routingConfig);
        services.AddSingleton(routingConfig);
        var routingHintsTtl = cfg.GetValue("SAGIDE:Caching:RoutingHintsTtlSeconds", 60);
        services.AddSingleton<ModelRoutingHints>(sp => new ModelRoutingHints(
            sp.GetService<IModelPerfRepository>(),
            sp.GetRequiredService<EndpointAliasResolver>(),
            sp.GetRequiredService<RoutingConfig>(),
            sp.GetRequiredService<ILogger<ModelRoutingHints>>(),
            routingHintsTtl));

        // QualitySampler — idle-capacity LLM output quality scoring via Claude Sonnet
        services.AddSingleton<QualitySampler>(sp => new QualitySampler(
            sp.GetRequiredService<RoutingConfig>(),
            sp.GetRequiredService<IModelQualityRepository>(),
            sp.GetRequiredService<ProviderFactory>(),
            sp.GetRequiredService<EndpointAliasResolver>(),
            sp.GetRequiredService<TaskQueue>(),
            sp.GetRequiredService<IPromptRegistry>(),
            sp.GetRequiredService<ILogger<QualitySampler>>()));

        // Quality scoring — scores LLM outputs using a lightweight model
        var qualityScoringConfig = new QualityScoringConfig();
        cfg.GetSection("SAGIDE:QualityScoring").Bind(qualityScoringConfig);
        services.AddSingleton(qualityScoringConfig);
        services.AddSingleton<QualityScorer>();

        services.AddSingleton<SubtaskCoordinator>();
        // ISubtaskCoordinator alias — callers depend on the interface, not the concrete class
        services.AddSingleton<ISubtaskCoordinator>(sp => sp.GetRequiredService<SubtaskCoordinator>());

        // IWorkflowEngine alias already registered by AddSagideWorkflows()

        // Registries: concrete singletons + interface aliases for DI consumers
        services.AddSingleton<PromptRegistry>();
        services.AddSingleton<IPromptRegistry>(sp => sp.GetRequiredService<PromptRegistry>());
        services.AddSingleton<IPromptRegistrationService>(sp => sp.GetRequiredService<PromptRegistry>());
        services.AddSingleton<SkillRegistry>();
        services.AddSingleton<ISkillRegistry>(sp => sp.GetRequiredService<SkillRegistry>());
        services.AddSingleton<ISkillRegistrationService>(sp => sp.GetRequiredService<SkillRegistry>());
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
        IConfiguration cfg,
        string dbPath)
    {
        // All RAG/memory types registered via the SAGIDE.Memory module
        services.AddSagideMemory(cfg, dbPath);

        // Session memory — transient so each resolved instance is a fresh isolated store
        services.AddTransient<Core.Interfaces.ISessionMemory, InMemorySessionMemory>();

        // IWorkflowStepRenderer and IWorkflowGitService registered in AddSagideOrchestration
        // (before AddSagideWorkflows is called).

        return services;
    }

    // ── Tools ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers <see cref="IToolRegistry"/> populated with the three built-in tools:
    /// <c>web_fetch</c>, <c>web_search</c>, and <c>git</c>.
    ///
    /// Call after <see cref="AddSagideRagPipeline"/> and <see cref="AddSagideSecurity"/>.
    /// </summary>
    public static IServiceCollection AddSagideTools(this IServiceCollection services)
    {
        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new SAGIDE.Tools.InProcessToolRegistry(
                sp.GetRequiredService<ILogger<SAGIDE.Tools.InProcessToolRegistry>>(),
                sp.GetService<IAuditLog>());

            // web_fetch — wraps WebFetcher
            var fetcher = sp.GetRequiredService<WebFetcher>();
            registry.Register(new SAGIDE.Tools.Tools.WebFetchTool(
                async (url, ct) => (await fetcher.FetchUrlAsync(url, ct)).Body));

            // web_search — wraps WebSearchAdapter (optional; may not be configured)
            var searcher = sp.GetService<WebSearchAdapter>();
            if (searcher is not null)
                registry.Register(new SAGIDE.Tools.Tools.WebSearchTool(
                    (query, ct) => searcher.SearchAsync(query, ct: ct)));

            // git — wraps GitService.RunReadOnlyAsync (optional; may not be available)
            var gitService = sp.GetService<GitService>();
            if (gitService is not null)
                registry.Register(new SAGIDE.Tools.Tools.GitTool(
                    (workspace, command, ct) => gitService.RunReadOnlyAsync(workspace, command, ct)));

            return registry;
        });

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
