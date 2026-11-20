using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

/// <summary>Stores PM models drafted from manuals and approved by a person (in this app; not linked to an EAM).</summary>
public interface IPmModelGateway
{
    /// <summary>Saves the PM model, replacing any stored model with the same Id (and all its jobs and lines).</summary>
    Task SaveAsync(PmModel model, CancellationToken cancellationToken = default);

    /// <summary>Whether a PM model with this Id is already stored (approving a new draft replaces it).</summary>
    Task<bool> ExistsAsync(string pmModelId, CancellationToken cancellationToken = default);

    /// <summary>PM models on this asset, plus models that have a PM job on it (when it is a sub-asset).</summary>
    Task<IReadOnlyList<PmModel>> GetForAssetAsync(string assetId, CancellationToken cancellationToken = default);
}
