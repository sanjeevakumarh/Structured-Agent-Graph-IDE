using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Observability;

namespace SAGIDE.Tools;

/// <summary>
/// In-process implementation of <see cref="IToolRegistry"/>.
///
/// Tools are registered at startup and resolved by name at call time.
/// Every <see cref="ExecuteAsync"/> call:
///   1. Emits a <c>SAGIDE.Tools</c> activity span (latency, tool name, caller).
///   2. Calls <see cref="IAuditLog.RecordToolCallAsync"/> fire-and-forget.
///   3. Delegates to the tool implementation.
///
/// When the Security module adds per-tool permission gates (Option B), the check
/// will be inserted here before step 3 — callers won't change.
/// </summary>
public sealed class InProcessToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAuditLog? _auditLog;
    private readonly ILogger<InProcessToolRegistry> _logger;

    public InProcessToolRegistry(
        ILogger<InProcessToolRegistry> logger,
        IAuditLog? auditLog = null)
    {
        _logger   = logger;
        _auditLog = auditLog;
    }

    public IReadOnlyList<ITool> All => [.. _tools.Values];

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogDebug("Tool registered: {Name}", tool.Name);
    }

    public ITool? Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public async Task<string> ExecuteAsync(
        string toolName,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException(
                $"Tool '{toolName}' is not registered. Available: {string.Join(", ", _tools.Keys)}");

        // Observability span
        using var activity = SagideActivitySource.Start(
            SagideActivitySource.Tools,
            $"tool.execute:{toolName}",
            ActivityKind.Internal,
            TraceContext.TraceId);
        activity?.SetTag("tool.name",   toolName);
        activity?.SetTag("tool.params", parameters.Count);

        // Audit — fire-and-forget, never block the call
        var callerTag = TraceContext.SourceTag ?? "unknown";
        if (_auditLog is not null)
            _ = _auditLog.RecordToolCallAsync(toolName, parameters, callerTag, ct);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await tool.ExecuteAsync(parameters, ct);
            activity?.SetTag("tool.success",    true);
            activity?.SetTag("tool.latency_ms", sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag("tool.success",    false);
            activity?.SetTag("tool.error",      ex.GetType().Name);
            activity?.SetTag("tool.latency_ms", sw.ElapsedMilliseconds);
            _logger.LogWarning(ex, "Tool '{Name}' threw an exception", toolName);
            throw;
        }
    }
}
