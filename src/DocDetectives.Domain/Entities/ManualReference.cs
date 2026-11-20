namespace DocDetectives.Domain.Entities;

/// <summary>A manual stored in the knowledge index, with how many sections of it are indexed.</summary>
public sealed record ManualReference(
    string ManualId,
    string FileName,
    int SectionCount);
