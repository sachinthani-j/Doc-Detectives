using DocDetectives.Domain.Entities;
using System.Text.RegularExpressions;

namespace DocDetectives.Application.Services;

/// <summary>
/// Turns the LLM's proposed sub-assets and spare parts into a validated asset tree.
/// The LLM proposes; this class enforces the rules (mandatory fields, max depth, unique names, valid parents).
/// </summary>
public static class AssetTreeBuilder
{
    // Root = level 1, so sub-assets are levels 2 and 3 ("two or three levels at most").
    public const int MaxLevels = 3;

    private const int MaxRootIdLength = 80;
    private const int MaxSubIdSuffixLength = 40;

    /// <summary>
    /// Returns the root asset first, followed by its sub-assets; null when the manual has no manufacturer or equipment name.
    /// </summary>
    public static IReadOnlyList<Asset>? Build(
        EquipmentAssetStructure structure,
        string? fallbackManufacturer,
        out string? missingInfo)
    {
        var manufacturer = (structure.Manufacturer ?? fallbackManufacturer)?.Trim();
        var rootName = structure.EquipmentName?.Trim();

        missingInfo = (string.IsNullOrEmpty(manufacturer), string.IsNullOrEmpty(rootName)) switch
        {
            (true, true) => "manufacturer and no equipment name",
            (true, false) => "manufacturer",
            (false, true) => "equipment name",
            _ => null
        };

        if (missingInfo is not null)
        {
            return null;
        }

        var rootId = Truncate($"AUTO-{Clean(manufacturer!)}-{Clean(rootName!)}", MaxRootIdLength);

        // Sub-assets keyed by cleaned name: drops blanks, duplicates and anything named like the whole equipment.
        var subAssets = new Dictionary<string, SubAssetItem>();
        foreach (var item in structure.SubAssets ?? [])
        {
            var key = SubKey(item.Name);
            if (key.Length > 0 && key != SubKey(rootName))
            {
                subAssets.TryAdd(key, item with { Name = item.Name!.Trim() });
            }
        }

        // Place every sub-asset: unknown or circular parents fall back to the root, too-deep ones move up a level.
        var placed = new Dictionary<string, (string Id, int Level, string ParentId)>();
        (string Id, int Level, string ParentId) Place(string key, HashSet<string> visiting)
        {
            if (placed.TryGetValue(key, out var done))
            {
                return done;
            }

            visiting.Add(key);
            var parentKey = SubKey(subAssets[key].ParentName);
            var parent = (Id: rootId, Level: 1, ParentId: rootId);

            if (subAssets.ContainsKey(parentKey) && !visiting.Contains(parentKey))
            {
                parent = Place(parentKey, visiting);
            }

            if (parent.Level >= MaxLevels)
            {
                parent = (parent.ParentId, parent.Level - 1, rootId);
            }

            var result = ($"{rootId}-{key}", parent.Level + 1, parent.Id);
            placed[key] = result;
            return result;
        }

        foreach (var key in subAssets.Keys)
        {
            Place(key, []);
        }

        // Spare parts go to the asset they belong to; unknown or empty owners fall back to the root.
        var partsByAssetId = new Dictionary<string, List<string>>();
        foreach (var part in structure.SpareParts ?? [])
        {
            if (string.IsNullOrWhiteSpace(part.PartName))
            {
                continue;
            }

            var ownerId = placed.TryGetValue(SubKey(part.BelongsTo), out var owner) ? owner.Id : rootId;
            var entry = string.IsNullOrWhiteSpace(part.ArticleNumber)
                ? part.PartName.Trim()
                : $"{part.ArticleNumber.Trim()} | {part.PartName.Trim()}";

            var list = partsByAssetId.TryGetValue(ownerId, out var existing) ? existing : partsByAssetId[ownerId] = [];
            if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(entry);
            }
        }

        IReadOnlyList<string> PartsOf(string assetId) =>
            partsByAssetId.TryGetValue(assetId, out var parts) ? parts : [];

        var models = (structure.ModelNumbers ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var assets = new List<Asset>
        {
            new(
                Id: rootId,
                Name: rootName!,
                Manufacturer: manufacturer!,
                ModelNumber: string.Join(", ", models),
                SerialNumber: string.Empty, // manuals describe an equipment type, not a physical unit
                Category: structure.EquipmentCategory?.Trim() ?? string.Empty,
                ParentAssetId: null,
                SpareParts: PartsOf(rootId))
        };

        // Parents before children, so the list reads top-down.
        foreach (var (key, place) in placed.OrderBy(p => p.Value.Level))
        {
            assets.Add(new Asset(
                Id: place.Id,
                Name: $"{subAssets[key].Name} - {rootName}", // naming convention: "Pump - MX9"
                Manufacturer: manufacturer!,
                ModelNumber: string.Empty,
                SerialNumber: string.Empty,
                Category: string.Empty,
                ParentAssetId: place.ParentId,
                SpareParts: PartsOf(place.Id)));
        }

        return assets;
    }

    // Azure Search key-safe and deterministic, so re-ingestion updates the same assets.
    private static string Clean(string value) =>
        Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');

    // Sub-asset identity: cleaned and already truncated, so two distinct keys never produce the same asset ID.
    private static string SubKey(string? name) => Truncate(Clean(name ?? string.Empty), MaxSubIdSuffixLength);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].TrimEnd('-');
}
