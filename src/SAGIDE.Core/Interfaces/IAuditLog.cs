namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Append-only audit trail for security-relevant events in the Agent OS.
///
/// Every task submission, tool call, and authentication failure is recorded
/// so that operators can reconstruct what happened and when.
///
/// The default implementation persists to SQLite via <c>SqliteAuditLog</c>
/// in <c>SAGIDE.Security</c>. A no-op implementation is used in tests.
/// </summary>
public interface IAuditLog
{
    /// <summary>Record a task submission event.</summary>
    Task RecordTaskSubmittedAsync(
        string taskId,
        string agentType,
        string modelProvider,
        string modelId,
        string sourceTag,
        CancellationToken ct = default);

    /// <summary>Record a tool call (pre-execution).</summary>
    Task RecordToolCallAsync(
        string toolName,
        IReadOnlyDictionary<string, string> parameters,
        string callerTag,
        CancellationToken ct = default);

    /// <summary>Record an authentication failure (wrong/missing bearer token).</summary>
    Task RecordAuthFailureAsync(
        string path,
        string? remoteIp,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieve recent audit entries for the API / dashboard.
    /// </summary>
    Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int limit = 100, CancellationToken ct = default);
}

/// <summary>One row in the audit trail.</summary>
public record AuditEntry(
    string Id,
    string EventType,       // "task_submitted" | "tool_call" | "auth_failure"
    string Subject,         // taskId, toolName, or path
    string Actor,           // sourceTag or remote IP
    string Detail,          // JSON blob with event-specific fields
    DateTime OccurredAt);
