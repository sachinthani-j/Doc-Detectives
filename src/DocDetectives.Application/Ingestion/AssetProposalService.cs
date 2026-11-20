using DocDetectives.Application.Services;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

/// <summary>
/// Creates a proposed asset tree once a person has approved it (possibly after editing it).
/// Until then nothing is written: not the assets, and not new entries in the characteristic master table.
/// </summary>
public sealed class AssetProposalService(
    IAssetCatalogGateway assetCatalog,
    CharacteristicService characteristicService)
{
    /// <summary>How the proposal's characteristics map onto the master table right now (nothing is saved).</summary>
    public Task<CharacteristicResolution> PreviewCharacteristicsAsync(
        AssetProposal proposal,
        CancellationToken cancellationToken = default) =>
        characteristicService.PreviewAsync(
            proposal.Characteristics, proposal.Assets[0].Category, proposal.SourceFileName, cancellationToken);

    /// <summary>Whether an asset with this ID already exists (e.g. approved in an earlier run).</summary>
    public async Task<bool> ExistsAsync(string assetId, CancellationToken cancellationToken = default) =>
        await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken) is not null;

    /// <summary>
    /// Saves new characteristics to the master table, then creates the root asset and its sub-assets in one upload,
    /// with the characteristic values already on the root asset.
    /// </summary>
    public async Task<IReadOnlyList<Asset>> ApproveAsync(
        AssetProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var assets = proposal.Assets.ToList();

        if (proposal.Characteristics.Count > 0)
        {
            var resolution = await characteristicService.ResolveAndSaveMasterAsync(
                proposal.Characteristics, assets[0].Category, proposal.SourceFileName, cancellationToken);

            assets[0] = assets[0] with { Characteristics = resolution.Values };
        }

        await assetCatalog.CreateAssetsAsync(assets, proposal.ManualId, cancellationToken);
        return assets;
    }
}
