using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Entities;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DocDetectives.ConsoleApp;

/// <param name="RejectedAssetIds">IDs of every asset in proposals that were not approved (nothing was created for them).</param>
public sealed record AssetReviewResult(int Approved, int Skipped, IReadOnlySet<string> RejectedAssetIds);

/// <summary>
/// Shows each proposed new asset (with sub-assets, spare parts and characteristics) before anything is created.
/// The person can approve it, edit it first, or skip it. Asset IDs never change while editing, so job plans and
/// manual chunks that already point to an asset stay valid. Plain ASCII output only.
/// </summary>
public static class AssetProposalReview
{
    private const int MaxLevels = 3;

    public static async Task<AssetReviewResult> RunAsync(
        IReadOnlyList<AssetProposal> proposals,
        AssetProposalService service,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        output.WriteLine($"=== New asset review: {proposals.Count} proposal(s). Nothing is created until you approve it. ===");
        var approved = 0;
        var rejected = new HashSet<string>();

        for (var i = 0; i < proposals.Count; i++)
        {
            var proposal = proposals[i];

            while (true)
            {
                output.WriteLine();
                output.WriteLine($"--- New asset {i + 1}/{proposals.Count} (from {proposal.SourceFileName}) ---");
                await PrintAsync(proposal, service, output, cancellationToken);

                if (await service.ExistsAsync(proposal.Assets[0].Id, cancellationToken))
                {
                    output.WriteLine("  Note: an asset with this ID already exists; approving updates it.");
                }

                var choice = Ask(input, output, "(A)pprove and create, (E)dit, (S)kip without creating, (Q)uit review");
                if (choice is null or "q")
                {
                    foreach (var rest in proposals.Skip(i))
                    {
                        rejected.UnionWith(rest.Assets.Select(a => a.Id));
                    }

                    output.WriteLine($"Review stopped. Created: {approved}, not created: {proposals.Count - approved}.");
                    return new AssetReviewResult(approved, proposals.Count - approved, rejected);
                }

                if (choice == "a")
                {
                    var created = await service.ApproveAsync(proposal, cancellationToken);
                    output.WriteLine($"  Created '{created[0].Id}' with {created.Count - 1} sub-asset(s) and " +
                                     $"{created[0].Characteristics?.Count ?? 0} characteristic(s).");
                    approved++;
                    break;
                }

                if (choice == "s")
                {
                    rejected.UnionWith(proposal.Assets.Select(a => a.Id));
                    output.WriteLine("  Skipped: nothing created.");
                    break;
                }

                if (choice == "e")
                {
                    proposal = Edit(proposal, input, output);
                }
            }
        }

        output.WriteLine();
        output.WriteLine($"Asset review complete. Created: {approved}, not created: {proposals.Count - approved}.");
        return new AssetReviewResult(approved, proposals.Count - approved, rejected);
    }

