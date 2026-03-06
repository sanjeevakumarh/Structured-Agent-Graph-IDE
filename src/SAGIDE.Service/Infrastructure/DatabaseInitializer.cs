using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;

namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Runs async database initialization (schema bootstrap, sample pruning) as a hosted service
/// so startup never blocks a thread-pool thread with GetAwaiter().GetResult().
/// Registered inside <see cref="ServiceCollectionExtensions.AddSagidePersistence"/> — before
/// other hosted services — to guarantee tables exist before first use.
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly ITaskRepository _taskRepo;
    private readonly IModelPerfRepository _perfRepo;
    private readonly IModelQualityRepository _qualityRepo;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        ITaskRepository taskRepo,
        IModelPerfRepository perfRepo,
        IModelQualityRepository qualityRepo,
        IConfiguration config,
        ILogger<DatabaseInitializer> logger)
    {
        _taskRepo    = taskRepo;
        _perfRepo    = perfRepo;
        _qualityRepo = qualityRepo;
        _config      = config;
        _logger      = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing databases...");

        // Schema bootstrap — creates all tables (task_history, results, workflows, etc.)
        await _taskRepo.InitializeAsync();

        // Prune old model performance/quality samples
        var perfRetention    = _config.GetValue("SAGIDE:Routing:PerfRetentionDays", 3);
        var qualityRetention = _config.GetValue("SAGIDE:Routing:QualityRetentionDays", 7);
        await _perfRepo.PruneOldSamplesAsync(perfRetention);
        await _qualityRepo.PruneOldSamplesAsync(qualityRetention);

        _logger.LogInformation("Database initialization complete");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
