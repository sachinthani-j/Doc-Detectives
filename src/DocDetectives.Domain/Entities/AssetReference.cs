namespace DocDetectives.Domain.Entities;

/// <summary>
/// What a document states about the equipment it is about (e.g. an inspection checklist's header).
/// Any field may be missing.
/// </summary>
public sealed record AssetReference(
    string? AssetId,
    string? SerialNumber,
    string? AssetName,
    string? Manufacturer,
    string? ModelNumber);