    private static async Task PrintAsync(
        AssetProposal proposal,
        AssetProposalService service,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var ordered = InTreeOrder(proposal.Assets);

        // 1. Every new asset, as the columns that will be written to the assets index.
        output.WriteLine("  New assets (columns written to the index):");
        for (var n = 0; n < ordered.Count; n++)
        {
            var (asset, depth) = ordered[n];
            output.WriteLine($"  {n + 1,3}. {(depth == 0 ? "MAIN ASSET" : $"Sub-asset (level {depth + 1})")}");
            output.WriteLine($"         assetId          : {asset.Id}");
            output.WriteLine($"         name             : {asset.Name}");
            output.WriteLine($"         manufacturer     : {Show(asset.Manufacturer)}");
            output.WriteLine($"         manufacturerName : {Show(asset.ManufacturerName)}");
            output.WriteLine($"         manufactureYear  : {asset.ManufactureYear?.ToString() ?? "- (not found in manual)"}");
            output.WriteLine($"         modelNumber      : {Show(asset.ModelNumber)}");
            output.WriteLine($"         serialNumber     : {Show(asset.SerialNumber)}");
            output.WriteLine($"         category         : {Show(asset.Category)}");
            output.WriteLine($"         parentAssetId    : {asset.ParentAssetId ?? "- (top level)"}");
            output.WriteLine($"         linkedManualIds  : {proposal.ManualId}");
            output.WriteLine($"         spareParts       : {(asset.SpareParts?.Count ?? 0)}");
            output.WriteLine($"         characteristics  : {(depth == 0 ? proposal.Characteristics.Count : 0)}");
        }

        // 2. The hierarchy with each asset's spare parts (bill of materials).
        output.WriteLine("  Asset tree and spare parts:");
        for (var n = 0; n < ordered.Count; n++)
        {
            var (asset, depth) = ordered[n];
            var indent = new string(' ', 4 + depth * 4);
            output.WriteLine($"  {n + 1,3}.{indent}{asset.Name}");
            var parts = asset.SpareParts ?? [];
            for (var p = 0; p < parts.Count; p++)
            {
                output.WriteLine($"      {indent}    - Spare part {p + 1}: {parts[p]}");
            }
        }

        output.WriteLine("  Characteristics (put on the main asset):");
        if (proposal.Characteristics.Count == 0)
        {
            output.WriteLine("        none");
            return;
        }

        // Show how each value maps onto the master table: an existing entry is reused, otherwise a new one is added.
        var preview = await service.PreviewCharacteristicsAsync(proposal, cancellationToken);
        for (var c = 0; c < proposal.Characteristics.Count; c++)
        {
            var item = proposal.Characteristics[c];
            var mapped = preview.Mapping.FirstOrDefault(m => m.Index == c);
            var master = mapped.CharacteristicId is null
                ? "skipped: no value, or a duplicate"
                : mapped.IsNew ? $"new in master: {mapped.CharacteristicId}" : $"master: {mapped.CharacteristicId}";
            output.WriteLine($"  {c + 1,3}. {item.Name}: {FormatValue(item)}   ({master}; manual: {item.SourceParameter})");
        }
    }

