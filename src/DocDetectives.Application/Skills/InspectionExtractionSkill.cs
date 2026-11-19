using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Extracts the asset reference and the inspection lines from an inspection checklist or report.
/// </summary>
public sealed class InspectionExtractionSkill(IChatClient chatClient)
{
    public async Task<InspectionDocument> ExtractAsync(
        ExtractedManual document,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            You are an industrial maintenance analyst. Extract the inspection data from the document below.

            Asset reference: copy exactly what the document states about the inspected equipment — asset/equipment number
            (AssetId), serial number, equipment name, manufacturer and model. Leave a field null if it is not stated.

            Lines: one entry per inspection point, in document order. Description is the check to perform.
            Criteria is the requirement or limit if stated (e.g. "max 12 mm"). Result, Measurement and Remarks are only
            filled when the document records an outcome (e.g. "OK", "Not OK", "N/A", "12.4 mm", inspector notes);
            for a blank checklist leave them null.
            Component is the part of the equipment the check is about, as named in the document (e.g. "Drive Train",
            "Fan Motor", "Spray nozzles"); null when the check is about the equipment as a whole.
            Do not invent values. Keep the document's language.

            FILE NAME: {{document.FileName}}

            DOCUMENT CONTENT:
            {{document.MarkdownContent}}
            """;

        var response = await chatClient.GetResponseAsync<InspectionDocument>(
            prompt,
            cancellationToken: cancellationToken);

        return response.Result ?? new InspectionDocument(
            Title: null,
            AssetId: null,
            SerialNumber: null,
            AssetName: null,
            Manufacturer: null,
            ModelNumber: null,
            InspectionDate: null,
            Inspector: null,
            Lines: []);
    }
}
