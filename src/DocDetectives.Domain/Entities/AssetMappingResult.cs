namespace DocDetectives.Domain.Entities;

public enum MappingStatus
{
    AutoLinked,
    NeedsReview,
    Created,
    Unmatched
}

public sealed record AssetMappingResult(
    string AssetId,
    string ManualId,
    double ConfidenceScore,
    string Reasoning,
    MappingStatus Status);