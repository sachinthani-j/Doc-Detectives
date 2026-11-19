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

    Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default);
}