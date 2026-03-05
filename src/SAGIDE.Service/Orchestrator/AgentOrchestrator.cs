using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Agents;
using SAGIDE.Service.Events;
using SAGIDE.Service.Observability;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.ActivityLogging;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Orchestrator;

public class AgentOrchestrator : ITaskSubmissionService
{
    private readonly TaskQueue _taskQueue;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<ModelProvider, IAgentProvider> _providers;
    private readonly DeadLetterQueue _deadLetterQueue;
    private readonly TimeoutConfig _timeoutConfig;
    private readonly AgentLimitsConfig _agentLimitsConfig;
    private readonly ITaskRepository? _repository;
    private readonly ResultParser _resultParser;
    private readonly ActivityLogger? _activityLogger;
    private readonly Infrastructure.GitService? _gitService;
    private readonly Infrastructure.GitConfig? _gitConfig;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly int _broadcastThrottleMs;
    private readonly Infrastructure.LoggingConfig _loggingConfig;
    private readonly SagideMetrics? _metrics;
    private readonly CircuitBreakerRegistry? _circuitBreakerRegistry;
    private readonly IEventBus _eventBus;
    private readonly Providers.PromptBuilder _promptBuilder;
    private readonly Providers.OllamaHostHealthMonitor? _ollamaMonitor;
    private readonly IModelPerfRepository? _perfRepo;
    private readonly EndpointAliasResolver? _aliasResolver;
    private readonly Routing.QualitySampler? _qualitySampler;
    private CancellationTokenSource? _processingCts;

    // ── Ollama failover constants ──────────────────────────────────────────────
    /// <summary>
    /// Maximum number of host-failover attempts before a task is sent to the DLQ.
    /// Each attempt tries a different healthy Ollama server; backoff applies when
    /// no alternative is reachable.
    /// </summary>
    private const int MaxFailoverAttempts = 5;
    /// <summary>
    /// Maximum number of back-pressure retries (HTTP 429/503) before escalating
    /// to a host failover.
    /// </summary>
    private const int MaxBusyRetries = 10;

    /// <summary>
    /// Resolves when StartProcessingAsync has finished loading persisted tasks from the database.
    /// Await this before starting workflow recovery to guarantee ordering.
    /// </summary>
    private readonly TaskCompletionSource _initCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task InitializationCompleted => _initCompleted.Task;

    public int MaxConcurrentTasks { get; }

