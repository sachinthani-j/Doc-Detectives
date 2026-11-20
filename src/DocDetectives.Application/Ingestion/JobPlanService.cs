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
/// Drafts PM maintenance from a manual or inspection document (one draft per interval, each with its task and
/// inspection lines linked to the asset they are about). Nothing is stored until a person approves a draft:
/// <see cref="CreatePmModelAsync"/> then creates the PM model with its PM jobs and lines.
/// </summary>
public sealed class JobPlanService(
    JobPlanExtractionSkill extractionSkill,
    AssetReferenceResolver assetResolver,
    IAssetCatalogGateway assetCatalog,
    IPmModelGateway pmModelGateway)
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
        // A manual first decides whether the equipment warrants PM models at all ("when in doubt, leave it out").
        if (documentType == DocumentType.Manual && extraction.PmModelNeeded == false)
        {
            var reason = string.IsNullOrWhiteSpace(extraction.PmModelReason)
                ? "the equipment does not need recurring maintenance in Ultimo."
                : extraction.PmModelReason.Trim();
            return new JobPlanReport([], $"No PM model: {reason}", assetResolution);
        }

        var plansWithLines = (extraction.Plans ?? [])
            .Where(p => p.Lines?.Any(l => !string.IsNullOrWhiteSpace(l.Title)) == true)
            .ToList();

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
            var id = $"PM-{baseId}-{suffix}";
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

        var note = $"Drafted {plans.Count} PM model(s) with {plans.Sum(p => p.Lines.Count)} line(s)" +
                   (assetId is null ? " (no asset linked)" : $" on asset '{assetId}'") +
                   "; waiting for approval.";

        return new JobPlanReport(plans, note, assetResolution);
    }

    /// <summary>
    /// PM model code of a draft on one asset: the draft's code plus the asset, so the same manual's PM model on
    /// several assets never overwrites itself, and re-approving a revised manual updates the same model.
    /// </summary>
    public static string PmModelId(string draftId, string assetId) =>
        $"{draftId}-{Regex.Replace(assetId.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-')}";

    /// <summary>Whether a PM model for this draft is already stored on any of these assets (approving replaces it).</summary>
    public async Task<bool> PmModelExistsAsync(
        string draftId,
        IReadOnlyList<string> assetIds,
        CancellationToken cancellationToken = default)
    {
        foreach (var assetId in assetIds)
        {
            if (await pmModelGateway.ExistsAsync(PmModelId(draftId, assetId), cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Creates the PM model of an approved draft on every given asset (the assets the manual is linked to), with the
    /// same PM jobs and lines on each. On the draft's own asset the reviewed line links are kept; on another asset,
    /// a line about a component is linked to that asset's own matching sub-asset (otherwise to the asset itself).
    /// </summary>
    public async Task<IReadOnlyList<PmModel>> CreatePmModelsAsync(
        JobPlan draft,
        IReadOnlyList<string> assetIds,
        CancellationToken cancellationToken = default)
    {
        var models = new List<PmModel>();
        foreach (var assetId in assetIds.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal))
        {
            var lines = draft.Lines;
            if (!string.Equals(assetId, draft.AssetId, StringComparison.Ordinal))
            {
                var subAssets = await ComponentAssetMatcher.GetSubAssetsAsync(assetCatalog, assetId, cancellationToken);
                lines = draft.Lines
                    .Select(line => line with
                    {
                        AssetId = ComponentAssetMatcher.Find(line.Component ?? line.EquipmentCode, subAssets)?.Id ?? assetId
                    })
                    .ToList();
            }

            var onAsset = draft with
            {
                Id = PmModelId(draft.Id, assetId),
                Status = JobPlanStatus.Approved,
                AssetId = assetId,
                Lines = lines
            };

            if (PmModelBuilder.Build(onAsset) is { } model)
            {
                await pmModelGateway.SaveAsync(model, cancellationToken);
                models.Add(model);
            }
        }

        return models;
    }

    // Azure Search key-safe (letters, digits, '-') and deterministic, so a revised document updates its draft.
    private static string Clean(string? value)
    {
        var cleaned = Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
        return cleaned.Length <= MaxIdPartLength ? cleaned : cleaned[..MaxIdPartLength].TrimEnd('-');
    }
}
