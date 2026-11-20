using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

public sealed record CharacteristicReport(
    IReadOnlyList<AssetCharacteristic> Filled,
    int SkippedExisting,
    int NewDefinitions,
    string Note);

/// <summary>
/// Fills an asset's characteristics from its manual, straight onto the asset (no approval step).
/// Only empty characteristics are filled; characteristics not yet in the master table are added to it.
/// </summary>
public sealed class CharacteristicService(
    CharacteristicExtractionSkill extractionSkill,
    ICharacteristicCatalog catalog,
    IAssetCatalogGateway assetCatalog)
{
    private readonly SemaphoreSlim _masterLock = new(1, 1);

    // Entries saved during this run, in case the search index has not made them visible yet.
    private readonly Dictionary<string, CharacteristicDefinition> _savedThisRun = [];

    /// <summary>Extract, resolve against the master table and fill an existing asset (only empty values).</summary>
    public async Task<CharacteristicReport> FillFromManualAsync(
        string assetId,
        ExtractedManual manual,
        CancellationToken cancellationToken = default)
    {
        if ((manual.AssetStructure?.TechnicalSpecifications ?? []).Count == 0)
        {
            return new CharacteristicReport([], 0, 0, "None: the manual has no technical specifications.");
        }

        // An asset created moments ago can take a second or two to become searchable.
        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        for (var attempt = 1; asset is null && attempt <= 3; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        }

        if (asset is null)
        {
            return new CharacteristicReport([], 0, 0, $"Skipped: asset '{assetId}' not found.");
        }

        var extracted = await ExtractAsync(asset, manual, cancellationToken);
        var resolution = await ResolveAndSaveMasterAsync(extracted, asset.Category, manual.FileName, cancellationToken);

        var merge = CharacteristicResolver.MergeIntoAsset(asset.Characteristics, resolution.Values);
        if (merge.Filled.Count > 0)
        {
            await assetCatalog.UpdateCharacteristicsAsync(asset.Id, merge.Merged, cancellationToken);
        }

        var note = $"Filled {merge.Filled.Count} characteristic(s) on asset '{asset.Id}'" +
                   (merge.SkippedExisting > 0 ? $", kept {merge.SkippedExisting} existing value(s)" : "") +
                   (resolution.NewDefinitionCount > 0 ? $", added {resolution.NewDefinitionCount} new to the master table" : "") +
                   ".";

        return new CharacteristicReport(merge.Filled, merge.SkippedExisting, resolution.NewDefinitionCount, note);
    }

    /// <summary>
    /// Asks the AI for characteristic values of an asset (existing or proposed), using the master table for names
    /// and units. Nothing is saved.
    /// </summary>
    public async Task<IReadOnlyList<ExtractedCharacteristic>> ExtractAsync(
        Asset asset,
        ExtractedManual manual,
        CancellationToken cancellationToken = default)
    {
        var specifications = manual.AssetStructure?.TechnicalSpecifications ?? [];
        if (specifications.Count == 0)
        {
            return [];
        }

        // The AI call runs outside the master lock so parallel documents don't wait on each other.
        var extraction = await extractionSkill.ExtractAsync(
            asset, manual, specifications, await LoadMasterAsync(cancellationToken), cancellationToken);

        return extraction.Characteristics ?? [];
    }

    /// <summary>What the values would look like against the current master table, without saving anything (for review).</summary>
    public async Task<CharacteristicResolution> PreviewAsync(
        IReadOnlyList<ExtractedCharacteristic> extracted,
        string? equipmentType,
        string sourceFileName,
        CancellationToken cancellationToken = default) =>
        CharacteristicResolver.Resolve(
            extracted, await LoadMasterAsync(cancellationToken), equipmentType, sourceFileName, DateTimeOffset.UtcNow);

    /// <summary>
    /// Resolves the values against the master table and saves new or updated master entries.
    /// Returns the values ready to put on the asset.
    /// </summary>
    public async Task<CharacteristicResolution> ResolveAndSaveMasterAsync(
        IReadOnlyList<ExtractedCharacteristic> extracted,
        string? equipmentType,
        string sourceFileName,
        CancellationToken cancellationToken = default)
    {
        // One document at a time updates the master table, so two manuals that find the same new
        // characteristic at once still end up with a single master entry.
        await _masterLock.WaitAsync(cancellationToken);
        try
        {
            var resolution = CharacteristicResolver.Resolve(
                extracted,
                await LoadMasterAsync(cancellationToken),
                equipmentType,
                sourceFileName,
                DateTimeOffset.UtcNow);

            await catalog.SaveAsync(resolution.DefinitionsToSave, cancellationToken);
            foreach (var definition in resolution.DefinitionsToSave)
            {
                _savedThisRun[definition.Id] = definition;
            }

            return resolution;
        }
        finally
        {
            _masterLock.Release();
        }
    }

    private async Task<IReadOnlyList<CharacteristicDefinition>> LoadMasterAsync(CancellationToken cancellationToken)
    {
        var stored = await catalog.GetAllAsync(cancellationToken);
        var byId = stored.ToDictionary(d => d.Id);
        foreach (var (id, definition) in _savedThisRun)
        {
            byId[id] = definition; // this run's version is the newest
        }

        return byId.Values.ToList();
    }
}
