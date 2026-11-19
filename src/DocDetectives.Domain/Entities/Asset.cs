namespace DocDetectives.Domain.Entities;

/// <param name="ParentAssetId">Asset ID of the parent in the hierarchy; null for a top-level asset.</param>
/// <param name="SpareParts">Bill of materials, one "ArticleNumber | PartName" entry per part.</param>
public sealed record Asset(
    string Id,
    string Name,
    string Manufacturer,
    string ModelNumber,
    string SerialNumber,
    string Category,
    string? ParentAssetId = null,
    IReadOnlyList<string>? SpareParts = null);
