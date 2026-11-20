namespace DocDetectives.Domain.Entities;

/// <summary>A manual stored in the knowledge index, with how many sections of it are indexed.</summary>
/// <param name="AssetIds">Assets the manual is linked to (union over its sections); null when not loaded.</param>
/// <param name="SectionTitles">Section titles in document order; only loaded for a single manual.</param>
public sealed record ManualReference(
    string ManualId,
    string FileName,
    int SectionCount,
    IReadOnlyList<string>? AssetIds = null,
    IReadOnlyList<string>? SectionTitles = null);
