using DocDetectives.Domain.Entities;
using System.Text.RegularExpressions;

namespace DocDetectives.Application.Services;

/// <param name="Mapping">For each input row that was used: its index in the input, the master entry, and whether that entry is new.</param>
public sealed record CharacteristicResolution(
    IReadOnlyList<AssetCharacteristic> Values,
    IReadOnlyList<CharacteristicDefinition> DefinitionsToSave,
    int NewDefinitionCount,
    IReadOnlyList<(int Index, string CharacteristicId, bool IsNew)> Mapping);

public sealed record CharacteristicMerge(
    IReadOnlyList<AssetCharacteristic> Merged,
    IReadOnlyList<AssetCharacteristic> Filled,
    int SkippedExisting);

/// <summary>
/// Maps extracted characteristic values onto the master table and onto an asset.
/// Pure logic (no I/O), so the rules are easy to test:
/// an existing master entry is reused (by name or alias), an unknown characteristic becomes a new master entry,
/// and a value the asset already has is never overwritten.
/// </summary>
public static class CharacteristicResolver
{
    private const int MaxIdLength = 60;

    public static CharacteristicResolution Resolve(
        IReadOnlyList<ExtractedCharacteristic> extracted,
        IReadOnlyList<CharacteristicDefinition> master,
        string? equipmentType,
        string sourceFileName,
        DateTimeOffset now)
    {
        var definitions = master.ToDictionary(d => d.Id);
        var changedIds = new HashSet<string>();
        var newCount = 0;
        var values = new List<AssetCharacteristic>();
        var mapping = new List<(int, string, bool)>();
        var newIds = new HashSet<string>();

        for (var index = 0; index < extracted.Count; index++)
        {
            var item = extracted[index];
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var valueType = CharacteristicValueType.All.FirstOrDefault(t =>
                string.Equals(t, item.ValueType, StringComparison.OrdinalIgnoreCase)) ?? CharacteristicValueType.Alphanumeric;

            var definition = Find(definitions.Values, item.Name) ?? Find(definitions.Values, item.SourceParameter);
            var isNew = definition is null;

            if (definition is null)
            {
                // First time this characteristic is seen: add it to the master table.
                definition = new CharacteristicDefinition(
                    Id: NewId(item.Name, definitions),
                    Name: item.Name.Trim(),
                    ValueType: valueType,
                    Unit: Blank(item.Unit),
                    UnitSymbol: Blank(item.UnitSymbol),
                    Aliases: [],
                    Options: [],
                    EquipmentTypes: [],
                    CreatedUtc: now);
            }

            // The master entry decides the type: a value that does not fit it is skipped rather than stored wrongly.
            if (!HasValueFor(definition.ValueType, item))
            {
                continue;
            }

            // Already have a value for this characteristic from this manual? Keep the first one.
            if (values.Any(v => v.CharacteristicId == definition.Id))
            {
                continue;
            }

            var updated = Learn(definition, item, equipmentType);
            if (!ReferenceEquals(updated, definition) || !master.Any(m => m.Id == definition.Id))
            {
                changedIds.Add(updated.Id);
            }

            definitions[updated.Id] = updated;
            if (isNew)
            {
                newCount++;
                newIds.Add(updated.Id);
            }

            mapping.Add((index, updated.Id, newIds.Contains(updated.Id)));

            values.Add(new AssetCharacteristic(
                CharacteristicId: updated.Id,
                Name: updated.Name,
                ValueType: updated.ValueType,
                NumericValue: updated.ValueType == CharacteristicValueType.Numeric ? item.NumericValue : null,
                Unit: updated.Unit,
                UnitSymbol: updated.UnitSymbol,
                BooleanValue: updated.ValueType == CharacteristicValueType.YesNo ? item.BooleanValue : null,
                TextValue: updated.ValueType is CharacteristicValueType.Alphanumeric or CharacteristicValueType.MultipleChoice
                    ? Blank(item.TextValue)
                    : null,
                DateValue: updated.ValueType == CharacteristicValueType.Date ? Blank(item.DateValue) : null,
                Source: $"{sourceFileName}: {item.SourceText ?? item.SourceParameter}",
                ExtractedUtc: now));
        }

        return new CharacteristicResolution(
            values,
            changedIds.Select(id => definitions[id]).ToList(),
            newCount,
            mapping);
    }

