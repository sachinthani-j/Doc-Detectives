using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

public interface IJobGateway
{
    /// <summary>
    /// Creates the job, or replaces it if a job with the same Id already exists.
    /// </summary>
    Task CreateJobAsync(Job job, CancellationToken cancellationToken = default);
}
