namespace DocDetectives.Domain.Entities;

public static class JobStatus
{
    public const string Open = "Open";
    public const string Completed = "Completed";
}

/// <param name="Id">Deterministic per source document, so re-ingesting the same document updates the same job.</param>
public sealed record Job(
    string Id,
    string AssetId,
    string Title,
    string JobType,
    string Status,
    string SourceDocumentId,
    string SourceFileName,
    string? InspectionDate,
    string? Inspector,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<JobLine> Lines);

/// <summary>
/// One job line, linked to the equipment it is about: a sub-asset when the inspection point names one,
/// otherwise the job's own asset.
/// </summary>
public sealed record JobLine(
    int LineNumber,
    string AssetId,
    string? Component,
    string Description,
    string? Criteria,
    string? Result,
    string? Measurement,
    string? Remarks);
