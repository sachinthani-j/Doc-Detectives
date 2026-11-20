using System.Globalization;
using DocDetectives.Application.Services;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Application.Ingestion;

// Shapes a person's plain-words edit is applied to (by ReviewEditSkill). They hold only what may change:
// IDs, sources and asset links stay on the original and are copied back by ReviewEditMerger.

/// <param name="ChangeSummary">One sentence on what was changed.</param>
/// <param name="NotDone">Requested changes that could not be made, each with a short reason.</param>
public sealed record JobPlanEdit(
    string Title,
    string? Interval,
    int? IntervalValue,
    string? IntervalUnit,
    List<string> Materials,
    List<string> SafetyNotes,
    List<JobPlanLineEdit> Lines,
    string? ChangeSummary,
    List<string> NotDone);

/// <param name="OriginalLineNumber">The line's number in the draft shown to the user; null for a new line.</param>
public sealed record JobPlanLineEdit(
    int? OriginalLineNumber,
    string Title,
    string LineType,
    string? Instruction,
    string? Component,
    string? MeasurementPoint,
    string? NominalValue,
    List<string> AllowedResults);

public sealed record AssetProposalEdit(
    List<ProposedAssetEdit> Assets,
    string? ChangeSummary,
    List<string> NotDone);

/// <param name="Id">Null for a new sub-asset the user asked to add.</param>
/// <param name="ParentId">Id of the parent asset; null for the root.</param>
/// <param name="Existing">An asset that already exists in the EAM; read-only.</param>
public sealed record ProposedAssetEdit(
    string? Id,
    string Name,
    string? ModelNumber,
    string? Category,
    string? ParentId,
    bool Existing,
    List<string> SpareParts);

public sealed record CharacteristicsEdit(
    List<CharacteristicValueEdit> Values,
    string? ChangeSummary,
    List<string> NotDone);

/// <param name="Value">Number, yes/no, text or date, as text.</param>
public sealed record CharacteristicValueEdit(string Name, string? Value, string? Unit);

/// <summary>An edited draft plus what was refused (by the AI or by validation) and what changed.</summary>
public sealed record EditOutcome<T>(T Result, IReadOnlyList<string> Refused, string? Summary);

/// <summary>
/// Applies an edited shape back onto the original draft, keeping IDs, sources and links, and validating the values.
/// Anything that cannot be applied is listed in <see cref="EditOutcome{T}.Refused"/> instead of being saved.
/// </summary>
public static class ReviewEditMerger
{
    public static readonly IReadOnlyList<string> IntervalUnits = ["Hours", "Days", "Weeks", "Months", "Years"];

    // ---------- PM drafts ----------

    public static JobPlanEdit ToEdit(JobPlan plan) => new(
        plan.Title,
        plan.Interval,
        plan.IntervalValue,
        plan.IntervalUnit,
        plan.Materials.ToList(),
        plan.SafetyNotes.ToList(),
        plan.Lines.Select(l => new JobPlanLineEdit(
            l.LineNumber, l.Title, l.LineType, l.Instruction, l.Component, l.MeasurementPoint, l.NominalValue,
            l.AllowedResults.ToList())).ToList(),
        null,
        []);

