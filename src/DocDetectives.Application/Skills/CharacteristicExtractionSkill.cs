using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Turns a manual's technical specifications into characteristic values for one asset,
/// reusing the characteristic master table wherever a characteristic already exists.
/// </summary>
public sealed class CharacteristicExtractionSkill(IChatClient chatClient)
{
    private static readonly string CharacteristicRules = PromptLoader.Load("characteristics.md");

    public async Task<CharacteristicExtraction> ExtractAsync(
        Asset asset,
        ExtractedManual manual,
        IReadOnlyList<TechnicalSpecItem> specifications,
        IReadOnlyList<CharacteristicDefinition> master,
        CancellationToken cancellationToken = default)
    {
        var masterLines = master.Count == 0
            ? "(empty: every characteristic you find is new)"
            : string.Join("\n", master.Select(d =>
                $"- {d.Name} | {d.ValueType} | {d.Unit ?? "-"} ({d.UnitSymbol ?? "-"})" +
                (d.Aliases.Count > 0 ? $" | also called: {string.Join(", ", d.Aliases)}" : "") +
                (d.Options.Count > 0 ? $" | options: {string.Join(", ", d.Options)}" : "")));

        var specLines = string.Join("\n", specifications.Select(s => $"- {s.Parameter}: {s.Value}"));

        var prompt = $$"""
            You fill in the characteristics of one asset from its manual's technical specifications, following the rules.

            CHARACTERISTIC RULES:
            {{CharacteristicRules}}

            ASSET:
            - Name: {{asset.Name}}
            - Manufacturer: {{asset.Manufacturer}}
            - Model: {{(string.IsNullOrWhiteSpace(asset.ModelNumber) ? "unknown" : asset.ModelNumber)}}
            - Equipment type code: {{(string.IsNullOrWhiteSpace(asset.Category) ? "unknown" : asset.Category)}}

            MANUAL: {{manual.FileName}} (models covered: {{(manual.ModelNumbers.Count > 0 ? string.Join(", ", manual.ModelNumbers) : "not stated")}})

            CHARACTERISTIC MASTER TABLE (Name | ValueType | Unit (symbol)):
            {{masterLines}}

            TECHNICAL SPECIFICATIONS FROM THE MANUAL:
            {{specLines}}
            """;

        var response = await chatClient.GetResponseAsync<CharacteristicExtraction>(
            prompt,
            cancellationToken: cancellationToken);

        return response.Result ?? new CharacteristicExtraction([]);
    }
}
