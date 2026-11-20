using DocDetectives.Application.Services;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

/// <summary>
/// Creates a proposed asset tree once a person has approved it (possibly after editing it).
/// Until then nothing is written: not the assets, and not new entries in the characteristic master table.
/// Existing EAM assets in a proposal are never rewritten; they only get the manual linked to them.
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
    /// Saves new characteristics to the master table, then creates the new assets in one upload, with the
    /// characteristic values on the root asset when the root is new. Existing assets in the proposal are left as
    /// they are and only linked to the manual. Returns the assets that were created.
    /// </summary>
    public async Task<IReadOnlyList<Asset>> ApproveAsync(
        AssetProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var assets = proposal.NewAssets.ToList();
        var rootIsNew = assets.Count > 0 && assets[0].Id == proposal.Assets[0].Id;

        if (rootIsNew && proposal.Characteristics.Count > 0)
        {
            var resolution = await characteristicService.ResolveAndSaveMasterAsync(
                proposal.Characteristics, assets[0].Category, proposal.SourceFileName, cancellationToken);

            assets[0] = assets[0] with { Characteristics = resolution.Values };
        }

        if (assets.Count > 0)
        {
            await assetCatalog.CreateAssetsAsync(assets, proposal.ManualId, cancellationToken);
        }

        // Existing sub-assets the manual maps onto get the manual linked too (only their link list changes).
        foreach (var existing in proposal.Assets.Skip(1).Where(a => proposal.IsExisting(a.Id)))
        {
            await assetCatalog.LinkManualToAssetAsync(
                existing.Id, proposal.ManualId, 1.0, MappingStatus.AutoLinked, cancellationToken);
        }

        return assets;
    }
}
