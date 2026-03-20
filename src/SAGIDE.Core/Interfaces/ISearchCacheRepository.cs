using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persistent search result cache keyed by SHA-256 hash of (query + maxResults).
/// Abstracted so <c>SAGIDE.Memory</c> has no reference to <c>SAGIDE.Service.Persistence</c>.
/// </summary>
public interface ISearchCacheRepository
{
    Task<SearchCacheEntry?> GetAsync(string queryHash);
    Task UpsertAsync(SearchCacheEntry entry);
    Task PruneAsync(int retentionDays);
}
