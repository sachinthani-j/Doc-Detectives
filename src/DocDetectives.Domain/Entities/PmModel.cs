namespace DocDetectives.Domain.Entities;

public static class PmJobType
{
    /// <summary>The PM job has at least one task line (lubricate, replace, clean...).</summary>
    public const string PeriodicalMaintenance = "Periodical Maintenance";

    /// <summary>Every line of the PM job is a check to record.</summary>
    public const string Inspection = "Inspection";
}

/// <summary>
/// A preventive maintenance (PM) model on an asset, drafted from a manual and created once a person approves it.
/// Stored in this app only (not linked to an EAM): a planner can still set the start date and schedule.
/// </summary>
/// <param name="Id">PM model code; stable per manual and interval, so re-approving a revised manual updates the same model.</param>
/// <param name="AssetId">The equipment the PM model belongs to.</param>
/// <param name="FrequencyNumber">e.g. 4 with unit "Weeks"; null when the manual states no frequency.</param>
/// <param name="FrequencyStartDate">First due date (ISO yyyy-MM-dd); null until a planner sets it.</param>
/// <param name="Jobs">PM jobs: one for the asset itself and one for each sub-asset that has lines of its own.</param>
public sealed record PmModel(
    string Id,
    string AssetId,
    string Description,
    int? FrequencyNumber,
    string? FrequencyUnit,
    string? Frequency,
    string? FrequencyStartDate,
    string SourceDocumentId,
    string SourceFileName,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<PmJob> Jobs);

/// <summary>One PM job of a PM model: the work on one piece of equipment, with its task and inspection lines.</summary>
/// <param name="JobCode">"001", "002", ... within the PM model.</param>
/// <param name="AssetId">The equipment this job is about: the model's asset or one of its sub-assets.</param>
/// <param name="JobType">See <see cref="PmJobType"/>.</param>
/// <param name="Lines">Lines numbered 1..n within this job.</param>
public sealed record PmJob(
    string JobCode,
    string AssetId,
    string? Component,
    string Description,
    string JobType,
    IReadOnlyList<JobPlanLine> Lines);
