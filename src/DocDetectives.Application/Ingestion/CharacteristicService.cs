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
/// Characteristic values extracted for an existing asset, not saved yet: they wait for a person's consent.
/// </summary>
/// <param name="WouldFill">Values that would go into empty characteristics of the asset.</param>
/// <param name="KeptExisting">Values the asset already has; these are never overwritten.</param>
public sealed record CharacteristicDraft(
    string AssetId,
    string SourceFileName,
    IReadOnlyList<ExtractedCharacteristic> Extracted,
    IReadOnlyList<AssetCharacteristic> WouldFill,
    int KeptExisting,
    int NewDefinitions,
    string Note)
{
    public static CharacteristicDraft Empty(string assetId, string sourceFileName, string note) =>
        new(assetId, sourceFileName, [], [], 0, 0, note);
}

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
        var draft = await DraftForAssetAsync(assetId, manual, cancellationToken);
        return draft.Extracted.Count == 0
            ? new CharacteristicReport([], 0, 0, draft.Note)
            : await SaveDraftAsync(draft, cancellationToken);
    }

    /// <summary>
    /// Extracts characteristic values for an existing asset and shows which empty ones they would fill.
    /// Nothing is saved: not the values, and not new master table entries (see <see cref="SaveDraftAsync"/>).
    /// </summary>
    public async Task<CharacteristicDraft> DraftForAssetAsync(
        string assetId,
        ExtractedManual manual,
        CancellationToken cancellationToken = default)
    {
        if ((manual.AssetStructure?.TechnicalSpecifications ?? []).Count == 0)
        {
            return CharacteristicDraft.Empty(assetId, manual.FileName, "None: the manual has no technical specifications.");
        }

        var asset = await FindAssetAsync(assetId, manual.FileName, cancellationToken);
        if (asset is null)
        {
            return CharacteristicDraft.Empty(assetId, manual.FileName, $"Skipped: asset '{assetId}' not found.");
        }

        var extracted = await ExtractAsync(asset, manual, cancellationToken);
        if (extracted.Count == 0)
        {
            return CharacteristicDraft.Empty(assetId, manual.FileName, "None: no characteristics found in the specifications.");
        }

        return await BuildDraftAsync(asset, manual.FileName, extracted, cancellationToken);
    }

    /// <summary>
    /// The draft again after the person changed or removed values (no AI call): what would be filled now.
    /// Nothing is saved.
    /// </summary>
    public async Task<CharacteristicDraft> ReviseDraftAsync(
        CharacteristicDraft draft,
        IReadOnlyList<ExtractedCharacteristic> extracted,
        CancellationToken cancellationToken = default)
    {
        var asset = await FindAssetAsync(draft.AssetId, draft.SourceFileName, cancellationToken)
                    ?? throw new InvalidOperationException($"asset '{draft.AssetId}' was not found.");
        return await BuildDraftAsync(asset, draft.SourceFileName, extracted, cancellationToken);
    }

    private async Task<CharacteristicDraft> BuildDraftAsync(
        Asset asset,
        string fileName,
        IReadOnlyList<ExtractedCharacteristic> extracted,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(extracted, asset.Category, fileName, cancellationToken);
        var merge = CharacteristicResolver.MergeIntoAsset(asset.Characteristics, preview.Values);

        var note = $"{merge.Filled.Count} empty characteristic(s) on asset '{asset.Id}' would be filled" +
                   (merge.SkippedExisting > 0 ? $", {merge.SkippedExisting} existing value(s) kept" : "") +
                   (preview.NewDefinitionCount > 0 ? $", {preview.NewDefinitionCount} new added to the master table" : "") +
                   ".";

        return new CharacteristicDraft(
            asset.Id, fileName, extracted, merge.Filled, merge.SkippedExisting, preview.NewDefinitionCount, note);
    }

    /// <summary>Saves a draft: new master table entries first, then the empty values on the asset (re-read, so it is current).</summary>
    public async Task<CharacteristicReport> SaveDraftAsync(
        CharacteristicDraft draft,
        CancellationToken cancellationToken = default)
    {
        var asset = await FindAssetAsync(draft.AssetId, draft.SourceFileName, cancellationToken);
        if (asset is null)
        {
            return new CharacteristicReport([], 0, 0, $"Skipped: asset '{draft.AssetId}' not found.");
        }

        var resolution = await ResolveAndSaveMasterAsync(draft.Extracted, asset.Category, draft.SourceFileName, cancellationToken);
        var watch = Stopwatch.StartNew();

        var merge = CharacteristicResolver.MergeIntoAsset(asset.Characteristics, resolution.Values);
        if (merge.Filled.Count > 0)
        {
            await assetCatalog.UpdateCharacteristicsAsync(asset.Id, merge.Merged, cancellationToken);
        }

        LogPart(draft.SourceFileName, "asset update", watch, $"{merge.Filled.Count} value(s) filled");

        var note = $"Filled {merge.Filled.Count} characteristic(s) on asset '{asset.Id}'" +
                   (merge.SkippedExisting > 0 ? $", kept {merge.SkippedExisting} existing value(s)" : "") +
                   (resolution.NewDefinitionCount > 0 ? $", added {resolution.NewDefinitionCount} new to the master table" : "") +
                   ".";

        return new CharacteristicReport(merge.Filled, merge.SkippedExisting, resolution.NewDefinitionCount, note);
    }

    // An asset created moments ago can take a second or two to become searchable.
    private async Task<Asset?> FindAssetAsync(string assetId, string fileName, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        var retries = 0;
        for (var attempt = 1; asset is null && attempt <= 3; attempt++)
        {
            retries++;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        }

        LogPart(fileName, "asset lookup", watch, retries > 0 ? $"{retries} retry(ies)" : null);
        return asset;
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