    private static AssetProposal Edit(AssetProposal proposal, TextReader input, TextWriter output)
    {
        while (true)
        {
            var choice = Ask(input, output,
                "Edit: (1) asset details, (2) add sub-asset, (3) delete sub-asset, (4) move sub-asset, " +
                "(5) add spare part, (6) remove spare part, (7) change characteristic, (8) remove characteristic, (D)one");

            var assets = proposal.Assets.ToList();
            switch (choice)
            {
                case null or "d":
                    return proposal;

                case "1" when PickAsset(input, output, proposal, "Asset number") is { } asset:
                    var isRoot = asset.Id == assets[0].Id;
                    output.WriteLine("  Press Enter to keep a value, type '-' to clear it.");
                    var edited = asset with
                    {
                        Name = AskField(input, output, "Name", asset.Name) ?? asset.Name,
                        Manufacturer = AskField(input, output, "Manufacturer", asset.Manufacturer) ?? asset.Manufacturer,
                        ManufacturerName = AskField(input, output, "Manufacturer name", asset.ManufacturerName),
                        ManufactureYear = AskYear(input, output, asset.ManufactureYear),
                        ModelNumber = AskField(input, output, "Model", asset.ModelNumber) ?? string.Empty,
                        Category = isRoot ? AskField(input, output, "Category", asset.Category) ?? string.Empty : asset.Category,
                        SerialNumber = AskField(input, output, "Serial number", asset.SerialNumber) ?? string.Empty
                    };
                    assets[assets.FindIndex(a => a.Id == asset.Id)] = edited;
                    break;

                case "2" when PickAsset(input, output, proposal, "Parent asset number") is { } parent:
                    if (Depth(assets, parent.Id) + 1 >= MaxLevels)
                    {
                        output.WriteLine($"  Not added: the hierarchy is limited to {MaxLevels} levels.");
                        break;
                    }

                    var name = AskField(input, output, "Sub-asset name", null);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        break;
                    }

                    var rootAsset = assets[0];
                    var id = UniqueId($"{rootAsset.Id}-{Clean(name)}", assets);
                    assets.Add(new Asset(id, $"{name} - {rootAsset.Name}", rootAsset.Manufacturer, string.Empty, string.Empty,
                        string.Empty, parent.Id, [], null));
                    output.WriteLine($"  Added '{id}'.");
                    break;

                case "3" when PickAsset(input, output, proposal, "Sub-asset number to delete") is { } removed:
                    if (removed.Id == assets[0].Id)
                    {
                        output.WriteLine("  The main asset cannot be deleted here; use (S)kip to not create anything.");
                        break;
                    }

                    // Its children move up to its parent and its spare parts go to its parent, so nothing is lost by accident.
                    var parentId = removed.ParentAssetId ?? assets[0].Id;
                    assets = assets
                        .Where(a => a.Id != removed.Id)
                        .Select(a => a.ParentAssetId == removed.Id ? a with { ParentAssetId = parentId } : a)
                        .Select(a => a.Id == parentId
                            ? a with { SpareParts = (a.SpareParts ?? []).Concat(removed.SpareParts ?? []).Distinct().ToList() }
                            : a)
                        .ToList();
                    output.WriteLine($"  Deleted '{removed.Id}'; its sub-assets and spare parts moved to its parent.");
                    break;

                case "4" when PickAsset(input, output, proposal, "Sub-asset number to move") is { } moving:
                    if (moving.Id == assets[0].Id || PickAsset(input, output, proposal, "New parent asset number") is not { } newParent)
                    {
                        break;
                    }

                    if (newParent.Id == moving.Id || IsDescendant(assets, newParent.Id, moving.Id))
                    {
                        output.WriteLine("  Not moved: an asset cannot be placed under itself or its own sub-assets.");
                    }
                    else if (Depth(assets, newParent.Id) + 1 + SubtreeHeight(assets, moving.Id) > MaxLevels)
                    {
                        output.WriteLine($"  Not moved: the hierarchy is limited to {MaxLevels} levels.");
                    }
                    else
                    {
                        assets[assets.FindIndex(a => a.Id == moving.Id)] = moving with { ParentAssetId = newParent.Id };
                    }
                    break;

                case "5" when PickAsset(input, output, proposal, "Asset number for the spare part") is { } owner:
                    var part = AskField(input, output, "Spare part (e.g. 51234567 | Oil filter)", null);
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        assets[assets.FindIndex(a => a.Id == owner.Id)] =
                            owner with { SpareParts = (owner.SpareParts ?? []).Append(part).ToList() };
                    }
                    break;

                case "6" when PickAsset(input, output, proposal, "Asset number with the spare part") is { } holder:
                    var held = holder.SpareParts ?? [];
                    var which = AskNumber(input, output, "Spare part number", held.Count);
                    if (which is { } partIndex)
                    {
                        assets[assets.FindIndex(a => a.Id == holder.Id)] =
                            holder with { SpareParts = held.Where((_, n) => n != partIndex).ToList() };
                    }
                    break;

                case "7" when AskNumber(input, output, "Characteristic number", proposal.Characteristics.Count) is { } changeIndex:
                    var characteristics = proposal.Characteristics.ToList();
                    characteristics[changeIndex] = EditValue(characteristics[changeIndex], input, output);
                    proposal = proposal with { Characteristics = characteristics };
                    break;

                case "8" when AskNumber(input, output, "Characteristic number", proposal.Characteristics.Count) is { } removeIndex:
                    proposal = proposal with
                    {
                        Characteristics = proposal.Characteristics.Where((_, n) => n != removeIndex).ToList()
                    };
                    break;
            }

