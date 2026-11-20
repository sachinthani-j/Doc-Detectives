namespace DocDetectives.Domain.Entities;

/// <param name="ManufactureYear">Year the equipment was built, only when the manual states it; never the document date.</param>
public sealed record EquipmentAssetStructure(
    string? Manufacturer,
    string? EquipmentName,
    List<string> ModelNumbers,
    string? EquipmentCategory,
    string? DocumentCode,
    List<TechnicalSpecItem> TechnicalSpecifications,
    List<string> SafetyWarnings,
    string? CalibrationAndMaintenance,
    List<SparePartItem> SpareParts,
    List<FaultCodeItem> FaultCodes,
    List<SubAssetItem> SubAssets,
    int? ManufactureYear);

public sealed record TechnicalSpecItem(
    string Parameter,
    string Value);

/// <param name="BelongsTo">Name of the sub-asset this part belongs to; null when it belongs to the whole equipment.</param>
public sealed record SparePartItem(
    string PartName,
    string ArticleNumber,
    string? BelongsTo);

/// <param name="ParentName">Name of the parent sub-asset; null when the parent is the whole equipment.</param>
/// <param name="Reason">Why work is tracked at this level (maintenance plan, own serial number, failure history...).</param>
public sealed record SubAssetItem(
    string Name,
    string? ParentName,
    string Reason);

public sealed record FaultCodeItem(
    string CodeOrSymptom,
    string Cause,
    string Resolution);