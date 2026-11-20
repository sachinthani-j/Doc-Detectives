using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Drafts job plans (the "what to do" of preventive maintenance) from a manual's maintenance schedule
/// or from an inspection document's checklist.
/// </summary>
public sealed class JobPlanExtractionSkill(IChatClient chatClient)
{
    private static readonly string JobPlanRules = PromptLoader.Load("job-plan.md");

    public async Task<JobPlanExtraction> ExtractAsync(
        ExtractedManual document,
        DocumentType documentType,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            You are a maintenance planner. Draft job plans from the document below, following the rules.
            Also return the document's own code (DocumentCode, e.g. "JD-PL3-PREV-DRO-001" or "1-365-PK-00030-BL"),
            the materials and tools needed (Materials) and general safety notes (SafetyNotes).
            For an inspection document, also fill InspectedAsset with exactly what the document states about the
            inspected equipment: asset/equipment number (AssetId), serial number, equipment name, manufacturer and model.
            Leave a field null when it is not stated. For a manual, leave InspectedAsset null.

            DOCUMENT TYPE: {{documentType}}

            JOB PLAN RULES:
            {{JobPlanRules}}

            FILE NAME: {{document.FileName}}

            DOCUMENT CONTENT:
            {{document.MarkdownContent}}
            """;

        var response = await chatClient.GetResponseAsync<JobPlanExtraction>(
            prompt,
            cancellationToken: cancellationToken);

        return response.Result ?? new JobPlanExtraction(null, [], [], [], null);
    }
}
