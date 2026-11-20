namespace DocDetectives.Domain.Entities;

public static class JobPlanStatus
{
    /// <summary>Proposed by AI from a document; exists only until a person reviews it, and is never stored.</summary>
    public const string Draft = "Draft";

    /// <summary>Reviewed (and possibly edited) and approved by a person; only approved plans are stored.</summary>
    public const string Approved = "Approved";
}

public static class JobPlanLineType
{
    /// <summary>Something to do (EAM "Taak"), e.g. "Lubricate all bearings with SWEPCO 116".</summary>
    public const string Task = "Task";

    /// <summary>Something to check and record (EAM "Inspecteren"), e.g. "Bearings lubricated: OK / NOK".</summary>
    public const string Inspection = "Inspection";
}

/// <summary>
/// The "what to do" part of a preventive maintenance model, drafted from a manual or an inspection document.
/// Scheduling (frequency is suggested when the document states it) is completed by a planner in the EAM.
/// </summary>
/// <param name="Id">Stable per document code and interval, so a revised document updates its draft.</param>
/// <param name="AssetId">The asset the plan belongs to, when known. Lines may point to sub-assets.</param>
/// <param name="IntervalValue">Suggested frequency from the document, e.g. 4 with unit "Weeks"; null when not stated.</param>
public sealed record JobPlan(
    string Id,
    string Title,
    string Status,
    DocumentType SourceType,
    string SourceDocumentId,
    string SourceFileName,
    string? DocumentCode,
    string? AssetId,
    string? Manufacturer,
    IReadOnlyList<string> ModelNumbers,
    string? Interval,
    int? IntervalValue,
    string? IntervalUnit,
    IReadOnlyList<string> Materials,
    IReadOnlyList<string> SafetyNotes,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<JobPlanLine> Lines);

/// <summary>One job plan line, in the EAM's job plan format (number, title, type, work instruction, measuring data).</summary>
/// <param name="AllowedResults">Answers the technician can record, e.g. ["OK", "NOK"]; empty for a task.</param>
public sealed record JobPlanLine(
    int LineNumber,
    string Title,
    string LineType,
    string? Instruction,
    string? EquipmentCode,
    string? Component,
    string? AssetId,
    string? MeasurementPoint,
    string? NominalValue,
    string? MeasurementCode,
    IReadOnlyList<string> AllowedResults);
