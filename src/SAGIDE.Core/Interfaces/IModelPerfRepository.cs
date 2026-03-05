using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface IModelPerfRepository
{
    /// <summary>Persists one per-call performance sample (fire-and-forget safe).</summary>
    Task InsertSampleAsync(ModelPerfSample sample);

    /// <summary>
    /// Returns aggregated performance summaries for all (modelId, serverAlias) pairs
    /// observed in the given time window. Filters are optional.
    /// </summary>
    Task<IReadOnlyList<ModelPerfSummary>> GetSummaryAsync(
        string? modelId, string? serverAlias, int windowMinutes);

    /// <summary>Deletes samples older than <paramref name="retentionDays"/> days.</summary>
    Task PruneOldSamplesAsync(int retentionDays);
}