    /// <summary>Adds the new values to the asset's list, filling only characteristics that have no value yet.</summary>
    public static CharacteristicMerge MergeIntoAsset(
        IReadOnlyList<AssetCharacteristic>? existing,
        IReadOnlyList<AssetCharacteristic> values)
    {
        var merged = (existing ?? []).ToList();
        var filled = new List<AssetCharacteristic>();
        var skipped = 0;

        foreach (var value in values)
        {
            var index = merged.FindIndex(c => c.CharacteristicId == value.CharacteristicId || Key(c.Name) == Key(value.Name));
            if (index >= 0 && merged[index].HasValue)
            {
                skipped++; // never overwrite a value that is already filled in
                continue;
            }

            if (index >= 0)
            {
                merged[index] = value;
            }
            else
            {
                merged.Add(value);
            }

            filled.Add(value);
        }

        return new CharacteristicMerge(merged, filled, skipped);
    }

    // Records what this manual taught us: the name it used (alias), the equipment type, a new multiple-choice option.
    private static CharacteristicDefinition Learn(CharacteristicDefinition definition, ExtractedCharacteristic item, string? equipmentType)
    {
        var aliases = definition.Aliases.ToList();
        foreach (var name in new[] { item.Name, item.SourceParameter })
        {
            if (!string.IsNullOrWhiteSpace(name) && Key(name) != Key(definition.Name) && aliases.All(a => Key(a) != Key(name)))
            {
                aliases.Add(name.Trim());
            }
        }

        var types = definition.EquipmentTypes.ToList();
        if (!string.IsNullOrWhiteSpace(equipmentType) && !types.Contains(equipmentType.Trim()))
        {
            types.Add(equipmentType.Trim());
        }

        var options = definition.Options.ToList();
        if (definition.ValueType == CharacteristicValueType.MultipleChoice &&
            Blank(item.TextValue) is { } option && options.All(o => Key(o) != Key(option)))
        {
            options.Add(option);
        }

        var unchanged = aliases.Count == definition.Aliases.Count &&
                        types.Count == definition.EquipmentTypes.Count &&
                        options.Count == definition.Options.Count;

        return unchanged ? definition : definition with { Aliases = aliases, EquipmentTypes = types, Options = options };
    }

    private static CharacteristicDefinition? Find(IEnumerable<CharacteristicDefinition> definitions, string? name)
    {
        var key = Key(name);
        return key.Length == 0
            ? null
            : definitions.FirstOrDefault(d => Key(d.Name) == key || d.Aliases.Any(a => Key(a) == key));
    }

    private static bool HasValueFor(string valueType, ExtractedCharacteristic item) => valueType switch
    {
        CharacteristicValueType.Numeric => item.NumericValue is not null,
        CharacteristicValueType.YesNo => item.BooleanValue is not null,
        CharacteristicValueType.Date => !string.IsNullOrWhiteSpace(item.DateValue),
        _ => !string.IsNullOrWhiteSpace(item.TextValue)
    };

    private static string NewId(string name, Dictionary<string, CharacteristicDefinition> existing)
    {
        var cleaned = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');
        var id = "CHR-" + (cleaned.Length <= MaxIdLength ? cleaned : cleaned[..MaxIdLength].TrimEnd('-'));
        var candidate = id;
        for (var n = 2; existing.ContainsKey(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    // "Head (m)", "head", "HEAD" all compare equal; letters and digits only.
    private static string Key(string? value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", "");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
