using Cronos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Orchestrator;
using SAGIDE.Service.Prompts;

namespace SAGIDE.Service.Scheduling;

/// <summary>
/// Background service that reads all prompt definitions with a <c>schedule</c> cron field
/// and submits tasks at the correct times. Ticks once per minute.
/// Prompts with subtasks are dispatched via <see cref="SubtaskCoordinator"/>.
/// Last-fired timestamps are persisted to SQLite so restarts don't re-fire within the same window.
/// </summary>
public sealed class SchedulerService : BackgroundService
{
    private readonly PromptRegistry _registry;
    private readonly ITaskSubmissionService _taskSubmission;
    private readonly SubtaskCoordinator _coordinator;
    private readonly ISchedulerRepository _schedulerRepo;
    private readonly bool _enabled;
    private readonly ILogger<SchedulerService> _logger;

    // In-memory cache of last-fire times; loaded from SQLite on startup
    private readonly Dictionary<string, DateTimeOffset> _lastFired = [];

    public SchedulerService(
        PromptRegistry registry,
        ITaskSubmissionService taskSubmission,
        SubtaskCoordinator coordinator,
        ISchedulerRepository schedulerRepo,
        IConfiguration configuration,
        ILogger<SchedulerService> logger)
    {
        _registry       = registry;
        _taskSubmission = taskSubmission;
        _coordinator    = coordinator;
        _schedulerRepo  = schedulerRepo;
        _enabled        = configuration.GetValue("SAGIDE:Scheduler:Enabled", true);
        _logger         = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Scheduler disabled via configuration");
            return;
        }

        // Restore persisted last-fired timestamps so we don't re-fire after a restart
        try
        {
            await _schedulerRepo.LoadAllLastFiredAsync(_lastFired);
            _logger.LogInformation("Scheduler loaded {N} persisted last-fired entries", _lastFired.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load scheduler state from database — starting fresh");
        }

        _logger.LogInformation("Scheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Scheduler tick error");
            }

            // Wait until the start of the next minute
            var now   = DateTimeOffset.UtcNow;
            var next  = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
            var delay = next - now;
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private Task TickAsync(CancellationToken ct)
    {
        var now       = DateTimeOffset.UtcNow;
        var scheduled = _registry.GetScheduled();

        foreach (var prompt in scheduled)
        {
            if (string.IsNullOrWhiteSpace(prompt.Schedule)) continue;

            CronExpression cron;
            try
            {
                cron = CronExpression.Parse(prompt.Schedule, CronFormat.Standard);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid cron expression '{Expr}' for prompt {Domain}/{Name}",
                    prompt.Schedule, prompt.Domain, prompt.Name);
                continue;
            }

            var key      = $"{prompt.Domain}/{prompt.Name}";
            var lastFire = _lastFired.GetValueOrDefault(key, DateTimeOffset.MinValue);

            // Find the most recent occurrence at or before now
            var occurrence = cron.GetNextOccurrence(now.AddMinutes(-1), TimeZoneInfo.Utc);
            if (occurrence is null || occurrence > now) continue;

            // Already fired this occurrence
            if (lastFire >= occurrence) continue;

            _lastFired[key] = now;

            // Persist immediately so a restart won't re-fire this occurrence
            _ = _schedulerRepo.SetLastFiredAtAsync(key, now)
                .ContinueWith(t => _logger.LogWarning(t.Exception, "Failed to persist scheduler state for {Key}", key),
                    TaskContinuationOptions.OnlyOnFaulted);

            _logger.LogInformation("Scheduler firing: {Domain}/{Name} (schedule: {Schedule})",
                prompt.Domain, prompt.Name, prompt.Schedule);

            // Fire-and-forget; exceptions are caught inside FirePromptAsync
            _ = FirePromptAsync(prompt, ct);
        }

        return Task.CompletedTask;
    }

    private async Task FirePromptAsync(PromptDefinition prompt, CancellationToken ct)
    {
        try
        {
            // Check inline subtasks OR objects/workflow declarations (WorkflowExpander runs inside RunAsync).
            if (prompt.Subtasks.Count > 0 || prompt.Objects.Count > 0 || prompt.DataCollection?.Steps.Count > 0)
            {
                // Multi-model prompt: use SubtaskCoordinator
                _logger.LogInformation(
                    "Scheduler delegating {Domain}/{Name} to SubtaskCoordinator ({N} subtasks / {O} objects)",
                    prompt.Domain, prompt.Name, prompt.Subtasks.Count, prompt.Objects.Count);
                await _coordinator.RunAsync(prompt, variableOverrides: null, ct);
            }
            else
            {
                // Single-model prompt: submit a simple AgentTask
                await SubmitSingleTaskAsync(prompt, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler failed to fire {Domain}/{Name}", prompt.Domain, prompt.Name);
        }
    }

    private async Task SubmitSingleTaskAsync(PromptDefinition prompt, CancellationToken ct)
    {
        var modelId      = prompt.ModelPreference?.Primary
                        ?? prompt.ModelPreference?.Orchestrator
                        ?? string.Empty;
        var providerStr  = ModelIdParser.ParseProvider(modelId);
        var cleanModelId = ModelIdParser.StripPrefix(modelId);

        var task = new AgentTask
        {
            AgentType     = AgentType.Generic,
            ModelProvider = providerStr,
            ModelId       = cleanModelId,
            Description   = $"[Scheduled] {prompt.Domain}/{prompt.Name}: {prompt.Description?.Trim() ?? string.Empty}",
            SourceTag     = prompt.SourceTag ?? $"scheduled_{prompt.Domain}",
            Priority      = 0,
            Metadata      = new Dictionary<string, string>
            {
                ["prompt_domain"] = prompt.Domain,
                ["prompt_name"]   = prompt.Name,
                ["triggered_by"]  = "scheduler",
            },
        };

        var taskId = await _taskSubmission.SubmitTaskAsync(task, ct);
        _logger.LogInformation("Scheduler submitted task {TaskId} for {Domain}/{Name}",
            taskId, prompt.Domain, prompt.Name);
    }

}
