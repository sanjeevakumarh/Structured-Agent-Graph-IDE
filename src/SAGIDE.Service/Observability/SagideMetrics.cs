using System.Diagnostics.Metrics;

namespace SAGIDE.Service.Observability;

/// <summary>
/// Central metrics hub for the SAGIDE service (O2).
///
/// Uses <see cref="System.Diagnostics.Metrics.Meter"/> so values are automatically
/// available to any registered <see cref="MeterListener"/> (e.g. an OpenTelemetry SDK,
/// dotnet-counters, or a future Prometheus exporter).
///
/// Running totals are also tracked via <see cref="System.Threading.Interlocked"/> so the
/// <c>GET /api/metrics</c> endpoint can return an instant snapshot without a MeterListener.
/// </summary>
public sealed class SagideMetrics : IDisposable
{
    private readonly Meter _meter;

    // ── System.Diagnostics.Metrics instruments ────────────────────────────────
    private readonly Counter<long> _counterSubmitted;
    private readonly Counter<long> _counterCompleted;
    private readonly Counter<long> _counterFailed;
    private readonly Counter<long> _counterInputTokens;
    private readonly Counter<long> _counterOutputTokens;
    private readonly Histogram<double> _histogramLatencyMs;

    // ── In-process running totals (for /api/metrics snapshot) ─────────────────
    private long _tasksSubmitted;
    private long _tasksCompleted;
    private long _tasksFailed;
    private long _llmInputTokens;
    private long _llmOutputTokens;
    private long _llmTotalLatencyMs;
    private long _llmCallCount;

    public DateTime ServiceStartedAt { get; } = DateTime.UtcNow;

    /// <param name="queueDepth">Callback returning current pending-queue depth (TaskQueue.PendingCount).</param>
    /// <param name="dlqDepth">Callback returning current DLQ depth (DeadLetterQueue.Count).</param>
    /// <param name="activeWorkflows">Callback returning current active workflow instances.</param>
    public SagideMetrics(
        Func<int>? queueDepth     = null,
        Func<int>? dlqDepth       = null,
        Func<int>? activeWorkflows = null)
    {
        _meter = new Meter("SAGIDE", "1.0");

        _counterSubmitted    = _meter.CreateCounter<long>("sag.tasks.submitted",    "tasks",   "Total tasks submitted since startup");
        _counterCompleted    = _meter.CreateCounter<long>("sag.tasks.completed",    "tasks",   "Total tasks completed successfully");
        _counterFailed       = _meter.CreateCounter<long>("sag.tasks.failed",       "tasks",   "Total tasks that failed or were dead-lettered");
        _counterInputTokens  = _meter.CreateCounter<long>("sag.llm.input_tokens",   "tokens",  "Cumulative LLM input tokens");
        _counterOutputTokens = _meter.CreateCounter<long>("sag.llm.output_tokens",  "tokens",  "Cumulative LLM output tokens");
        _histogramLatencyMs  = _meter.CreateHistogram<double>("sag.llm.latency_ms", "ms",      "Per-call LLM latency");

        if (queueDepth is not null)
            _meter.CreateObservableGauge("sag.queue.depth",      queueDepth,      "tasks",     "Pending items in the task queue");
        if (dlqDepth is not null)
            _meter.CreateObservableGauge("sag.dlq.depth",        dlqDepth,        "tasks",     "Items in the dead-letter queue");
        if (activeWorkflows is not null)
            _meter.CreateObservableGauge("sag.workflows.active", activeWorkflows,  "instances", "Active workflow instances");
    }

    // ── Record methods (called from AgentOrchestrator) ────────────────────────

    public void RecordTaskSubmitted()
    {
        Interlocked.Increment(ref _tasksSubmitted);
        _counterSubmitted.Add(1);
    }

    public void RecordTaskCompleted()
    {
        Interlocked.Increment(ref _tasksCompleted);
        _counterCompleted.Add(1);
    }

    public void RecordTaskFailed()
    {
        Interlocked.Increment(ref _tasksFailed);
        _counterFailed.Add(1);
    }

    /// <summary>Records one LLM call's latency and token counts (called after each model response).</summary>
    public void RecordLlmCall(long latencyMs, int inputTokens, int outputTokens)
    {
        Interlocked.Add(ref _llmTotalLatencyMs, latencyMs);
        Interlocked.Increment(ref _llmCallCount);
        Interlocked.Add(ref _llmInputTokens, inputTokens);
        Interlocked.Add(ref _llmOutputTokens, outputTokens);

        _counterInputTokens.Add(inputTokens);
        _counterOutputTokens.Add(outputTokens);
        _histogramLatencyMs.Record(latencyMs);
    }

    // ── Snapshot (for /api/metrics) ──────────────────────────────────────────

    public MetricsSnapshot GetSnapshot(int queuePending, int queueRunning, int dlqDepth, int activeWorkflows, long ipcDropped)
    {
        var callCount  = Interlocked.Read(ref _llmCallCount);
        var totalLatMs = Interlocked.Read(ref _llmTotalLatencyMs);
        return new MetricsSnapshot(
            TasksSubmitted    : Interlocked.Read(ref _tasksSubmitted),
            TasksCompleted    : Interlocked.Read(ref _tasksCompleted),
            TasksFailed       : Interlocked.Read(ref _tasksFailed),
            QueuePending      : queuePending,
            QueueRunning      : queueRunning,
            LlmCalls          : callCount,
            LlmInputTokens    : Interlocked.Read(ref _llmInputTokens),
            LlmOutputTokens   : Interlocked.Read(ref _llmOutputTokens),
            LlmAvgLatencyMs   : callCount > 0 ? (double)totalLatMs / callCount : 0,
            DlqDepth          : dlqDepth,
            ActiveWorkflows   : activeWorkflows,
            IpcDroppedMessages: ipcDropped,
            ServiceStartedAt  : ServiceStartedAt);
    }

    public void Dispose() => _meter.Dispose();
}

/// <summary>Point-in-time metrics snapshot returned by <c>GET /api/metrics</c>.</summary>
public record MetricsSnapshot(
    long     TasksSubmitted,
    long     TasksCompleted,
    long     TasksFailed,
    int      QueuePending,
    int      QueueRunning,
    long     LlmCalls,
    long     LlmInputTokens,
    long     LlmOutputTokens,
    double   LlmAvgLatencyMs,
    int      DlqDepth,
    int      ActiveWorkflows,
    long     IpcDroppedMessages,
    DateTime ServiceStartedAt)
{
    public DateTime SampledAt { get; } = DateTime.UtcNow;
}
