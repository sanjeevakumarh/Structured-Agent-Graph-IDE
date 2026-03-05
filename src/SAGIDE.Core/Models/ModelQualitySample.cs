namespace SAGIDE.Core.Models;

/// <summary>
/// A single LLM-scored output quality sample captured by <c>QualitySampler</c>.
/// Score is 0–100 as returned by the evaluating model (claude-sonnet-4-6).
/// </summary>
public record ModelQualitySample(
    string   Id,
    string   Provider,
    string   ModelId,
    string   ServerAlias,
    DateTime ScoredAt,
    double   Score,           // 0–100
    string   ReferenceTaskId);
