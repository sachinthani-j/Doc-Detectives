using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Services;

/// <summary>
/// The manual's proposed asset tree mapped onto the EAM: nodes that already exist are reused (never rewritten),
/// only missing nodes are new. <see cref="Assets"/> is the merged tree, root first, parents before children.
/// </summary>
/// <param name="Assets">Existing nodes as they are in the EAM, new nodes with ParentAssetId pointing at the mapped parent.</param>
/// <param name="ExistingAssetIds">Ids in <see cref="Assets"/> that already exist in the EAM.</param>
public sealed record ReconciledAssetTree(
    IReadOnlyList<Asset> Assets,
    IReadOnlySet<string> ExistingAssetIds)
{
    public IEnumerable<Asset> NewAssets => Assets.Where(a => !ExistingAssetIds.Contains(a.Id));
    public int NewCount => Assets.Count - ExistingAssetIds.Count;
}

/// <summary>
/// Maps a manual's proposed structure onto an existing EAM asset: "link existing A and B, create new C".
/// The root is the existing asset the manual matched; each proposed sub-asset is matched by name against that
/// asset's current sub-assets (children and grandchildren). Matches are linked as they are, misses become new
/// assets under the matched (or new) parent. Nothing in the EAM is changed by the mapping itself.
/// </summary>
public static class AssetTreeReconciler
{
    public static async Task<ReconciledAssetTree> ReconcileAsync(
        IReadOnlyList<Asset> proposed,
        Asset existingRoot,
        IAssetCatalogGateway assetCatalog,
        CancellationToken cancellationToken = default)
    {
        var existingSubs = await ComponentAssetMatcher.GetSubAssetsAsync(assetCatalog, existingRoot.Id, cancellationToken);
        var used = new HashSet<string>(StringComparer.Ordinal) { existingRoot.Id };

        // Proposed id -> id in the merged tree (the existing asset's id when matched).
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal) { [proposed[0].Id] = existingRoot.Id };
        var merged = new List<Asset> { existingRoot };
        var existingIds = new HashSet<string>(StringComparer.Ordinal) { existingRoot.Id };

        // AssetTreeBuilder lists parents before children, so a parent is always mapped before its children.
        foreach (var sub in proposed.Skip(1))
        {
            var match = FindByName(Component(sub.Name), existingSubs.Where(a => !used.Contains(a.Id)));
            if (match is not null)
            {
                used.Add(match.Id);
                mapped[sub.Id] = match.Id;
                merged.Add(match);
                existingIds.Add(match.Id);
                continue;
            }

            var parentId = sub.ParentAssetId is { } p && mapped.TryGetValue(p, out var mappedParent)
                ? mappedParent
                : existingRoot.Id;

            mapped[sub.Id] = sub.Id;
            merged.Add(sub with { ParentAssetId = parentId });
        }

        return new ReconciledAssetTree(merged, existingIds);
    }

    /// <summary>All nodes are new (no existing root matched).</summary>
    public static ReconciledAssetTree AllNew(IReadOnlyList<Asset> proposed) =>
        new(proposed, new HashSet<string>(StringComparer.Ordinal));

    // Sub-asset names follow "Component - Equipment"; only the component part identifies it.
    private static string Component(string name) => Normalize(name.Split(" - ")[0]);

    // Exact name first; otherwise one name containing the other (e.g. "Fan Motor" vs "Motor"), longest first.
    private static Asset? FindByName(string wanted, IEnumerable<Asset> candidates)
    {
        if (wanted.Length == 0) return null;

        var named = candidates
            .Select(a => (Asset: a, Name: Component(a.Name)))
            .Where(c => c.Name.Length > 0)
            .ToList();

        return named.FirstOrDefault(c => c.Name == wanted).Asset
               ?? named
                   .Where(c => Math.Min(c.Name.Length, wanted.Length) >= 4 &&
                               (c.Name.Contains(wanted) || wanted.Contains(c.Name)))
                   .OrderByDescending(c => c.Name.Length)
                   .FirstOrDefault().Asset;
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
