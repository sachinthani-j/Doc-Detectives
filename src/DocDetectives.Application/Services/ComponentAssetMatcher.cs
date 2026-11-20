using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Services;

/// <summary>
/// Links a line (inspection point, job plan step) to the sub-asset it is about, by the component named on the line.
/// Used for job lines and job plan lines alike.
/// </summary>
public static class ComponentAssetMatcher
{
    /// <summary>Children and grandchildren of an asset: the hierarchy is at most three levels deep.</summary>
    public static async Task<IReadOnlyList<Asset>> GetSubAssetsAsync(
        IAssetCatalogGateway assetCatalog,
        string assetId,
        CancellationToken cancellationToken)
    {
        var children = await assetCatalog.GetChildAssetsAsync(assetId, cancellationToken);
        var all = new List<Asset>(children);
        foreach (var child in children)
        {
            all.AddRange(await assetCatalog.GetChildAssetsAsync(child.Id, cancellationToken));
        }

        return all;
    }

    /// <summary>
    /// Matches the component named on a line to a sub-asset name. Sub-asset names follow "Component - Equipment",
    /// so only the part before " - " is compared. Exact match wins; otherwise a name that contains the other
    /// (e.g. "Fan Motor" vs "Motor"), preferring the longest; otherwise the sub-asset holding it as a spare part.
    /// </summary>
    public static Asset? Find(string? component, IReadOnlyList<Asset> subAssets)
    {
        var wanted = Normalize(component);
        if (wanted.Length == 0 || subAssets.Count == 0)
        {
            return null;
        }

        var candidates = subAssets
            .Select(a => (Asset: a, Name: Normalize(a.Name.Split(" - ")[0])))
            .Where(c => c.Name.Length > 0)
            .ToList();

        return candidates.FirstOrDefault(c => c.Name == wanted).Asset
               ?? candidates
                   .Where(c => Math.Min(c.Name.Length, wanted.Length) >= 4 &&
                               (c.Name.Contains(wanted) || wanted.Contains(c.Name)))
                   .OrderByDescending(c => c.Name.Length)
                   .FirstOrDefault().Asset
               // Not a sub-asset itself? Then the sub-asset that holds it as a spare part ("Spray nozzles").
               ?? subAssets.FirstOrDefault(a => (a.SpareParts ?? []).Any(p =>
                   Normalize(p.Split(" | ")[^1]) is var part && part.Length >= 4 &&
                   (part == wanted || part.Contains(wanted) || wanted.Contains(part))));
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
