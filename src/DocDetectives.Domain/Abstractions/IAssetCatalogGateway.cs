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

    Task<Asset?> FindAssetBySerialNumberAsync(
        string serialNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Direct children of an asset in the hierarchy (assets whose parent is the given asset).
    /// </summary>
    Task<IReadOnlyList<Asset>> GetChildAssetsAsync(
        string parentAssetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the assets (e.g. a root asset and its sub-assets) already linked to the given manual.
    /// Assets whose Id already exists are updated instead.
    /// </summary>
    Task CreateAssetsAsync(
        IReadOnlyList<Asset> assets,
        string manualId,
        CancellationToken cancellationToken = default);

    Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default);
}