using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

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
    IAssetCatalogGateway assetCatalog,
    ILogger<CharacteristicService> logger)
{
    private readonly SemaphoreSlim _masterLock = new(1, 1);

    // Logs the time of one part of the characteristics step, to see where it spends its time.
    private void LogPart(string fileName, string part, Stopwatch watch, string? detail = null)
    {
        logger.LogInformation(
            "Ingest {File}: characteristics - {Part} took {Seconds:0.0} s{Detail}",
            fileName, part, watch.Elapsed.TotalSeconds, detail is null ? "" : $" ({detail})");
        watch.Restart();
    }

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

        var watch = Stopwatch.StartNew();

        // An asset created moments ago can take a second or two to become searchable.
        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        var retries = 0;
        for (var attempt = 1; asset is null && attempt <= 3; attempt++)
        {
            retries++;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        }

        LogPart(manual.FileName, "asset lookup", watch, retries > 0 ? $"{retries} retry(ies)" : null);

        if (asset is null)
        {
            return new CharacteristicReport([], 0, 0, $"Skipped: asset '{assetId}' not found.");
        }

        var extracted = await ExtractAsync(asset, manual, cancellationToken);
        watch.Restart();

        var resolution = await ResolveAndSaveMasterAsync(extracted, asset.Category, manual.FileName, cancellationToken);
        watch.Restart();

        var merge = CharacteristicResolver.MergeIntoAsset(asset.Characteristics, resolution.Values);
        if (merge.Filled.Count > 0)
        {
            await assetCatalog.UpdateCharacteristicsAsync(asset.Id, merge.Merged, cancellationToken);
        }

        LogPart(manual.FileName, "asset update", watch, $"{merge.Filled.Count} value(s) filled");

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

        var watch = Stopwatch.StartNew();
        var master = await LoadMasterAsync(cancellationToken);
        LogPart(manual.FileName, "master table load", watch, $"{master.Count} entries");

        // The AI call runs outside the master lock so parallel documents don't wait on each other.
        var extraction = await extractionSkill.ExtractAsync(asset, manual, specifications, master, cancellationToken);
        var characteristics = extraction.Characteristics ?? [];
        LogPart(manual.FileName, "AI extraction", watch,
            $"{specifications.Count} spec(s) in, {characteristics.Count} characteristic(s) out");

        return characteristics;
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
        var watch = Stopwatch.StartNew();
        await _masterLock.WaitAsync(cancellationToken);
        LogPart(sourceFileName, "master lock wait", watch);
        try
        {
            var master = await LoadMasterAsync(cancellationToken);
            LogPart(sourceFileName, "master table reload", watch, $"{master.Count} entries");

            var resolution = CharacteristicResolver.Resolve(
                extracted,
                master,
                equipmentType,
                sourceFileName,
                DateTimeOffset.UtcNow);

            await catalog.SaveAsync(resolution.DefinitionsToSave, cancellationToken);
            LogPart(sourceFileName, "master table save", watch, $"{resolution.DefinitionsToSave.Count} definition(s)");
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
