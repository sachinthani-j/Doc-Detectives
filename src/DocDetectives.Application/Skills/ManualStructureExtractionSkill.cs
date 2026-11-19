using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Single-purpose skill that extracts a typed equipment asset structure from layout-preserved manual Markdown.
/// </summary>
public sealed class ManualStructureExtractionSkill(IChatClient chatClient)
{
    public async Task<EquipmentAssetStructure> ExtractStructureAsync(
        ExtractedManual manual,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            You are an industrial equipment documentation analyst. Extract the structured asset data from the technical manual below.
            Preserve exact OEM part/article numbers, model numbers, error codes, and numeric tolerances/limits.
            EquipmentName is the product name as the manual titles it (e.g. "EFG 213-320 Electric Forklift Truck"), without the manufacturer name.
            If a field is not present in the manual, return null or an empty list—do not guess.

            SubAssets: propose the asset hierarchy below the whole equipment, following the rules below.
            Do not include the whole equipment itself. Use short component names (e.g. "Hydraulic Pump"); ParentName is
            the name of another sub-asset, or null when it sits directly under the whole equipment.
            SpareParts.BelongsTo: the sub-asset name the part is fitted to, or null when it belongs to the whole equipment.

            ASSET STRUCTURE RULES:
            {{AssetStructureRules}}

            MANUAL FILE: {{manual.FileName}}

            MANUAL MARKDOWN CONTENT:
            {{manual.MarkdownContent}}
            """;

        var response = await chatClient.GetResponseAsync<EquipmentAssetStructure>(
            prompt,
            cancellationToken: cancellationToken);

        return response.Result ?? new EquipmentAssetStructure(
            Manufacturer: null,
            EquipmentName: null,
            ModelNumbers: [],
            EquipmentCategory: null,
            DocumentCode: null,
            TechnicalSpecifications: [],
            SafetyWarnings: [],
            CalibrationAndMaintenance: null,
            SpareParts: [],
            FaultCodes: [],
            SubAssets: []);
    }

    private static readonly string AssetStructureRules = LoadPrompt("asset-structure.md");

    private static string LoadPrompt(string fileName)
    {
        using var stream = typeof(ManualStructureExtractionSkill).Assembly
            .GetManifestResourceStream($"DocDetectives.Prompts.{fileName}")
            ?? throw new InvalidOperationException($"Embedded prompt '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}