    public AgentOrchestrator(
        TaskQueue taskQueue,
        IServiceProvider serviceProvider,
        DeadLetterQueue deadLetterQueue,
        TimeoutConfig timeoutConfig,
        AgentLimitsConfig agentLimitsConfig,
        ResultParser resultParser,
        ILogger<AgentOrchestrator> logger,
        ITaskRepository? repository = null,
        ActivityLogger? activityLogger = null,
        int maxConcurrentTasks = 5,
        Infrastructure.GitService? gitService = null,
        Infrastructure.GitConfig? gitConfig = null,
        int broadcastThrottleMs = 200,
        int maxFileSizeChars = 32_000,
        Providers.ProviderFactory? providerFactory = null,
        Infrastructure.LoggingConfig? loggingConfig = null,
        SagideMetrics? metrics = null,
        CircuitBreakerRegistry? circuitBreakerRegistry = null,
        IEventBus? eventBus = null,
        Providers.PromptBuilder? promptBuilder = null,
        Providers.OllamaHostHealthMonitor? ollamaMonitor = null,
        IModelPerfRepository? perfRepo = null,
        EndpointAliasResolver? aliasResolver = null,
        Routing.QualitySampler? qualitySampler = null)
    {
        _taskQueue = taskQueue;
        _serviceProvider = serviceProvider;
        // Cache providers at construction so task execution never calls back into the DI container.
        // Falls back to service-locator pattern if providerFactory is not supplied (e.g. tests).
        _providers = providerFactory is not null
            ? providerFactory.GetAllProviders().ToDictionary(p => p.Provider)
            : serviceProvider.GetServices<IAgentProvider>().ToDictionary(p => p.Provider);
        _deadLetterQueue = deadLetterQueue;
        _timeoutConfig = timeoutConfig;
        _agentLimitsConfig = agentLimitsConfig;
        _resultParser = resultParser;
        _logger = logger;
        _repository = repository;
        _activityLogger = activityLogger;
        _gitService = gitService;
        _gitConfig = gitConfig;
        MaxConcurrentTasks = maxConcurrentTasks;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrentTasks, maxConcurrentTasks);
        _broadcastThrottleMs = broadcastThrottleMs;
        _promptBuilder       = promptBuilder ?? new Providers.PromptBuilder(maxFileSizeChars);
        _ollamaMonitor       = ollamaMonitor;
        _perfRepo            = perfRepo;
        _aliasResolver       = aliasResolver;
        _qualitySampler      = qualitySampler;
        _loggingConfig           = loggingConfig ?? new Infrastructure.LoggingConfig();
        _metrics                 = metrics;
        _circuitBreakerRegistry  = circuitBreakerRegistry;
        _eventBus                = eventBus ?? new NullEventBus();
    }

    // Idempotent — duplicate task ID returns existing, no re-execution
    public async Task<string> SubmitTaskAsync(AgentTask task, CancellationToken ct)
    {
        var existing = _taskQueue.GetTask(task.Id);
        if (existing is not null)
        {
            _logger.LogDebug("Task {TaskId} already exists (idempotent), returning existing", task.Id);
            return existing.Id;
        }

        var taskId = _taskQueue.Enqueue(task);
        _metrics?.RecordTaskSubmitted();
        _logger.LogInformation(
            "Task {TaskId} queued: {AgentType} on {ModelProvider}/{ModelId}",
            taskId, task.AgentType, task.ModelProvider, task.ModelId);
        _logger.LogDebug(
            "Task {TaskId} description: {Description} | metadata: {Metadata}",
            taskId,
            _loggingConfig.SanitizeDescription(task.Description),
            _loggingConfig.SanitizeMetadata(task.Metadata));

        BroadcastUpdate(task);
        await PersistTaskAsync(task);

        // Log activity if workspace path is provided
        if (_activityLogger != null && task.Metadata.TryGetValue("workspacePath", out var workspacePath))
        {
            await _activityLogger.LogActivityAsync(new ActivityEntry
            {
                WorkspacePath = workspacePath,
                Timestamp = task.CreatedAt,
                HourBucket = ActivityLogger.GetHourBucket(task.CreatedAt),
                ActivityType = ActivityType.AgentTask,
                Actor = "agent",
                Summary = $"Task queued: {task.AgentType} - {task.Description}",
                TaskId = task.Id,
                FilePaths = task.FilePaths,
                Metadata = new Dictionary<string, string>
                {
                    ["agentType"] = task.AgentType.ToString(),
                    ["modelProvider"] = task.ModelProvider.ToString(),
                    ["modelId"] = task.ModelId,
                    ["status"] = "queued"
                }
            }, ct);
        }

        return taskId;
    }

    public async Task StartProcessingAsync(CancellationToken ct)
    {
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Load DLQ from database and purge expired
        await _deadLetterQueue.LoadFromStoreAsync();
        _deadLetterQueue.PurgeExpired();

        // Reload persisted pending/queued tasks from the database (crash recovery + offline queue)
        if (_repository is not null)
        {
            var pending = await _repository.LoadPendingTasksAsync();
            foreach (var t in pending)
            {
                if (_taskQueue.GetTask(t.Id) is null)
                    _taskQueue.Enqueue(t);
            }
            if (pending.Count > 0)
                _logger.LogInformation("Reloaded {Count} pending tasks from database", pending.Count);
        }

        // Prune any stale git worktrees left from a previous crash
        if (_gitService is not null)
            await _gitService.PruneStaleWorktreesAsync();

        _logger.LogInformation("Orchestrator started (max {Max} concurrent, DLQ: {DlqCount} entries)",
            MaxConcurrentTasks, _deadLetterQueue.Count);

        // Signal that initialization is complete; workflow recovery may now begin safely.
        _initCompleted.TrySetResult();

        while (!_processingCts.Token.IsCancellationRequested)
        {
            await _concurrencyLimiter.WaitAsync(_processingCts.Token);

            var (task, retryAfter) = _taskQueue.DequeueOrGetDelay();
            if (task is null)
            {
                _concurrencyLimiter.Release();
                // If tasks are scheduled for the future, wait until the next one is due
                var delay = retryAfter ?? TimeSpan.FromMilliseconds(100);
                await Task.Delay(delay, _processingCts.Token);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_processingCts.Token);
            _runningTasks[task.Id] = cts;

            _ = ExecuteTaskAsync(task, cts.Token);
        }
    }

    private async Task ExecuteTaskAsync(AgentTask task, CancellationToken ct)
    {
        int retryCount = 0;
        bool providerCallAttempted = false;
        ProviderCircuitBreaker? circuitBreaker = null;

        using var _scope = _logger.BeginScope(new { TaskId = task.Id, Provider = task.ModelProvider.ToString(), SourceTag = task.SourceTag });

        try
        {
            task.Status = AgentTaskStatus.Running;
            task.StartedAt = DateTime.UtcNow;
            BroadcastUpdate(task);
            await PersistTaskAsync(task);

            // Task-level execution timeout
            using var taskTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            taskTimeoutCts.CancelAfter(_timeoutConfig.TaskExecutionTimeout);
            var taskCt = taskTimeoutCts.Token;

            // Resolve the agent provider from the pre-built cache
            _providers.TryGetValue(task.ModelProvider, out var provider);

            if (provider is null)
            {
                await FailTask(task, $"No provider found for {task.ModelProvider}", "NO_PROVIDER", 0);
                return;
            }

            if (string.IsNullOrWhiteSpace(task.ModelId))
            {
                await FailTask(task, $"Task has empty ModelId for provider {task.ModelProvider}; cannot execute", "EMPTY_MODEL_ID", 0);
                return;
            }

            // Circuit breaker — reject immediately if the provider is known-bad.
            circuitBreaker = _circuitBreakerRegistry?.GetBreaker(task.ModelProvider);
            if (circuitBreaker is not null && !circuitBreaker.IsCallPermitted())
            {
                var resetIn = circuitBreaker.SecondsUntilReset;
                var msg = resetIn > 0
                    ? $"Circuit breaker open for {task.ModelProvider} — provider unavailable, resets in {resetIn:F0}s"
                    : $"Circuit breaker open for {task.ModelProvider} — provider unavailable";
                _logger.LogWarning("Task {TaskId}: {Message}", task.Id, msg);
                await FailTask(task, msg, "CIRCUIT_OPEN", 0);
                return;
            }

            // Max iteration support — each iteration feeds the previous result back into the
            // prompt so the agent can self-refine.  The default limit is 1 for most agent types,
            // making the loop effectively single-shot.  Refactoring agents default to 5.
            var maxIterations = _agentLimitsConfig.GetMaxIterations(task.AgentType);
            var basePrompt = await _promptBuilder.BuildAsync(task);
            var prompt = basePrompt;
            task.Metadata.TryGetValue("modelEndpoint", out var modelEndpointOverride);

            // Strip @machine suffix from ModelId if present (e.g. "deepseek-coder-v2:16b@workstation"
            // → "deepseek-coder-v2:16b").  The suffix is used by WorkflowEngine / SubtaskCoordinator
            // for endpoint routing but must not be forwarded to the Ollama API as part of the model name.
            var bareModelId = StripMachineSuffix(task.ModelId);

            var modelConfig = new ModelConfig(task.ModelProvider, bareModelId,
                Endpoint: string.IsNullOrEmpty(modelEndpointOverride) ? null : modelEndpointOverride);
            string lastResponse = "";

            // Determinism — check output cache before calling the model.
            // Cache key: SHA-256 of (prompt + modelId + provider) — sufficient for replay uniqueness.
            var cacheKey = ComputeCacheKey(basePrompt, task.ModelId, task.ModelProvider.ToString());
            if (!task.ForceRerun && _repository is not null)
            {
                var cached = await _repository.GetCachedOutputAsync(cacheKey);
                if (cached is not null)
                {
                    _logger.LogInformation(
                        "Task {TaskId}: cache hit for model {Model} — skipping LLM call",
                        task.Id, task.ModelId);
                    lastResponse = cached;
                    task.Metadata["response"]    = lastResponse;
                    task.Metadata["cacheHit"]    = "true";
                    task.Metadata["issueCount"]  = "0";
                    task.Metadata["changeCount"] = "0";
                    task.Metadata["issuesJson"]  = "[]";
                    task.Metadata["changesJson"] = "[]";
                    // Re-parse the cached output so issues/changes are correctly surfaced
                    var cachedResult = _resultParser.Parse(task.Id, task.AgentType, lastResponse, 0);
                    task.Metadata["issueCount"]  = cachedResult.Issues.Count.ToString();
                    task.Metadata["changeCount"] = cachedResult.Changes.Count.ToString();
                    task.Metadata["issuesJson"]  = System.Text.Json.JsonSerializer.Serialize(cachedResult.Issues);
                    task.Metadata["changesJson"] = System.Text.Json.JsonSerializer.Serialize(cachedResult.Changes);

                    task.Progress = 100;
                    task.Status = AgentTaskStatus.Completed;
                    task.StatusMessage = "Done (cached)";
                    task.CompletedAt = DateTime.UtcNow;
                    BroadcastUpdate(task);
                    // Atomic: task status + result in one transaction
                    await PersistCompletedAsync(task, cachedResult);

                    if (task.Metadata.ContainsKey("workflowInstanceId"))
                        _ = _serviceProvider.GetService<WorkflowEngine>()?.OnTaskUpdateAsync(ToResponse(task));
                    return;
                }
            }

            // Circuit breaker — mark that we are about to call the provider.
            // RecordSuccess / RecordFailure only fire when a real provider call was made.
            providerCallAttempted = true;

            AgentResult? lastResult = null;
            for (int iteration = 1; iteration <= maxIterations; iteration++)
            {
                taskCt.ThrowIfCancellationRequested();

                // On subsequent iterations, inject the previous output as refinement context.
                if (iteration > 1)
                    prompt = $"[Iteration {iteration}/{maxIterations}] Your previous output:\n\n{lastResponse}\n\n" +
                             $"Please refine and improve your answer. Original task:\n\n{basePrompt}";

                // Start at a low value so progress honestly reflects work not yet done.
                // (iteration-1)/max * 75 spaces out multi-iteration progress; +5 avoids 0%.
                task.Progress = (int)(((double)(iteration - 1) / maxIterations) * 75) + 5;
                task.StatusMessage = maxIterations > 1
                    ? $"Iteration {iteration}/{maxIterations} — sending to {task.ModelProvider}..."
                    : $"Sending to {task.ModelProvider}...";
                task.Metadata["currentIteration"] = iteration.ToString();
                task.Metadata["maxIterations"] = maxIterations.ToString();
                BroadcastUpdate(task);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                lastResponse = await StreamToStringAsync(provider, prompt, modelConfig, task, taskCt);
                sw.Stop();
                _metrics?.RecordLlmCall(sw.ElapsedMilliseconds, provider.LastInputTokens, provider.LastOutputTokens);

                // Persist per-call performance sample (fire-and-forget — never blocks task execution)
                if (_perfRepo is not null)
                {
                    var endpoint = modelConfig.Endpoint
                        ?? task.Metadata.GetValueOrDefault("modelEndpoint", "");
                    _ = _perfRepo.InsertSampleAsync(new ModelPerfSample(
                        Id:           Guid.NewGuid().ToString("N")[..16],
                        Provider:     task.ModelProvider.ToString(),
                        ModelId:      task.ModelId,
                        ServerAlias:  _aliasResolver?.GetAlias(endpoint) ?? "unknown",
                        StartedAt:    task.StartedAt ?? DateTime.UtcNow,
                        LatencyMs:    sw.ElapsedMilliseconds,
                        TokensInput:  provider.LastInputTokens,
                        TokensOutput: provider.LastOutputTokens,
                        Status:       "success",
                        ErrorCode:    null));
                }

                // After streaming completes, show a brief "Parsing" state before the final 100%.
                task.Progress = (int)((double)iteration / maxIterations * 80);
                task.StatusMessage = "Parsing results...";
                BroadcastUpdate(task);

                task.Metadata["latencyMs"] = sw.ElapsedMilliseconds.ToString();

                _logger.LogInformation(
                    "Task {TaskId} iteration {Iteration}/{Max} completed in {LatencyMs}ms",
                    task.Id, iteration, maxIterations, sw.ElapsedMilliseconds);

                // Parse the response into structured result
                var latency = sw.ElapsedMilliseconds;
                lastResult = _resultParser.Parse(task.Id, task.AgentType, lastResponse, latency);

                // Store parsed response in metadata for the extension to consume.
                // issuesJson / changesJson carry the full structured lists so ToResponse()
                // can reconstruct them without an additional repository round-trip.
                task.Metadata["response"]    = lastResponse;
                task.Metadata["issueCount"]  = lastResult.Issues.Count.ToString();
                task.Metadata["changeCount"] = lastResult.Changes.Count.ToString();
                task.Metadata["issuesJson"]  = System.Text.Json.JsonSerializer.Serialize(lastResult.Issues);
                task.Metadata["changesJson"] = System.Text.Json.JsonSerializer.Serialize(lastResult.Changes);

                // Persist intermediate result so partial state is visible during multi-iteration runs.
                // The final atomic write below will overwrite with the same data for the last iteration.
                await PersistResultAsync(lastResult);

                // Stop early if the agent reported no issues — further iterations won't help.
                if (lastResult.Issues.Count == 0)
                    break;
            }

            // Provider call succeeded — close the circuit (or confirm HalfOpen probe).
            circuitBreaker?.RecordSuccess();

            task.Progress = 100;
            task.Status = AgentTaskStatus.Completed;
            task.StatusMessage = "Done";
            task.CompletedAt = DateTime.UtcNow;
            _metrics?.RecordTaskCompleted();
            BroadcastUpdate(task);
            // Atomic: task Completed status + final result in one transaction
            await PersistCompletedAsync(task, lastResult!);

            // Quality sampling — score model output via Claude (fire-and-forget, non-fatal)
            // All safety guards (feature flag, allowlist, token budget) are checked inside.
            _qualitySampler?.Trigger(task, prompt, lastResponse ?? "");

            // Determinism — store final output in cache (fire-and-forget, non-fatal)
            if (_repository is not null && !string.IsNullOrEmpty(lastResponse))
                _ = _repository.StoreCachedOutputAsync(cacheKey, lastResponse, task.ModelId)
                    .ContinueWith(t => _logger.LogWarning(t.Exception, "Cache store failed for task {TaskId}", task.Id),
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

            // Notify workflow engine so the DAG can advance to the next step
            if (task.Metadata.ContainsKey("workflowInstanceId"))
                _ = _serviceProvider.GetService<WorkflowEngine>()?.OnTaskUpdateAsync(ToResponse(task));

            // Git auto-commit: write result to sag-agent-log branch (fire-and-forget, non-fatal)
            if (_gitConfig?.AutoCommitResults == true && _gitService?.IsAvailable == true
                && task.Metadata.TryGetValue("workspacePath", out var wpForGit)
                && task.Metadata.TryGetValue("response", out var responseForGit))
            {
                _ = _gitService.CommitTaskResultAsync(
                    wpForGit, task.Id, task.AgentType.ToString(),
                    task.Description, task.ModelId, responseForGit,
                    _gitConfig.Branch, ct);
            }

            // Log activity for task completion
            if (_activityLogger != null && task.Metadata.TryGetValue("workspacePath", out var workspacePath))
            {
                var result = _repository != null ? await _repository.GetResultAsync(task.Id) : null;
                var details = result != null ? System.Text.Json.JsonSerializer.Serialize(result) : null;

                await _activityLogger.LogActivityAsync(new ActivityEntry
                {
                    WorkspacePath = workspacePath,
                    Timestamp = task.CompletedAt.Value,
                    HourBucket = ActivityLogger.GetHourBucket(task.CompletedAt.Value),
                    ActivityType = ActivityType.AgentTask,
                    Actor = "agent",
                    Summary = $"Task completed: {task.AgentType} - {task.Description}",
                    Details = details,
                    TaskId = task.Id,
                    FilePaths = task.FilePaths,
                    Metadata = new Dictionary<string, string>
                    {
                        ["agentType"] = task.AgentType.ToString(),
                        ["modelProvider"] = task.ModelProvider.ToString(),
                        ["modelId"] = task.ModelId,
                        ["status"] = "completed",
                        ["latencyMs"] = task.Metadata.GetValueOrDefault("latencyMs", "0"),
                        ["issueCount"] = task.Metadata.GetValueOrDefault("issueCount", "0"),
                        ["changeCount"] = task.Metadata.GetValueOrDefault("changeCount", "0")
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Task execution timeout — counts as a provider failure for the circuit breaker.
            if (providerCallAttempted) circuitBreaker?.RecordFailure();
            _logger.LogWarning("Task {TaskId} timed out after {TimeoutMs}ms",
                task.Id, _timeoutConfig.TaskExecutionMs);
            await FailTask(task, $"Task execution timed out after {_timeoutConfig.TaskExecutionMs}ms",
                "EXECUTION_TIMEOUT", retryCount);
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancellation — NOT a provider failure; don't penalise the circuit.
            task.Status = AgentTaskStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.StatusMessage = "Cancelled by user";
            BroadcastUpdate(task);
            await PersistTaskAsync(task);
            _logger.LogInformation("Task {TaskId} cancelled by user", task.Id);
        }
        catch (Exception ex)
        {
            // Ollama-specific recovery: attempt managed failover before penalising
            // the circuit breaker or sending to the DLQ.
            if (task.ModelProvider == ModelProvider.Ollama)
            {
                bool requeued = false;

                // Server back-pressure (429 / 503): retry on the same host with exponential backoff.
                if (IsOllamaBusyError(ex))
                    requeued = await TryRequeueForBusyAsync(task);

                // Host unreachable (DNS failure, connection refused) or busy-wait exhausted:
                // route to a different healthy server.
                if (!requeued && (IsOllamaConnectivityError(ex) || IsOllamaBusyError(ex)))
                    requeued = await TryRequeueWithFailoverAsync(task);

                if (requeued) return; // don't penalise circuit breaker for retriable errors
            }

            // Genuine, non-retriable provider failure: open the circuit and send to DLQ.
            if (providerCallAttempted) circuitBreaker?.RecordFailure();
            _logger.LogError(ex, "Task {TaskId} failed", task.Id);
            await FailTask(task, ex.Message, ex.GetType().Name, retryCount);
        }
        finally
        {
            _runningTasks.TryRemove(task.Id, out _);
            _concurrencyLimiter.Release();
        }
    }

    /// <summary>
    /// Calls CompleteStreamingAsync, fires OnStreamingOutput events (throttled to ~200ms),
    /// and returns the full accumulated response string.
    ///
    /// Chunks are never dropped: each broadcast emits the accumulated text since the last
    /// broadcast (a delta slice), not just the most-recent raw chunk.  An idle watchdog
    /// cancels the stream if no chunk arrives within StreamingIdleTimeout.
    /// </summary>
    private async Task<string> StreamToStringAsync(
        IAgentProvider provider, string prompt, ModelConfig modelConfig,
        AgentTask task, CancellationToken ct)
    {
        var output = new StringBuilder();
        var tokenCount = 0;
        var lastBroadcastLength = 0;
        var lastBroadcast = DateTime.UtcNow;
        var broadcastInterval = TimeSpan.FromMilliseconds(_broadcastThrottleMs);

        // Idle watchdog: cancel if no chunk arrives within the configured window.
        // CancelAfter() resets the countdown on each successive call before it fires.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(_timeoutConfig.StreamingIdleTimeout);

        await foreach (var chunk in provider.CompleteStreamingAsync(prompt, modelConfig, idleCts.Token))
        {
            // Reset idle watchdog on every received chunk.
            idleCts.CancelAfter(_timeoutConfig.StreamingIdleTimeout);

            output.Append(chunk);
            tokenCount += chunk.Length / 4; // rough estimate: ~4 chars per token

            var now = DateTime.UtcNow;
            if (now - lastBroadcast >= broadcastInterval)
            {
                // Emit accumulated text since the previous broadcast (delta slice).
                var currentLength = output.Length;
                var delta = output.ToString(lastBroadcastLength, currentLength - lastBroadcastLength);
                _eventBus.Publish(new StreamingOutputEvent(new StreamingOutputMessage
                {
                    TaskId = task.Id,
                    TextChunk = delta,
                    TokensGeneratedSoFar = tokenCount,
                    IsLastChunk = false,
                }));

                // Update task progress based on token count (soft-caps near 78% asymptotically).
                // ~2 000 tokens ≈ 50 %, ~4 000 tokens ≈ 75 %, approaching but never reaching 78 %.
                var streamProgress = (int)(78.0 * (1.0 - Math.Exp(-tokenCount / 2000.0)));
                task.Progress = Math.Max(task.Progress, Math.Max(5, streamProgress));
                task.StatusMessage = $"Streaming… {tokenCount:N0} tokens";
                BroadcastUpdate(task);

                lastBroadcast = now;
                lastBroadcastLength = currentLength;
            }
        }

        // Emit any content buffered after the last throttled broadcast, then signal done.
        var remaining = output.ToString(lastBroadcastLength, output.Length - lastBroadcastLength);
        _eventBus.Publish(new StreamingOutputEvent(new StreamingOutputMessage
        {
            TaskId = task.Id,
            TextChunk = remaining,
            TokensGeneratedSoFar = tokenCount,
            IsLastChunk = true,
        }));

        return output.ToString();
    }

    // ── Ollama failover helpers ───────────────────────────────────────────────

    /// <summary>
    /// Returns true when the exception is a network-level connectivity failure
    /// (host not found, connection refused, network unreachable).
    /// These are retriable by routing to a different Ollama server.
    /// Model errors, auth failures, and timeouts are NOT connectivity errors.
    /// </summary>
    private static bool IsOllamaConnectivityError(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.HttpRequestError is System.Net.Http.HttpRequestError.NameResolutionError
                                     or System.Net.Http.HttpRequestError.ConnectionError)
                return true;

            if (hre.InnerException is System.Net.Sockets.SocketException se)
                return se.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                                          or System.Net.Sockets.SocketError.ConnectionRefused
                                          or System.Net.Sockets.SocketError.NetworkUnreachable
                                          or System.Net.Sockets.SocketError.HostUnreachable;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the Ollama server accepted the TCP connection but signalled
    /// back-pressure via HTTP 429 (Too Many Requests) or 503 (Service Unavailable).
    /// These are retriable on the same host after a back-off delay.
    /// </summary>
    private static bool IsOllamaBusyError(Exception ex)
        => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests
                                                 or System.Net.HttpStatusCode.ServiceUnavailable };

    /// <summary>
    /// Returns a set of Ollama base URLs that are currently targeted by running or
    /// queued tasks from the same workflow instance.  Used to prefer failover hosts
    /// that are NOT already busy with sibling steps, avoiding resource contention and
    /// any risk of circular waiting between dependent steps.
    /// </summary>
    private HashSet<string> GetSameWorkflowHosts(AgentTask task)
    {
        if (!task.Metadata.TryGetValue("workflowInstanceId", out var wfId))
            return [];

        return _taskQueue.GetAllTasks()
            .Where(t =>
                t.Id != task.Id &&
                (t.Status == AgentTaskStatus.Running || t.Status == AgentTaskStatus.Queued) &&
                t.Metadata.TryGetValue("workflowInstanceId", out var id) && id == wfId &&
                t.Metadata.ContainsKey("modelEndpoint"))
            .Select(t => t.Metadata["modelEndpoint"].TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Attempts to route the task to a different healthy Ollama server after a
    /// connectivity failure.  Tracks tried hosts in task metadata so the same broken
    /// server is never selected again within the same failover chain.
    ///
    /// When a healthy alternative is found the task is re-enqueued immediately.
    /// When no alternative is available the task is re-enqueued with exponential
    /// back-off on the original endpoint so it will be retried when the server
    /// recovers.
    ///
    /// Returns <see langword="false"/> once <see cref="MaxFailoverAttempts"/> is
    /// reached, at which point the caller should send the task to the DLQ.
    /// </summary>
    private async Task<bool> TryRequeueWithFailoverAsync(AgentTask task)
    {
        int attempts = task.Metadata.TryGetValue("_sagFailoverAttempts", out var attStr)
            && int.TryParse(attStr, out var att) ? att : 0;

        if (attempts >= MaxFailoverAttempts)
        {
            _logger.LogError(
                "Task {TaskId}: exhausted {Max} Ollama failover attempts — sending to DLQ",
                task.Id, MaxFailoverAttempts);
            return false;
        }

        // Build the set of already-tried endpoints
        var triedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (task.Metadata.TryGetValue("_sagTriedEndpoints", out var triedJson))
        {
            var tried = System.Text.Json.JsonSerializer.Deserialize<List<string>>(triedJson);
            if (tried is not null) triedSet.UnionWith(tried);
        }

        var currentEndpoint = task.Metadata.TryGetValue("modelEndpoint", out var ep)
            ? ep.TrimEnd('/') : null;
        if (!string.IsNullOrEmpty(currentEndpoint))
            triedSet.Add(currentEndpoint);

        // Select the next healthy host, preferring servers not already busy with
        // sibling workflow steps to avoid resource contention / circular waiting.
        string? nextEndpoint = null;
        if (_ollamaMonitor is not null)
        {
            var sameWorkflowHosts = GetSameWorkflowHosts(task);
            var candidates = _ollamaMonitor.GetAllReachableHosts()
                .Select(u => u.TrimEnd('/'))
                .Where(u => !triedSet.Contains(u))
                .ToList();

            // Prefer hosts without same-workflow tasks; fall back to any available candidate.
            nextEndpoint = candidates.FirstOrDefault(c => !sameWorkflowHosts.Contains(c))
                        ?? candidates.FirstOrDefault();
        }

        if (nextEndpoint is not null)
        {
            _logger.LogWarning(
                "Task {TaskId}: Ollama host {Old} unreachable — failing over to {New} " +
                "(attempt {N}/{Max})",
                task.Id, _aliasResolver?.GetAlias(currentEndpoint) ?? "unknown",
                _aliasResolver?.GetAlias(nextEndpoint) ?? "unknown",
                attempts + 1, MaxFailoverAttempts);

            triedSet.Add(nextEndpoint);
            task.Metadata["modelEndpoint"]        = nextEndpoint;
            task.Metadata["_sagFailoverAttempts"] = (attempts + 1).ToString();
            task.Metadata["_sagTriedEndpoints"]   =
                System.Text.Json.JsonSerializer.Serialize(triedSet.ToList());
            task.ScheduledFor  = null; // run immediately
            task.StatusMessage =
                $"Failing over to {_aliasResolver?.GetAlias(nextEndpoint) ?? "unknown"} (attempt {attempts + 1}/{MaxFailoverAttempts})";
        }
        else
        {
            // No healthy alternative — exponential back-off on same endpoint so the
            // task is retried when the server recovers.
            var backoffSec = (int)Math.Pow(2, Math.Min(attempts, 5)) * 30; // 30s → 960s
            var retryAt    = DateTime.UtcNow.AddSeconds(backoffSec);

            _logger.LogWarning(
                "Task {TaskId}: no healthy Ollama alternative found (attempt {N}/{Max}); " +
                "backoff {Sec}s, retry at {At:HH:mm:ss}",
                task.Id, attempts + 1, MaxFailoverAttempts, backoffSec, retryAt);

            task.Metadata["_sagFailoverAttempts"] = (attempts + 1).ToString();
            task.Metadata["_sagTriedEndpoints"]   =
                System.Text.Json.JsonSerializer.Serialize(triedSet.ToList());
            task.ScheduledFor  = retryAt;
            task.StatusMessage =
                $"Host unreachable — backoff {backoffSec}s " +
                $"(attempt {attempts + 1}/{MaxFailoverAttempts}, retry at {retryAt:HH:mm:ss})";
        }

        task.Status    = AgentTaskStatus.Queued;
        task.StartedAt = null;
        BroadcastUpdate(task);
        await PersistTaskAsync(task); // keep DB in sync with re-queued status
        _taskQueue.Enqueue(task);
        return true;
    }

    /// <summary>
    /// Handles Ollama server back-pressure (HTTP 429 / 503) by re-queuing the task
    /// on the SAME server with exponential back-off instead of failing immediately.
    ///
    /// Returns <see langword="false"/> once <see cref="MaxBusyRetries"/> is reached,
    /// after which the caller should escalate to <see cref="TryRequeueWithFailoverAsync"/>.
    /// </summary>
    private async Task<bool> TryRequeueForBusyAsync(AgentTask task)
    {
        int busyRetries = task.Metadata.TryGetValue("_sagBusyRetries", out var busyStr)
            && int.TryParse(busyStr, out var n) ? n : 0;

        if (busyRetries >= MaxBusyRetries)
            return false; // too many busy-waits → escalate to host failover

        var backoffSec = (int)Math.Pow(2, Math.Min(busyRetries, 4)) * 15; // 15s → 240s
        var retryAt    = DateTime.UtcNow.AddSeconds(backoffSec);

        _logger.LogWarning(
            "Task {TaskId}: Ollama server busy (retry {N}/{Max}) — re-queuing at {At:HH:mm:ss}",
            task.Id, busyRetries + 1, MaxBusyRetries, retryAt);

        task.Metadata["_sagBusyRetries"] = (busyRetries + 1).ToString();
        task.Status        = AgentTaskStatus.Queued;
        task.StartedAt     = null;
        task.ScheduledFor  = retryAt;
        task.StatusMessage =
            $"Server busy — retry {busyRetries + 1}/{MaxBusyRetries} at {retryAt:HH:mm:ss}";

        BroadcastUpdate(task);
        await PersistTaskAsync(task);
        _taskQueue.Enqueue(task);
        return true;
    }

    // Failed tasks go to DLQ with full context
    private async Task FailTask(AgentTask task, string errorMessage, string? errorCode, int retryCount)
    {
        task.Status = AgentTaskStatus.Failed;
        task.StatusMessage = errorMessage;
        task.CompletedAt = DateTime.UtcNow;
        _metrics?.RecordTaskFailed();
        BroadcastUpdate(task);

        // Persist failed perf sample when there was a real provider call (StartedAt is set)
        if (_perfRepo is not null && task.StartedAt.HasValue)
        {
            var latencyMs = (long)(DateTime.UtcNow - task.StartedAt.Value).TotalMilliseconds;
            var endpoint  = task.Metadata.GetValueOrDefault("modelEndpoint", "");
            var status    = errorCode == "EXECUTION_TIMEOUT" ? "timeout"
                          : errorCode == "CIRCUIT_OPEN"      ? "circuit_open"
                          : "error";
            _ = _perfRepo.InsertSampleAsync(new ModelPerfSample(
                Id:           Guid.NewGuid().ToString("N")[..16],
                Provider:     task.ModelProvider.ToString(),
                ModelId:      task.ModelId,
                ServerAlias:  _aliasResolver?.GetAlias(endpoint) ?? "unknown",
                StartedAt:    task.StartedAt.Value,
                LatencyMs:    latencyMs,
                TokensInput:  0,
                TokensOutput: 0,
                Status:       status,
                ErrorCode:    errorCode));
        }
        await PersistTaskAsync(task);

        // Notify workflow engine so downstream steps can be skipped
        if (task.Metadata.ContainsKey("workflowInstanceId"))
            _ = _serviceProvider.GetService<WorkflowEngine>()?.OnTaskUpdateAsync(ToResponse(task));

        _deadLetterQueue.Enqueue(task, errorMessage, errorCode, retryCount);

        // Log activity for task failure
        if (_activityLogger != null && task.Metadata.TryGetValue("workspacePath", out var workspacePath))
        {
            await _activityLogger.LogActivityAsync(new ActivityEntry
            {
                WorkspacePath = workspacePath,
                Timestamp = task.CompletedAt.Value,
                HourBucket = ActivityLogger.GetHourBucket(task.CompletedAt.Value),
                ActivityType = ActivityType.AgentTask,
                Actor = "agent",
                Summary = $"Task failed: {task.AgentType} - {task.Description}",
                Details = $"Error: {errorMessage}",
                TaskId = task.Id,
                FilePaths = task.FilePaths,
                Metadata = new Dictionary<string, string>
                {
                    ["agentType"] = task.AgentType.ToString(),
                    ["modelProvider"] = task.ModelProvider.ToString(),
                    ["modelId"] = task.ModelId,
                    ["status"] = "failed",
                    ["errorMessage"] = errorMessage,
                    ["errorCode"] = errorCode ?? "UNKNOWN",
                    ["retryCount"] = retryCount.ToString()
                }
            }, CancellationToken.None);
        }
    }

    // Status-aware cancellation
    public async Task CancelTaskAsync(string taskId, CancellationToken ct)
    {
        var task = _taskQueue.GetTask(taskId);
        if (task is null) return;

        switch (task.Status)
        {
            case AgentTaskStatus.Queued:
                task.Status = AgentTaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
                task.StatusMessage = "Cancelled while queued";
                BroadcastUpdate(task);
                await PersistTaskAsync(task);
                _logger.LogInformation("Queued task {TaskId} cancelled", taskId);
                await LogCancellationActivity(task, "Cancelled while queued", ct);
                break;

            case AgentTaskStatus.Running:
                // Immediately mark Cancelled and broadcast so the UI updates without waiting
                // for the LLM request to finish.  ExecuteTaskAsync's catch block will fire
                // another Cancelled broadcast — that's idempotent and handled by the dedup on
                // the extension side.
                task.Status = AgentTaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
                task.StatusMessage = "Cancelled by user";
                BroadcastUpdate(task);
                await PersistTaskAsync(task);
                await LogCancellationActivity(task, "Cancelled while running", ct);
                if (_runningTasks.TryRemove(taskId, out var cts))
                {
                    cts.Cancel();
                    _logger.LogInformation("Running task {TaskId} cancellation requested", taskId);
                }
                break;

            case AgentTaskStatus.WaitingApproval:
                task.Status = AgentTaskStatus.Cancelled;
                task.CompletedAt = DateTime.UtcNow;
                task.StatusMessage = "Cancelled while waiting approval";
                BroadcastUpdate(task);
                await PersistTaskAsync(task);
                _logger.LogInformation("Waiting-approval task {TaskId} cancelled", taskId);
                await LogCancellationActivity(task, "Cancelled while waiting approval", ct);
                break;

            default:
                _logger.LogDebug("Task {TaskId} in terminal state {Status}, ignoring cancel",
                    taskId, task.Status);
                break;
        }
    }

    private async Task LogCancellationActivity(AgentTask task, string reason, CancellationToken ct)
    {
        if (_activityLogger != null && task.Metadata.TryGetValue("workspacePath", out var workspacePath))
        {
            await _activityLogger.LogActivityAsync(new ActivityEntry
            {
                WorkspacePath = workspacePath,
                Timestamp = task.CompletedAt ?? DateTime.UtcNow,
                HourBucket = ActivityLogger.GetHourBucket(task.CompletedAt ?? DateTime.UtcNow),
                ActivityType = ActivityType.AgentTask,
                Actor = "human",
                Summary = $"Task cancelled: {task.AgentType} - {task.Description}",
                Details = reason,
                TaskId = task.Id,
                FilePaths = task.FilePaths,
                Metadata = new Dictionary<string, string>
                {
                    ["agentType"] = task.AgentType.ToString(),
                    ["modelProvider"] = task.ModelProvider.ToString(),
                    ["modelId"] = task.ModelId,
                    ["status"] = "cancelled",
                    ["reason"] = reason
                }
            }, ct);
        }
    }

    // Retry from DLQ — creates a new task with same parameters
    public async Task<string?> RetryFromDlqAsync(string dlqId, CancellationToken ct)
    {
        var entry = _deadLetterQueue.DequeueForRetry(dlqId);
        if (entry is null) return null;

        var newTask = new AgentTask
        {
            AgentType = entry.AgentType,
            ModelProvider = entry.ModelProvider,
            ModelId = entry.ModelId,
            Description = entry.Description,
            FilePaths = entry.FilePaths,
            Metadata = { ["retriedFromDlq"] = dlqId, ["originalTaskId"] = entry.OriginalTaskId }
        };

        return await SubmitTaskAsync(newTask, ct);
    }

    public TaskStatusResponse? GetTaskStatus(string taskId)
    {
        var task = _taskQueue.GetTask(taskId);
        return task is null ? null : ToResponse(task);
    }

    public List<TaskStatusResponse> GetAllTasks()
    {
        return _taskQueue.GetAllTasks().Select(ToResponse).ToList();
    }

    public DeadLetterQueue DLQ => _deadLetterQueue;

    public async Task ApproveTaskAsync(string taskId, bool approved, CancellationToken ct)
    {
        _taskQueue.UpdateTask(taskId, t =>
        {
            t.Status = approved ? AgentTaskStatus.Completed : AgentTaskStatus.Cancelled;
            t.CompletedAt = DateTime.UtcNow;
        });

        var task = _taskQueue.GetTask(taskId);
        if (task is not null)
        {
            BroadcastUpdate(task);
            await PersistTaskAsync(task);
        }
    }

    private void BroadcastUpdate(AgentTask task)
    {
        _eventBus.Publish(new TaskUpdatedEvent(ToResponse(task)));
        // Register terminal tasks for bounded in-memory history eviction
        if (task.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled)
            _taskQueue.MarkTerminal(task.Id);
    }

    private static TaskStatusResponse ToResponse(AgentTask task)
    {
        AgentResult? result = null;
        if (task.Status == AgentTaskStatus.Completed &&
            task.Metadata.TryGetValue("response", out var rawOutput))
        {
            var latency = task.Metadata.TryGetValue("latencyMs", out var ms)
                ? long.Parse(ms) : 0L;

            // Rehydrate structured lists from metadata so the UI receives full parsed results.
            var issues = task.Metadata.TryGetValue("issuesJson", out var issJson)
                ? System.Text.Json.JsonSerializer.Deserialize<List<Issue>>(issJson) ?? []
                : [];
            var changes = task.Metadata.TryGetValue("changesJson", out var chnJson)
                ? System.Text.Json.JsonSerializer.Deserialize<List<FileChange>>(chnJson) ?? []
                : [];

            result = new AgentResult
            {
                TaskId = task.Id,
                Success = true,
                Output = rawOutput,
                LatencyMs = latency,
                Issues = issues,
                Changes = changes,
            };
        }

        return new TaskStatusResponse
        {
            TaskId = task.Id,
            Status = task.Status,
            Progress = task.Progress,
            StatusMessage = task.StatusMessage,
            AgentType = task.AgentType,
            ModelProvider = task.ModelProvider,
            ModelId = task.ModelId,
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            Result = result,
            ScheduledFor = task.ScheduledFor,
            ComparisonGroupId = task.ComparisonGroupId,
        };
    }

    private async Task PersistTaskAsync(AgentTask task)
    {
        if (_repository is null) return;
        try { await _repository.SaveTaskAsync(task); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist task {TaskId}", task.Id); }
    }

    private async Task PersistResultAsync(AgentResult result)
    {
        if (_repository is null) return;
        try { await _repository.SaveResultAsync(result); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist result for task {TaskId}", result.TaskId); }
    }

    private async Task PersistCompletedAsync(AgentTask task, AgentResult result)
    {
        if (_repository is null) return;
        try { await _repository.SaveTaskCompletedWithResultAsync(task, result); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to atomically persist completed task {TaskId}", task.Id); }
    }

    /// <summary>
    /// Strips the <c>@machine</c> routing suffix from a model identifier.
    /// <para>
    /// YAML prompts and workflow step definitions may use the notation
    /// <c>modelId@machineName</c> (e.g. <c>deepseek-coder-v2:16b@workstation</c>) to
    /// express both the model name and the preferred Ollama server in a single string.
    /// The suffix is consumed during task routing and must be removed before the model
    /// name is forwarded to the Ollama API, which would otherwise return a 404.
    /// </para>
    /// <para>
    /// Rules: the <c>@</c> must not be the first character (empty model name is invalid),
    /// so an index of 0 is treated as a literal <c>@</c> in the model name and left unchanged.
    /// </para>
    /// </summary>
    internal static string StripMachineSuffix(string modelId)
    {
        var atIdx = modelId.LastIndexOf('@');
        return atIdx > 0 ? modelId[..atIdx].Trim() : modelId;
    }

    // Determinism — SHA-256 output cache key
    private static string ComputeCacheKey(string prompt, string modelId, string provider)
    {
        var raw = $"{provider}:{modelId}:{prompt}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}

public static class ServiceProviderExtensions
{
    public static IEnumerable<T> GetServices<T>(this IServiceProvider provider)
    {
        return (IEnumerable<T>?)provider.GetService(typeof(IEnumerable<T>))
            ?? Enumerable.Empty<T>();
    }
}
