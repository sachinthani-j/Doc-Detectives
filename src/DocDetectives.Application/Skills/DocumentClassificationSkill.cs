using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Decides whether an uploaded document is an equipment manual or an inspection document.
/// Only the opening of the document is sent: the type is clear from the title and first pages.
/// </summary>
public sealed class DocumentClassificationSkill(IChatClient chatClient)
{
    private const int ExcerptCharacters = 6000;

    public async Task<DocumentClassification> ClassifyAsync(
        ExtractedManual document,
        CancellationToken cancellationToken = default)
    {
        var excerpt = document.MarkdownContent.Length <= ExcerptCharacters
            ? document.MarkdownContent
            : document.MarkdownContent[..ExcerptCharacters];

        var prompt = $$"""
            Classify this industrial document as one of:
            - Manual: an equipment manual or instructions (operating, maintenance, installation, service manual, datasheet).
            - Inspection: an inspection checklist, inspection report, test report or certificate for a specific piece of
              equipment, with inspection points to check or that were checked.
            When in doubt, choose Manual. Give a one-sentence reason.

            FILE NAME: {{document.FileName}}

            DOCUMENT OPENING:
            {{excerpt}}
            """;

        var response = await chatClient.GetResponseAsync<DocumentClassification>(
            prompt,
            cancellationToken: cancellationToken);

        return response.Result ?? new DocumentClassification(DocumentType.Manual, "Classification failed; treated as a manual.");
    }
}
