using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persistence contract for LLM output quality samples captured by idle-capacity probes.
/// </summary>
public interface IModelQualityRepository
{
    Task InsertSampleAsync(ModelQualitySample sample);

    /// <summary>Returns the most recent quality samples ordered by <c>scored_at DESC</c>.</summary>
    Task<IReadOnlyList<ModelQualitySample>> GetRecentScoresAsync(
        string? modelId, string? serverAlias, int limit = 20);

    Task PruneOldSamplesAsync(int retentionDays);
}
