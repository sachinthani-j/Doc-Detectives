using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using System.Text.RegularExpressions;

namespace DocDetectives.Application.Ingestion;

/// <param name="Plans">Drafted plans, not saved yet: they wait for a person to approve them.</param>
/// <param name="AssetResolution">How the plan's asset was found (inspection documents); null when it was given.</param>
public sealed record JobPlanReport(
    IReadOnlyList<JobPlan> Plans,
    string Note,
    string? AssetResolution = null);

/// <summary>
/// Drafts job plans from a manual or inspection document and links each line to the asset it is about.
/// Drafts are only saved once a person approves them (see <see cref="SaveApprovedAsync"/>).
/// </summary>
public sealed class JobPlanService(
    JobPlanExtractionSkill extractionSkill,
    AssetReferenceResolver assetResolver,
    IAssetCatalogGateway assetCatalog,
    IJobPlanGateway jobPlanGateway)
{
    private const int MaxIdPartLength = 60;

    /// <param name="assetId">The asset the document belongs to (matched or created); null when unknown.</param>
    public async Task<JobPlanReport> DraftAsync(
        ExtractedManual document,
        DocumentType documentType,
        string? assetId,
        double autoLinkThreshold = 0.85,
        CancellationToken cancellationToken = default)
    {
        var extraction = await extractionSkill.ExtractAsync(document, documentType, cancellationToken);

        // An inspection document names its equipment: find that asset (asset number, serial number, then AI match).
        string? assetResolution = null;
        if (assetId is null && documentType == DocumentType.Inspection && extraction.InspectedAsset is { } reference)
        {
            var (asset, resolution) = await assetResolver.ResolveAsync(document, reference, autoLinkThreshold, cancellationToken);
            assetId = asset?.Id;
            assetResolution = resolution;
        }
        var plansWithLines = extraction.Plans.Where(p => p.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Title))).ToList();

        if (plansWithLines.Count == 0)
        {
            return new JobPlanReport([], documentType == DocumentType.Manual
                ? "None: the manual has no recurring maintenance schedule."
                : "None: no checklist steps found in the document.", assetResolution);
        }

        var subAssets = assetId is null
            ? []
            : await ComponentAssetMatcher.GetSubAssetsAsync(assetCatalog, assetId, cancellationToken);

        // Stable IDs from the document's own code (falls back to the file-content ID), plus the interval.
        var baseId = Clean(extraction.DocumentCode) is { Length: > 0 } code ? code : Clean(document.ManualId);
        var usedIds = new HashSet<string>();
        var plans = new List<JobPlan>();

        for (var i = 0; i < plansWithLines.Count; i++)
        {
            var extracted = plansWithLines[i];
            var suffix = Clean(extracted.Interval) is { Length: > 0 } interval ? interval : (i + 1).ToString("00");
            var id = $"PLAN-{baseId}-{suffix}";
            if (!usedIds.Add(id))
            {
                id = $"{id}-{i + 1}";
                usedIds.Add(id);
            }

            var lines = extracted.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Title))
                .Select((line, index) => new JobPlanLine(
                    LineNumber: index + 1,
                    Title: line.Title.Trim(),
                    LineType: string.Equals(line.LineType, JobPlanLineType.Task, StringComparison.OrdinalIgnoreCase)
                        ? JobPlanLineType.Task
                        : JobPlanLineType.Inspection,
                    Instruction: line.Instruction,
                    EquipmentCode: line.EquipmentCode,
                    Component: line.Component,
                    // The sub-asset the line is about; otherwise the plan's own asset (null when unknown).
                    AssetId: ComponentAssetMatcher.Find(line.Component ?? line.EquipmentCode, subAssets)?.Id ?? assetId,
                    MeasurementPoint: line.MeasurementPoint,
                    NominalValue: line.NominalValue,
                    MeasurementCode: line.MeasurementCode,
                    AllowedResults: (line.AllowedResults ?? [])
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Select(r => r.Trim())
                        .ToList()))
                .ToList();

            plans.Add(new JobPlan(
                Id: id,
                Title: extracted.Title.Trim(),
                Status: JobPlanStatus.Draft,
                SourceType: documentType,
                SourceDocumentId: document.ManualId,
                SourceFileName: document.FileName,
                DocumentCode: extraction.DocumentCode,
                AssetId: assetId,
                Manufacturer: document.Manufacturer,
                ModelNumbers: document.ModelNumbers,
                Interval: extracted.Interval,
                IntervalValue: extracted.IntervalValue,
                IntervalUnit: extracted.IntervalUnit,
                Materials: extraction.Materials ?? [],
                SafetyNotes: extraction.SafetyNotes ?? [],
                CreatedUtc: DateTimeOffset.UtcNow,
                Lines: lines));
        }

        var note = $"Drafted {plans.Count} job plan(s) with {plans.Sum(p => p.Lines.Count)} line(s)" +
                   (assetId is null ? " (no asset linked)" : $" on asset '{assetId}'") +
                   "; waiting for approval.";

        return new JobPlanReport(plans, note, assetResolution);
    }

    /// <summary>Status of an already stored plan with this Id (e.g. "Approved"), or null when there is none.</summary>
    public Task<string?> GetStoredStatusAsync(string jobPlanId, CancellationToken cancellationToken = default) =>
        jobPlanGateway.GetStatusAsync(jobPlanId, cancellationToken);

    /// <summary>Saves a plan a person has approved (possibly after editing it). Lines are renumbered 1..n.</summary>
    public async Task<JobPlan> SaveApprovedAsync(JobPlan plan, CancellationToken cancellationToken = default)
    {
        var approved = plan with
        {
            Status = JobPlanStatus.Approved,
            Lines = plan.Lines.Select((line, index) => line with { LineNumber = index + 1 }).ToList()
        };

        await jobPlanGateway.SaveAsync([approved], cancellationToken);
        return approved;
    }

    // Azure Search key-safe (letters, digits, '-') and deterministic, so a revised document updates its draft.
    private static string Clean(string? value)
    {
        var cleaned = Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
        return cleaned.Length <= MaxIdPartLength ? cleaned : cleaned[..MaxIdPartLength].TrimEnd('-');
    }
}
