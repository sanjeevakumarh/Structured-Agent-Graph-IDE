namespace SAGIDE.Core.Models;

/// <summary>
/// Per-call performance sample captured after each LLM provider invocation.
/// Stored in the <c>model_perf_samples</c> SQLite table.
/// </summary>
public record ModelPerfSample(
    string Id,
    string Provider,
    string ModelId,
    string ServerAlias,
    DateTime StartedAt,
    long LatencyMs,
    int TokensInput,
    int TokensOutput,
    /// <summary>"success" | "error" | "timeout" | "circuit_open"</summary>
    string Status,
    string? ErrorCode);

/// <summary>
/// Aggregated performance summary for a (ModelId, ServerAlias) pair over a time window.
/// </summary>
public record ModelPerfSummary(
    string ModelId,
    string ServerAlias,
    int SampleCount,
    int SuccessCount,
    long P50LatencyMs,
    long P95LatencyMs,
    double SuccessRate,
    double TokensPerSec,
    DateTime WindowStart,
    DateTime WindowEnd);
