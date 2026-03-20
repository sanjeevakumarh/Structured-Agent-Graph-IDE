using SAGIDE.Core.Interfaces;

namespace SAGIDE.Security;

/// <summary>
/// No-op audit log used in tests and when <c>SAGIDE:Security:AuditLog:Enabled</c>
/// is false. All writes are silently discarded; reads return an empty list.
/// </summary>
public sealed class NullAuditLog : IAuditLog
{
    public static readonly NullAuditLog Instance = new();

    public Task RecordTaskSubmittedAsync(string taskId, string agentType, string modelProvider,
        string modelId, string sourceTag, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordToolCallAsync(string toolName,
        IReadOnlyDictionary<string, string> parameters, string callerTag,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordAuthFailureAsync(string path, string? remoteIp,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditEntry>> GetRecentAsync(int limit = 100,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
}