    public static EditOutcome<JobPlan> Apply(JobPlan original, JobPlanEdit edit)
    {
        var refused = new List<string>(edit.NotDone ?? []);

        // Frequency: a known unit and a positive number, or no frequency at all.
        var (value, unit, interval) = (original.IntervalValue, original.IntervalUnit, original.Interval);
        var newUnit = NormalizeUnit(edit.IntervalUnit);
        if (edit.IntervalUnit is { Length: > 0 } && newUnit is null)
        {
            refused.Add($"'{edit.IntervalUnit}' is not a frequency unit (use {string.Join(", ", IntervalUnits)}).");
        }
        else if (edit.IntervalValue is <= 0)
        {
            refused.Add("The frequency must be a positive number.");
        }
        else if (edit.IntervalValue != original.IntervalValue || newUnit != original.IntervalUnit)
        {
            (value, unit) = (edit.IntervalValue, newUnit);
            interval = value is null || unit is null
                ? null
                : edit.Interval is { Length: > 0 } text && text != original.Interval
                    ? text.Trim()
                    : $"{value} {unit.ToLowerInvariant()}";
        }
        else if (edit.Interval is { Length: > 0 } text && text != original.Interval)
        {
            interval = text.Trim();
        }

        // Lines: kept lines keep their equipment links; new lines take them from a line on the same component.
        var originals = original.Lines.ToDictionary(l => l.LineNumber);
        var lines = new List<JobPlanLine>();
        foreach (var e in edit.Lines ?? [])
        {
            if (string.IsNullOrWhiteSpace(e.Title)) continue;

            var source = e.OriginalLineNumber is { } n && originals.TryGetValue(n, out var o) ? o : null;
            var sameComponent = source ?? original.Lines.FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(e.Component)
                && string.Equals(l.Component, e.Component, StringComparison.OrdinalIgnoreCase));

            var type = NormalizeLineType(e.LineType);
            if (type is null)
            {
                refused.Add($"Line '{e.Title}': type '{e.LineType}' is not Task or Inspection; kept {source?.LineType ?? JobPlanLineType.Task}.");
                type = source?.LineType ?? JobPlanLineType.Task;
            }

            IReadOnlyList<string> allowed = type == JobPlanLineType.Inspection
                ? (e.AllowedResults is { Count: > 0 } results ? results : source?.AllowedResults is { Count: > 0 } old ? old : ["OK", "NOK"])
                : [];

            lines.Add(new JobPlanLine(
                LineNumber: lines.Count + 1,
                Title: e.Title.Trim(),
                LineType: type,
                Instruction: Blank(e.Instruction),
                EquipmentCode: sameComponent?.EquipmentCode,
                Component: Blank(e.Component),
                AssetId: sameComponent?.AssetId,
                MeasurementPoint: Blank(e.MeasurementPoint),
                NominalValue: Blank(e.NominalValue),
                MeasurementCode: source?.MeasurementCode,
                AllowedResults: allowed));
        }

        if (lines.Count == 0)
        {
            refused.Add("A PM draft needs at least one line; the lines were kept.");
            lines = original.Lines.ToList();
        }

        var result = original with
        {
            Title = string.IsNullOrWhiteSpace(edit.Title) ? original.Title : edit.Title.Trim(),
            Interval = interval,
            IntervalValue = value,
            IntervalUnit = unit,
            Materials = Clean(edit.Materials),
            SafetyNotes = Clean(edit.SafetyNotes),
            Lines = lines
        };

