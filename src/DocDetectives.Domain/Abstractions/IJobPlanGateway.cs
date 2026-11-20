using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

/// <summary>
/// Stores job plans that a person has approved. Drafts are never stored: they live only until they are reviewed.
/// </summary>
public interface IJobPlanGateway
{
    /// <summary>Status of the stored plan with this Id, or null when no plan with this Id is stored.</summary>
    Task<string?> GetStatusAsync(string jobPlanId, CancellationToken cancellationToken = default);

    /// <summary>Saves the plans, replacing any stored plan with the same Id.</summary>
    Task SaveAsync(IReadOnlyList<JobPlan> plans, CancellationToken cancellationToken = default);
}
