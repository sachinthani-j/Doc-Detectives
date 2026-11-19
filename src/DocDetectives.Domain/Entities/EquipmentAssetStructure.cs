namespace DocDetectives.Domain.Entities;

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
    List<FaultCodeItem> FaultCodes);

public sealed record TechnicalSpecItem(
    string Parameter,
    string Value);

public sealed record SparePartItem(
    string PartName,
    string ArticleNumber);

public sealed record FaultCodeItem(
    string CodeOrSymptom,
    string Cause,
    string Resolution);