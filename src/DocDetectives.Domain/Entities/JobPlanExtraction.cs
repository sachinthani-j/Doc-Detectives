namespace DocDetectives.Domain.Entities;

/// <summary>What the LLM extracts from a document: zero or more job plans plus document-wide information.</summary>
/// <param name="DocumentCode">The document's own code, e.g. "JD-PL3-PREV-DRO-001" or "1-365-PK-00030-BL".</param>
/// <param name="InspectedAsset">For an inspection document: what it states about the inspected equipment; null for a manual.</param>
public sealed record JobPlanExtraction(
    string? DocumentCode,
    List<string> Materials,
    List<string> SafetyNotes,
    List<ExtractedJobPlan> Plans,
    AssetReference? InspectedAsset);

/// <param name="IntervalUnit">One of Hours, Days, Weeks, Months, Years; null when the document gives no frequency.</param>
public sealed record ExtractedJobPlan(
    string Title,
    string? Interval,
    int? IntervalValue,
    string? IntervalUnit,
    List<ExtractedJobPlanLine> Lines);

/// <param name="LineType">"Task" or "Inspection".</param>
/// <param name="EquipmentCode">Machine code the line is about, as printed (e.g. "PL3_DRO_100_M01"); null if none.</param>
/// <param name="Component">Machine or part name the line is about (e.g. "Drogerband 1", "Drive Train"); null for the whole equipment.</param>
public sealed record ExtractedJobPlanLine(
    string Title,
    string LineType,
    string? Instruction,
    string? EquipmentCode,
    string? Component,
    string? MeasurementPoint,
    string? NominalValue,
    string? MeasurementCode,
    List<string> AllowedResults);
