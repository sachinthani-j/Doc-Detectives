using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Services;

/// <summary>
/// Turns an approved PM draft (drafted from a manual) into a PM model: one PM job for the plan's asset, plus one PM job per sub-asset
/// that has lines of its own (a PM job is always about one piece of equipment).
/// </summary>
public static class PmModelBuilder
{
    /// <summary>The PM model for the draft, or null when the draft has no asset (a PM model needs equipment).</summary>
    public static PmModel? Build(JobPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.AssetId) || plan.Lines.Count == 0)
        {
            return null;
        }

        var assetId = plan.AssetId;

        // Lines without their own asset belong to the plan's asset; the plan's asset always comes first.
        var groups = plan.Lines
            .GroupBy(line => line.AssetId ?? assetId, StringComparer.Ordinal)
            .OrderBy(g => g.Key == assetId ? 0 : 1)
            .ToList();

        var jobs = groups.Select((group, index) =>
        {
            var lines = group.Select((line, i) => line with { LineNumber = i + 1 }).ToList();
            var onMainAsset = group.Key == assetId;
            var component = onMainAsset ? null : lines.Select(l => l.Component).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

            return new PmJob(
                JobCode: (index + 1).ToString("000"),
                AssetId: group.Key,
                Component: component,
                Description: onMainAsset ? plan.Title : $"{plan.Title} - {component ?? group.Key}",
                JobType: lines.All(l => l.LineType == JobPlanLineType.Inspection)
                    ? PmJobType.Inspection
                    : PmJobType.PeriodicalMaintenance,
                Lines: lines);
        }).ToList();

        return new PmModel(
            Id: plan.Id,
            AssetId: assetId,
            Description: plan.Title,
            FrequencyNumber: plan.IntervalValue,
            FrequencyUnit: plan.IntervalUnit,
            Frequency: plan.Interval,
            FrequencyStartDate: null,
            SourceDocumentId: plan.SourceDocumentId,
            SourceFileName: plan.SourceFileName,
            CreatedUtc: DateTimeOffset.UtcNow,
            Jobs: jobs);
    }
}
