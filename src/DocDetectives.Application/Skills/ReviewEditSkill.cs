using System.Text.Json;
using DocDetectives.Application.Ingestion;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Applies a person's plain-words change ("set the frequency to 6 months", "remove line 3") to a draft that is
/// waiting for review. Returns the whole changed draft; <see cref="ReviewEditMerger"/> validates it and copies the
/// IDs and links back. Nothing is saved here.
/// </summary>
public sealed class ReviewEditSkill(IChatClient chatClient)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Task<JobPlanEdit> EditPlanAsync(JobPlanEdit current, string instruction, CancellationToken cancellationToken = default) =>
        EditAsync(current, instruction, """
            This is a drafted preventive maintenance (PM) plan.
            - IntervalUnit must be one of Hours, Days, Weeks, Months, Years (or null when there is no frequency);
              IntervalValue is the number, Interval a short text like "every 6 months".
            - LineType is "Task" (something to do) or "Inspection" (something to check and record).
              Inspection lines have AllowedResults (e.g. ["OK", "NOK"]); task lines have none.
            - Keep OriginalLineNumber on lines you keep or change (even when you reorder them); use null for new lines.
              Remove a line by leaving it out. Line numbers the user mentions refer to OriginalLineNumber.
            """, cancellationToken);

    public Task<AssetProposalEdit> EditProposalAsync(AssetProposalEdit current, string instruction, CancellationToken cancellationToken = default) =>
        EditAsync(current, instruction, """
            This is a proposed asset tree: the first asset is the main equipment, the others are its sub-assets
            (ParentId points to their parent's Id).
            - Assets with Existing = true already exist in the EAM: copy them unchanged, never edit or remove them.
            - Keep Id on assets you keep or change. Remove a new sub-asset by leaving it out.
            - Add a new sub-asset with Id = null and ParentId = the Id of its parent (the main asset's Id when not said).
            - Moving an asset to another parent is not supported: keep ParentId as it is.
            """, cancellationToken);

    public Task<CharacteristicsEdit> EditCharacteristicsAsync(CharacteristicsEdit current, string instruction, CancellationToken cancellationToken = default) =>
        EditAsync(current, instruction, """
            These are characteristic values of an asset, extracted from its manual.
            - Change Value and/or Unit of the characteristics the user names; Value is a number, yes/no, text or a
              date (yyyy-MM-dd). Remove a characteristic by leaving it out.
            - Don't add characteristics that are not in the list; put such requests in NotDone.
            """, cancellationToken);

    private async Task<T> EditAsync<T>(T current, string instruction, string rules, CancellationToken cancellationToken)
        where T : class
    {
        var prompt = $$"""
            You edit a draft that a maintenance engineer is reviewing before it is saved.
            Apply ONLY the change the engineer asks for; copy everything else exactly as it is.
            Return the complete draft after the change.
            Set ChangeSummary to one short sentence describing what you changed (in the engineer's language).
            For any part of the request you could not do, add a short reason to NotDone; set NotDone to [] otherwise.

            RULES FOR THIS DRAFT:
            {{rules}}

            CURRENT DRAFT (JSON):
            {{JsonSerializer.Serialize(current, Json)}}

            ENGINEER'S REQUEST:
            {{instruction}}
            """;

        var response = await chatClient.GetResponseAsync<T>(prompt, cancellationToken: cancellationToken);
        return response.Result ?? throw new InvalidOperationException("The AI returned no changed draft.");
    }
}