            proposal = proposal with { Assets = InTreeOrder(assets).Select(x => x.Asset).ToList() };
        }
    }

    // Enter keeps the year, "-" clears it, anything that is not a plausible year keeps the old value.
    private static int? AskYear(TextReader input, TextWriter output, int? current)
    {
        var answer = AskField(input, output, "Manufacture year", current?.ToString());
        if (answer is null)
        {
            return null;
        }

        if (int.TryParse(answer, out var year) && year >= 1900 && year <= DateTime.UtcNow.Year + 1)
        {
            return year;
        }

        output.WriteLine("  Not a valid year: value kept.");
        return current;
    }

    private static ExtractedCharacteristic EditValue(ExtractedCharacteristic item, TextReader input, TextWriter output)
    {
        output.WriteLine($"  {item.Name} is {item.ValueType}. Press Enter to keep the value.");
        var answer = AskField(input, output, "New value", FormatValue(item));
        if (answer is null || answer == FormatValue(item))
        {
            return item;
        }

        return item.ValueType switch
        {
            CharacteristicValueType.Numeric when double.TryParse(answer.Split(' ')[0].Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => item with { NumericValue = number },
            CharacteristicValueType.YesNo => item with { BooleanValue = answer.Trim().ToLowerInvariant() is "y" or "yes" or "true" or "ja" },
            CharacteristicValueType.Date => item with { DateValue = answer },
            CharacteristicValueType.Numeric => Warn(item, output),
            _ => item with { TextValue = answer }
        };
    }

    private static ExtractedCharacteristic Warn(ExtractedCharacteristic item, TextWriter output)
    {
        output.WriteLine("  Not a number: value kept.");
        return item;
    }

    private static string FormatValue(ExtractedCharacteristic c) => c.ValueType switch
    {
        CharacteristicValueType.Numeric => $"{c.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "-"} {c.UnitSymbol ?? c.Unit}".TrimEnd(),
        CharacteristicValueType.YesNo => c.BooleanValue is null ? "-" : c.BooleanValue.Value ? "Yes" : "No",
        CharacteristicValueType.Date => c.DateValue ?? "-",
        _ => c.TextValue ?? "-"
    };

    // Root first, then each asset followed by its children (depth-first), with each asset's depth.
    private static List<(Asset Asset, int Depth)> InTreeOrder(IReadOnlyList<Asset> assets)
    {
        var result = new List<(Asset, int)>();
        void Visit(Asset asset, int depth)
        {
            result.Add((asset, depth));
            foreach (var child in assets.Where(a => a.ParentAssetId == asset.Id))
            {
                Visit(child, depth + 1);
            }
        }

        Visit(assets[0], 0);
        return result;
    }

    private static int Depth(IReadOnlyList<Asset> assets, string assetId)
    {
        var depth = 0;
        var current = assets.FirstOrDefault(a => a.Id == assetId);
        while (current?.ParentAssetId is { } parentId && depth < 10)
        {
            depth++;
            current = assets.FirstOrDefault(a => a.Id == parentId);
        }

        return depth;
    }

    private static int SubtreeHeight(IReadOnlyList<Asset> assets, string assetId) =>
        1 + assets.Where(a => a.ParentAssetId == assetId).Select(c => SubtreeHeight(assets, c.Id)).DefaultIfEmpty(0).Max();

    private static bool IsDescendant(IReadOnlyList<Asset> assets, string candidateId, string ancestorId)
    {
        var current = assets.FirstOrDefault(a => a.Id == candidateId);
        for (var guard = 0; current?.ParentAssetId is { } parentId && guard < 10; guard++)
        {
            if (parentId == ancestorId)
            {
                return true;
            }

            current = assets.FirstOrDefault(a => a.Id == parentId);
        }

        return false;
    }

    private static Asset? PickAsset(TextReader input, TextWriter output, AssetProposal proposal, string prompt)
    {
        var ordered = InTreeOrder(proposal.Assets);
        return AskNumber(input, output, prompt, ordered.Count) is { } index ? ordered[index].Asset : null;
    }

    private static int? AskNumber(TextReader input, TextWriter output, string prompt, int count)
    {
        if (count == 0)
        {
            output.WriteLine("  Nothing to choose from.");
            return null;
        }

        var answer = Ask(input, output, $"{prompt} (1-{count})");
        if (int.TryParse(answer, out var number) && number >= 1 && number <= count)
        {
            return number - 1;
        }

        output.WriteLine("  Not a valid number.");
        return null;
    }

    private static string UniqueId(string id, IReadOnlyList<Asset> assets)
    {
        var candidate = id;
        for (var n = 2; assets.Any(a => a.Id == candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    private static string Clean(string value) =>
        Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    // Enter keeps the current value, "-" clears it.
    private static string? AskField(TextReader input, TextWriter output, string label, string? current)
    {
        output.Write($"  {label} [{current ?? ""}]: ");
        var answer = input.ReadLine();
        if (answer is null || answer.Length == 0)
        {
            return current;
        }

        return answer.Trim() == "-" ? null : answer.Trim();
    }

    private static string? Ask(TextReader input, TextWriter output, string prompt)
    {
        output.Write($"{prompt}: ");
        return input.ReadLine()?.Trim().ToLowerInvariant();
    }
}
