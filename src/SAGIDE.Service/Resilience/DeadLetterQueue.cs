using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Resilience;

public class DeadLetterQueue
{
    private readonly ConcurrentDictionary<string, DeadLetterEntry> _entries = new();
    private readonly ITaskRepository? _repository;
    private readonly ILogger<DeadLetterQueue> _logger;
    private readonly TimeSpan _retentionPeriod;

    public DeadLetterQueue(ILogger<DeadLetterQueue> logger, ITaskRepository? repository = null, int retentionDays = 7)
    {
        _logger = logger;
        _repository = repository;
        _retentionPeriod = TimeSpan.FromDays(retentionDays);
    }

    public async Task LoadFromStoreAsync()
    {
        if (_repository is null) return;

        var entries = await _repository.GetDlqEntriesAsync();
        foreach (var entry in entries)
        {
            _entries[entry.Id] = entry;
        }
        _logger.LogInformation("Loaded {Count} DLQ entries from database", entries.Count);
    }

    public void Enqueue(AgentTask failedTask, string errorMessage, string? errorCode = null, int retryCount = 0)
    {
        var entry = new DeadLetterEntry
        {
            OriginalTaskId = failedTask.Id,
            AgentType = failedTask.AgentType,
            ModelProvider = failedTask.ModelProvider,
            ModelId = failedTask.ModelId,
            Description = failedTask.Description,
            FilePaths = failedTask.FilePaths,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode,
            RetryCount = retryCount,
            OriginalCreatedAt = failedTask.CreatedAt,
            Metadata = new Dictionary<string, string>(failedTask.Metadata)
        };

        _entries[entry.Id] = entry;
        _ = PersistEntryAsync(entry);

        _logger.LogWarning(
            "Task {OriginalTaskId} moved to DLQ as {DlqId}: {Error} (after {RetryCount} retries)",
            failedTask.Id, entry.Id, errorMessage, retryCount);
    }

    public IReadOnlyList<DeadLetterEntry> GetAll()
    {
        return _entries.Values
            .OrderByDescending(e => e.FailedAt)
            .ToList();
    }

    public DeadLetterEntry? Get(string dlqId)
    {
        _entries.TryGetValue(dlqId, out var entry);
        return entry;
    }

    public DeadLetterEntry? DequeueForRetry(string dlqId)
    {
        if (_entries.TryRemove(dlqId, out var entry))
        {
            _ = RemoveEntryAsync(dlqId);
            _logger.LogInformation("DLQ entry {DlqId} dequeued for retry", dlqId);
            return entry;
        }
        return null;
    }

    public bool Discard(string dlqId)
    {
        if (_entries.TryRemove(dlqId, out _))
        {
            _ = RemoveEntryAsync(dlqId);
            _logger.LogInformation("DLQ entry {DlqId} discarded", dlqId);
            return true;
        }
        return false;
    }

    public int Count => _entries.Count;

    public int PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - _retentionPeriod;
        var expired = _entries
            .Where(kvp => kvp.Value.FailedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            _entries.TryRemove(key, out _);
        }

        if (expired.Count > 0)
        {
            _ = PurgePersistenceAsync(cutoff);
            _logger.LogInformation("Purged {Count} expired DLQ entries (older than {Days} days)",
                expired.Count, _retentionPeriod.TotalDays);
        }

        return expired.Count;
    }

    private async Task PersistEntryAsync(DeadLetterEntry entry)
    {
        if (_repository is null) return;
        try { await _repository.SaveDlqEntryAsync(entry); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to persist DLQ entry {DlqId}", entry.Id); }
    }

    private async Task RemoveEntryAsync(string dlqId)
    {
        if (_repository is null) return;
        try { await _repository.RemoveDlqEntryAsync(dlqId); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to remove DLQ entry {DlqId} from DB", dlqId); }
    }

    private async Task PurgePersistenceAsync(DateTime cutoff)
    {
        if (_repository is null) return;
        try { await _repository.PurgeDlqOlderThanAsync(cutoff); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to purge expired DLQ entries from DB"); }
    }
}
