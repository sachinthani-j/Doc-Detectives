using DocDetectives.Application.Services;
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
    AssetReferenceResolver assetResolver,
    IAssetCatalogGateway assetCatalog,
    IJobGateway jobGateway)
{
    public async Task<InspectionProcessingReport> ProcessAsync(
        ExtractedManual document,
        double autoLinkThreshold,
        CancellationToken cancellationToken = default)
    {
        var inspection = await extractionSkill.ExtractAsync(document, cancellationToken);
        var reference = new AssetReference(
            inspection.AssetId, inspection.SerialNumber, inspection.AssetName, inspection.Manufacturer, inspection.ModelNumber);
        var (asset, resolution) = await assetResolver.ResolveAsync(document, reference, autoLinkThreshold, cancellationToken);

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
        var subAssets = await ComponentAssetMatcher.GetSubAssetsAsync(assetCatalog, asset.Id, cancellationToken);
        var jobLines = lines.Select((line, index) => new JobLine(
            LineNumber: index + 1,
            AssetId: ComponentAssetMatcher.Find(line.Component, subAssets)?.Id ?? asset.Id,
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

}