        return new EditOutcome<JobPlan>(result, refused, Blank(edit.ChangeSummary));
    }

    // ---------- New-asset proposals ----------

    public static AssetProposalEdit ToEdit(AssetProposal proposal) => new(
        proposal.Assets.Select(a => new ProposedAssetEdit(
            a.Id, a.Name, Blank(a.ModelNumber), Blank(a.Category), a.ParentAssetId, proposal.IsExisting(a.Id),
            (a.SpareParts ?? []).ToList())).ToList(),
        null,
        []);

    public static EditOutcome<AssetProposal> Apply(AssetProposal original, AssetProposalEdit edit)
    {
        var refused = new List<string>(edit.NotDone ?? []);
        var root = original.Assets[0];
        var edited = new Dictionary<string, ProposedAssetEdit>(StringComparer.Ordinal);
        foreach (var e in edit.Assets ?? [])
        {
            if (e.Id is { } id && original.Assets.Any(a => a.Id == id)) edited.TryAdd(id, e);
        }

        var result = new List<Asset>();
        var removed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asset in original.Assets)
        {
            var hasEdit = edited.TryGetValue(asset.Id, out var e);

            if (original.IsExisting(asset.Id))
            {
                // Existing EAM assets are only shown for context: they are never changed or removed here.
                if (!hasEdit || Differs(asset, e!))
                {
                    refused.Add($"'{asset.Name}' already exists in the EAM and is not changed here.");
                }
                result.Add(asset);
                continue;
            }

            if (!hasEdit)
            {
                if (asset.Id == root.Id)
                {
                    refused.Add("The main asset can't be removed; reply **no** to reject the whole proposal.");
                    result.Add(asset);
                }
                else
                {
                    removed.Add(asset.Id);
                }
                continue;
            }

            if (e!.ParentId != asset.ParentAssetId)
            {
                refused.Add($"Moving '{asset.Name}' to another parent isn't supported; it stays where it is.");
            }

            result.Add(asset with
            {
                Name = string.IsNullOrWhiteSpace(e.Name) ? asset.Name : e.Name.Trim(),
                ModelNumber = e.ModelNumber?.Trim() ?? asset.ModelNumber,
                Category = e.Category?.Trim() ?? asset.Category,
                SpareParts = Clean(e.SpareParts)
            });
        }

        // A removed sub-asset takes its new sub-assets with it.
        bool changed;
        do
        {
            changed = false;
            foreach (var orphan in result.Where(a => a.ParentAssetId is { } p && removed.Contains(p) && !original.IsExisting(a.Id)).ToList())
            {
                result.Remove(orphan);
                removed.Add(orphan.Id);
                changed = true;
            }
        } while (changed);

        // New sub-assets the user asked for, under an asset that is still in the proposal.
        foreach (var e in (edit.Assets ?? []).Where(a => a.Id is null || original.Assets.All(o => o.Id != a.Id)))
        {
            if (string.IsNullOrWhiteSpace(e.Name)) continue;

            var parent = result.FirstOrDefault(a => a.Id == (e.ParentId ?? root.Id));
            if (parent is null)
            {
                refused.Add($"Couldn't add '{e.Name}': its parent is not in the proposal.");
                continue;
            }

            if (Level(result, parent) >= AssetTreeBuilder.MaxLevels)
            {
                refused.Add($"Couldn't add '{e.Name}': at most {AssetTreeBuilder.MaxLevels} levels are allowed.");
                continue;
            }

            var id = AssetTreeBuilder.SubAssetId(root.Id, e.Name.Trim());
            if (result.Any(a => a.Id == id))
            {
                refused.Add($"Couldn't add '{e.Name}': an asset with that name is already in the proposal.");
                continue;
            }

            result.Add(new Asset(
                Id: id,
                Name: e.Name.Trim(),
                Manufacturer: root.Manufacturer,
                ModelNumber: e.ModelNumber?.Trim() ?? string.Empty,
                SerialNumber: string.Empty,
                Category: e.Category?.Trim() ?? root.Category,
                ParentAssetId: parent.Id,
                SpareParts: Clean(e.SpareParts),
                ManufacturerName: root.ManufacturerName));
        }

        return new EditOutcome<AssetProposal>(original with { Assets = result }, refused, Blank(edit.ChangeSummary));
    }

    // ---------- Characteristic values ----------

    public static CharacteristicsEdit ToEdit(IReadOnlyList<ExtractedCharacteristic> values) => new(
        values.Select(c => new CharacteristicValueEdit(c.Name, ValueText(c), c.UnitSymbol ?? c.Unit)).ToList(),
        null,
        []);

    public static EditOutcome<IReadOnlyList<ExtractedCharacteristic>> Apply(
        IReadOnlyList<ExtractedCharacteristic> original,
        CharacteristicsEdit edit)
    {
        var refused = new List<string>(edit.NotDone ?? []);
        var byName = (edit.Values ?? [])
            .GroupBy(v => v.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in byName.Keys.Where(n => original.All(o => !o.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
        {
            refused.Add($"'{name}' was not extracted from the manual; only extracted characteristics can be changed or removed.");
        }

        var result = new List<ExtractedCharacteristic>();
        foreach (var c in original)
        {
            if (!byName.TryGetValue(c.Name, out var e)) continue; // removed by the user

            var unitChanged = !string.Equals(Blank(e.Unit), c.UnitSymbol ?? c.Unit, StringComparison.Ordinal);
            if (string.Equals(Blank(e.Value), ValueText(c), StringComparison.Ordinal) && !unitChanged)
            {
                result.Add(c);
                continue;
            }

            var updated = WithValue(c, e.Value, out var problem);
            if (problem is not null)
            {
                refused.Add(problem);
                result.Add(c);
                continue;
            }

            if (unitChanged)
            {
                updated = updated with { Unit = Blank(e.Unit), UnitSymbol = Blank(e.Unit) };
            }

            result.Add(updated with { SourceText = $"Changed by user (was: {ValueText(c) ?? "empty"})" });
        }

        return new EditOutcome<IReadOnlyList<ExtractedCharacteristic>>(result, refused, Blank(edit.ChangeSummary));
    }

    public static string? ValueText(ExtractedCharacteristic c) =>
        c.NumericValue is { } n ? n.ToString("0.###", CultureInfo.InvariantCulture)
        : c.BooleanValue is { } b ? (b ? "Yes" : "No")
        : Blank(c.TextValue) ?? Blank(c.DateValue);

    private static ExtractedCharacteristic WithValue(ExtractedCharacteristic c, string? text, out string? problem)
    {
        problem = null;
        text = Blank(text);
        var cleared = c with { NumericValue = null, BooleanValue = null, TextValue = null, DateValue = null };
        if (text is null) return cleared;

        switch (c.ValueType)
        {
            case CharacteristicValueType.Numeric:
                var number = text.Replace(',', '.').Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return cleared with { NumericValue = value };
                problem = $"'{c.Name}' needs a number, not '{text}'.";
                return c;

            case CharacteristicValueType.YesNo:
                var yes = text.ToLowerInvariant() is "yes" or "y" or "true" or "ja" or "1";
                var no = text.ToLowerInvariant() is "no" or "n" or "false" or "nee" or "0";
                if (yes || no) return cleared with { BooleanValue = yes };
                problem = $"'{c.Name}' needs yes or no, not '{text}'.";
                return c;

            case CharacteristicValueType.Date:
                if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, out var date))
                    return cleared with { DateValue = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                problem = $"'{c.Name}' needs a date (yyyy-MM-dd), not '{text}'.";
                return c;

            default:
                return cleared with { TextValue = text };
        }
    }

    // ---------- helpers ----------

    private static bool Differs(Asset asset, ProposedAssetEdit e) =>
        !string.Equals(e.Name?.Trim(), asset.Name, StringComparison.Ordinal)
        || !string.Equals(Blank(e.ModelNumber), Blank(asset.ModelNumber), StringComparison.Ordinal)
        || !string.Equals(Blank(e.Category), Blank(asset.Category), StringComparison.Ordinal)
        || !(e.SpareParts ?? []).SequenceEqual(asset.SpareParts ?? []);

    private static int Level(IReadOnlyList<Asset> assets, Asset asset)
    {
        var level = 1;
        for (var current = asset; current.ParentAssetId is { } parentId && level < 10; level++)
        {
            var parent = assets.FirstOrDefault(a => a.Id == parentId);
            if (parent is null) break;
            current = parent;
        }
        return level;
    }

    private static string? NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        var u = unit.Trim();
        return IntervalUnits.FirstOrDefault(x =>
            x.Equals(u, StringComparison.OrdinalIgnoreCase) || x.TrimEnd('s').Equals(u, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeLineType(string? type) =>
        string.Equals(type?.Trim(), JobPlanLineType.Task, StringComparison.OrdinalIgnoreCase) ? JobPlanLineType.Task
        : string.Equals(type?.Trim(), JobPlanLineType.Inspection, StringComparison.OrdinalIgnoreCase) ? JobPlanLineType.Inspection
        : null;

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(i => i?.Trim()).OfType<string>().Where(i => i.Length > 0).Distinct().ToList();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
