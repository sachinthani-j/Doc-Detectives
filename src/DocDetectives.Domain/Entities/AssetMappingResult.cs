namespace DocDetectives.Domain.Entities;

public enum MappingStatus
{
    AutoLinked,
    NeedsReview,
    Created,
    /// <summary>A new asset is proposed and waits for a person to review it; nothing is created yet.</summary>
    Proposed,
    Unmatched
}

public sealed record AssetMappingResult(
    string AssetId,
    string ManualId,
    double ConfidenceScore,
    string Reasoning,
    MappingStatus Status);