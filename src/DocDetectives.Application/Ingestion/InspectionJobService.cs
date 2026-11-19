using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

public sealed record InspectionProcessingReport(
    InspectionDocument Inspection,
    Asset? Asset,
    string AssetResolution,
    Job? Job,
    string JobNote);

/// <summary>
/// Turns an inspection document into a job on the inspected asset, with one job line per inspection point.
/// </summary>
public sealed class InspectionJobService(
    InspectionExtractionSkill extractionSkill,
    AssetMatchingSkill matchingSkill,
    IAssetCatalogGateway assetCatalog,
    IJobGateway jobGateway)
{
    public async Task<InspectionProcessingReport> ProcessAsync(
        ExtractedManual document,
        double autoLinkThreshold,
        CancellationToken cancellationToken = default)
    {
        var inspection = await extractionSkill.ExtractAsync(document, cancellationToken);
        var (asset, resolution) = await ResolveAssetAsync(document, inspection, autoLinkThreshold, cancellationToken);

        // A job needs a confirmed asset: creating one against a guessed asset would put work on the wrong equipment.
        if (asset is null)
        {
            return new InspectionProcessingReport(inspection, null, resolution, null,
                "Skipped: no job created because the inspected asset could not be identified with confidence.");
        }

        var lines = inspection.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Description)).ToList();
        if (lines.Count == 0)
        {
            return new InspectionProcessingReport(inspection, asset, resolution, null,
                "Skipped: no inspection lines found in the document.");
        }

        // A completed report (every line has a result) becomes a completed job; a checklist becomes an open job.
        var status = lines.All(l => !string.IsNullOrWhiteSpace(l.Result)) ? JobStatus.Completed : JobStatus.Open;

        // Link every line to the equipment it inspects: the matching sub-asset, or else the job's own asset.
        var subAssets = await GetSubAssetsAsync(asset.Id, cancellationToken);
        var jobLines = lines.Select((line, index) => new JobLine(
            LineNumber: index + 1,
            AssetId: FindComponentAsset(line.Component, subAssets)?.Id ?? asset.Id,
            Component: line.Component,
            Description: line.Description.Trim(),
            Criteria: line.Criteria,
            Result: line.Result,
            Measurement: line.Measurement,
            Remarks: line.Remarks)).ToList();

        var job = new Job(
            Id: $"JOB-{document.ManualId}",
            AssetId: asset.Id,
            Title: string.IsNullOrWhiteSpace(inspection.Title) ? $"Inspection - {asset.Name}" : inspection.Title.Trim(),
            JobType: "Inspection",
            Status: status,
            SourceDocumentId: document.ManualId,
            SourceFileName: document.FileName,
            InspectionDate: inspection.InspectionDate,
            Inspector: inspection.Inspector,
            CreatedUtc: DateTimeOffset.UtcNow,
            Lines: jobLines);

        await jobGateway.CreateJobAsync(job, cancellationToken);

        var onSubAssets = jobLines.Count(l => l.AssetId != asset.Id);
        return new InspectionProcessingReport(inspection, asset, resolution, job,
            $"Created job '{job.Id}' ({job.Status}) with {jobLines.Count} line(s); " +
            $"{onSubAssets} linked to a sub-asset, {jobLines.Count - onSubAssets} to the asset itself.");
    }

    // Children and grandchildren: the hierarchy is at most three levels deep.
    private async Task<IReadOnlyList<Asset>> GetSubAssetsAsync(string assetId, CancellationToken cancellationToken)
    {
        var children = await assetCatalog.GetChildAssetsAsync(assetId, cancellationToken);
        var all = new List<Asset>(children);
        foreach (var child in children)
        {
            all.AddRange(await assetCatalog.GetChildAssetsAsync(child.Id, cancellationToken));
        }

        return all;
    }

    // Matches the component named on the inspection line to a sub-asset name.
    // Sub-asset names follow "Component - Equipment", so only the part before " - " is compared.
    // Exact match wins; otherwise a name that contains the other (e.g. "Fan Motor" vs "Motor"), preferring the longest.
    private static Asset? FindComponentAsset(string? component, IReadOnlyList<Asset> subAssets)
    {
        var wanted = Normalize(component);
        if (wanted.Length == 0 || subAssets.Count == 0)
        {
            return null;
        }

        var candidates = subAssets
            .Select(a => (Asset: a, Name: Normalize(a.Name.Split(" - ")[0])))
            .Where(c => c.Name.Length > 0)
            .ToList();

        return candidates.FirstOrDefault(c => c.Name == wanted).Asset
               ?? candidates
                   .Where(c => Math.Min(c.Name.Length, wanted.Length) >= 4 &&
                               (c.Name.Contains(wanted) || wanted.Contains(c.Name)))
                   .OrderByDescending(c => c.Name.Length)
                   .FirstOrDefault().Asset
               // Not a sub-asset itself? Then the sub-asset that holds it as a spare part ("Spray nozzles").
               ?? subAssets.FirstOrDefault(a => (a.SpareParts ?? []).Any(p =>
                   Normalize(p.Split(" | ")[^1]) is var part && part.Length >= 4 &&
                   (part == wanted || part.Contains(wanted) || wanted.Contains(part))));
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // Most reliable first: the asset number on the document, then the serial number, then AI matching.
    private async Task<(Asset? Asset, string Resolution)> ResolveAssetAsync(
        ExtractedManual document,
        InspectionDocument inspection,
        double autoLinkThreshold,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(inspection.AssetId) &&
            await assetCatalog.GetAssetByIdAsync(inspection.AssetId.Trim(), cancellationToken) is { } byId)
        {
            return (byId, $"Matched by asset ID '{inspection.AssetId.Trim()}'.");
        }

        if (!string.IsNullOrWhiteSpace(inspection.SerialNumber) &&
            await assetCatalog.FindAssetBySerialNumberAsync(inspection.SerialNumber.Trim(), cancellationToken) is { } bySerial)
        {
            return (bySerial, $"Matched by serial number '{inspection.SerialNumber.Trim()}'.");
        }

        // AI matching on manufacturer / model / name. Links are not stored: an inspection document is not a manual.
        var searchable = document with
        {
            Manufacturer = inspection.Manufacturer ?? document.Manufacturer,
            ModelNumbers = string.IsNullOrWhiteSpace(inspection.ModelNumber) ? [] : [inspection.ModelNumber.Trim()],
            EquipmentCategory = inspection.AssetName ?? document.EquipmentCategory
        };

        var matches = await matchingSkill.MatchManualToAssetsAsync(
            searchable,
            autoLinkThreshold,
            persistLinks: false,
            cancellationToken);

        var best = matches
            .Where(m => m.Status == MappingStatus.AutoLinked)
            .OrderByDescending(m => m.ConfidenceScore)
            .FirstOrDefault();

        if (best is not null && await assetCatalog.GetAssetByIdAsync(best.AssetId, cancellationToken) is { } matched)
        {
            return (matched, $"Matched by AI ({best.ConfidenceScore:P0}): {best.Reasoning}");
        }

        var reviewCount = matches.Count(m => m.Status == MappingStatus.NeedsReview);
        var stated = inspection.AssetId ?? inspection.SerialNumber ?? inspection.AssetName ?? "nothing";
        return (null, reviewCount > 0
            ? $"Not matched: {reviewCount} possible asset(s) need review (document states: {stated})."
            : $"Not matched: no asset found (document states: {stated}).");
    }
}
