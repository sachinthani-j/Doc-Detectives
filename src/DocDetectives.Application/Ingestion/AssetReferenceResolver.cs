using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

/// <summary>
/// Finds the existing asset a document refers to, most reliable first: the asset number on the document,
/// then the serial number, then AI matching on manufacturer / model / name. Returns no asset rather than a guess.
/// </summary>
public sealed class AssetReferenceResolver(
    IAssetCatalogGateway assetCatalog,
    AssetMatchingSkill matchingSkill)
{
    public async Task<(Asset? Asset, string Resolution)> ResolveAsync(
        ExtractedManual document,
        AssetReference reference,
        double autoLinkThreshold,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(reference.AssetId) &&
            await assetCatalog.GetAssetByIdAsync(reference.AssetId.Trim(), cancellationToken) is { } byId)
        {
            return (byId, $"Matched by asset ID '{reference.AssetId.Trim()}'.");
        }

        if (!string.IsNullOrWhiteSpace(reference.SerialNumber) &&
            await assetCatalog.FindAssetBySerialNumberAsync(reference.SerialNumber.Trim(), cancellationToken) is { } bySerial)
        {
            return (bySerial, $"Matched by serial number '{reference.SerialNumber.Trim()}'.");
        }

        // AI matching on manufacturer / model / name. Links are not stored: this document is not a manual.
        var searchable = document with
        {
            Manufacturer = reference.Manufacturer ?? document.Manufacturer,
            ModelNumbers = string.IsNullOrWhiteSpace(reference.ModelNumber) ? [] : [reference.ModelNumber.Trim()],
            EquipmentCategory = reference.AssetName ?? document.EquipmentCategory
        };

        var matches = await matchingSkill.MatchManualToAssetsAsync(
            searchable,
            autoLinkThreshold,
            persistLinks: false,
            cancellationToken);

        var best = matches
            .Where(m => m.Status == MappingStatus.AutoLinked)
            .OrderByDescending(m => m.ConfidenceScore)
            .FirstOrDefault();

        if (best is not null && await assetCatalog.GetAssetByIdAsync(best.AssetId, cancellationToken) is { } matched)
        {
            return (matched, $"Matched by AI ({best.ConfidenceScore:P0}): {best.Reasoning}");
        }

        var reviewCount = matches.Count(m => m.Status == MappingStatus.NeedsReview);
        var stated = reference.AssetId ?? reference.SerialNumber ?? reference.AssetName ?? "nothing";
        return (null, reviewCount > 0
            ? $"Not matched: {reviewCount} possible asset(s), none certain enough (document states: {stated})."
            : $"Not matched: no candidate asset scored high enough (document states: {stated}).");
    }
}
