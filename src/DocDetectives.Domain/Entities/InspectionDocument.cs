namespace DocDetectives.Domain.Entities;

public enum DocumentType
{
    Manual,
    Inspection
}

public sealed record DocumentClassification(
    DocumentType DocumentType,
    string Reason);

/// <summary>
/// Data extracted from an inspection document: which asset it is about, plus the inspection lines.
/// The asset reference fields are whatever the document states; any of them may be missing.
/// </summary>
public sealed record InspectionDocument(
    string? Title,
    string? AssetId,
    string? SerialNumber,
    string? AssetName,
    string? Manufacturer,
    string? ModelNumber,
    string? InspectionDate,
    string? Inspector,
    List<InspectionLine> Lines);

/// <summary>
/// One inspection step. Description is always set; the other fields are filled only when the document has them
/// (a blank checklist has none, a completed report has a result and often a measurement or remarks).
/// Component is the part of the equipment the check is about (e.g. "Drive Train"); null for the whole equipment.
/// </summary>
public sealed record InspectionLine(
    string Description,
    string? Criteria,
    string? Result,
    string? Measurement,
    string? Remarks,
    string? Component);
