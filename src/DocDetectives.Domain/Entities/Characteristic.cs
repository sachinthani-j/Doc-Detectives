namespace DocDetectives.Domain.Entities;

/// <summary>Value types, matching the EAM's characteristic columns.</summary>
public static class CharacteristicValueType
{
    public const string Numeric = "Numeric";
    public const string YesNo = "YesNo";
    public const string Alphanumeric = "Alphanumeric";
    public const string Date = "Date";
    public const string MultipleChoice = "MultipleChoice";

    public static readonly IReadOnlyList<string> All = [Numeric, YesNo, Alphanumeric, Date, MultipleChoice];
}

/// <summary>
/// One entry in the characteristic master table (e.g. "Head", Numeric, unit "Metre", symbol "m").
/// The table grows as manuals are ingested: a characteristic found for the first time is added,
/// and later manuals reuse the existing entry instead of creating a new one.
/// </summary>
/// <param name="Aliases">Other names manuals used for this characteristic (e.g. "Delivery head", "Opvoerhoogte").</param>
/// <param name="Options">Choices for a MultipleChoice characteristic.</param>
/// <param name="EquipmentTypes">Equipment type codes of assets this characteristic was filled on.</param>
public sealed record CharacteristicDefinition(
    string Id,
    string Name,
    string ValueType,
    string? Unit,
    string? UnitSymbol,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Options,
    IReadOnlyList<string> EquipmentTypes,
    DateTimeOffset CreatedUtc);

/// <summary>A characteristic value on an asset, with the manual text it came from.</summary>
public sealed record AssetCharacteristic(
    string CharacteristicId,
    string Name,
    string ValueType,
    double? NumericValue,
    string? Unit,
    string? UnitSymbol,
    bool? BooleanValue,
    string? TextValue,
    string? DateValue,
    string? Source,
    DateTimeOffset ExtractedUtc)
{
    public bool HasValue =>
        NumericValue is not null || BooleanValue is not null ||
        !string.IsNullOrWhiteSpace(TextValue) || !string.IsNullOrWhiteSpace(DateValue);
}

/// <summary>What the LLM returns: characteristic values for one asset, mapped to the master table where possible.</summary>
public sealed record CharacteristicExtraction(List<ExtractedCharacteristic> Characteristics);

/// <param name="Name">Master table name when it is the same characteristic, otherwise a new short name.</param>
/// <param name="SourceParameter">The parameter name exactly as the manual wrote it.</param>
/// <param name="SourceText">The manual's original "parameter: value" text, kept as evidence.</param>
public sealed record ExtractedCharacteristic(
    string Name,
    string SourceParameter,
    string ValueType,
    string? Unit,
    string? UnitSymbol,
    double? NumericValue,
    bool? BooleanValue,
    string? TextValue,
    string? DateValue,
    string? SourceText);
