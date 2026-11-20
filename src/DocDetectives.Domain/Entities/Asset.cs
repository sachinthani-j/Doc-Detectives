namespace DocDetectives.Domain.Entities;

/// <param name="ParentAssetId">Asset ID of the parent in the hierarchy; null for a top-level asset.</param>
/// <param name="SpareParts">Bill of materials, one "ArticleNumber | PartName" entry per part.</param>
/// <param name="Characteristics">Characteristic values (e.g. Head 32 m, rpm 1450), linked to the master table.</param>
/// <param name="ManufactureYear">Year the equipment was built; null when unknown.</param>
/// <param name="ManufacturerName">Full manufacturer name (Manufacturer may hold an EAM code such as "KARO").</param>
public sealed record Asset(
    string Id,
    string Name,
    string Manufacturer,
    string ModelNumber,
    string SerialNumber,
    string Category,
    string? ParentAssetId = null,
    IReadOnlyList<string>? SpareParts = null,
    IReadOnlyList<AssetCharacteristic>? Characteristics = null,
    int? ManufactureYear = null,
    string? ManufacturerName = null);
