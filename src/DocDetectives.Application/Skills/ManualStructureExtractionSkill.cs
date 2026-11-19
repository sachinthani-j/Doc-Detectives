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
            FaultCodes: []);
    }
}