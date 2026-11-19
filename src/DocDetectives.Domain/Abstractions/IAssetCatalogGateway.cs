using DocDetectives.Domain.Entities;

namespace DocDetectives.Domain.Abstractions;

public interface IAssetCatalogGateway
{
    Task<IReadOnlyList<Asset>> SearchAssetsAsync(
        string? manufacturer,
        string? modelKeyword,
        CancellationToken cancellationToken = default);

    Task<Asset?> GetAssetByIdAsync(
        string assetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the asset already linked to the given manual, or updates it if an asset with the same Id exists.
    /// </summary>
    Task<Asset> CreateAssetAsync(
        Asset asset,
        string manualId,
        CancellationToken cancellationToken = default);

    Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default);
}