namespace DocDetectives.Domain.Entities;

public sealed record ExtractedManual(
    string ManualId,
    string FileName,
    string MarkdownContent,
    string? Manufacturer,
    IReadOnlyList<string> ModelNumbers,
    string? EquipmentCategory,
    string RawJsonResponse,
    EquipmentAssetStructure? AssetStructure = null);