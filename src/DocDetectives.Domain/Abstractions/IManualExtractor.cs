using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

public interface IManualExtractor
{
    Task<ExtractedManual> ExtractAsync(
        Stream fileStream,
        string fileName,
        CancellationToken cancellationToken = default);
}