using System.Runtime.CompilerServices;
using System.Text;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>A manual section used to ground an answer. <see cref="Number"/> matches the [n] citations in the answer.</summary>
public sealed record AnswerSource(int Number, string ManualId, string FileName, string SectionTitle, string Content);

/// <summary>
/// The retrieval half of a question: the asset (if any), the manual sections found, and the prompt to send.
/// <see cref="Message"/> is set instead of <see cref="Prompt"/> when there is nothing to ask the model about.
/// </summary>
public sealed record QaContext(
    Asset? Asset,
    IReadOnlyList<AnswerSource> Sources,
    string? Prompt,
    string? Message);

/// <summary>
/// Single-purpose skill that answers technician questions strictly grounded in manuals linked to an AssetId,
/// or in all manuals when no AssetId is given.
/// </summary>
public sealed class TechnicalManualQaSkill(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IAssetCatalogGateway assetCatalog,
    IKnowledgeIndex knowledgeIndex)
{
    /// <summary>Finds the asset and the relevant manual sections, and builds the grounded prompt.</summary>
    public async Task<QaContext> PrepareAsync(
        string? assetId,
        string question,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = null;
        if (!string.IsNullOrWhiteSpace(assetId))
        {
            asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
            if (asset is null)
            {
                return new QaContext(null, [], null, $"Asset '{assetId}' was not found in the system.");
            }
        }

        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [question],
            cancellationToken: cancellationToken);

        var chunks = await knowledgeIndex.SearchByAssetAsync(
            asset?.Id,
            queryEmbeddings[0].Vector,
            question,
            topK,
            cancellationToken);

        if (chunks.Count == 0)
        {
            var message = asset is null
                ? "No manual sections matched this question. Have any manuals been ingested yet?"
                : $"No technical manual sections are currently linked to asset {asset.Id} ({asset.Name}).";
            return new QaContext(asset, [], null, message);
        }

        var sources = chunks
            .Select((c, i) => new AnswerSource(i + 1, c.ManualId, c.FileName, c.SectionTitle, c.Content))
            .ToList();

        return new QaContext(asset, sources, BuildPrompt(asset, sources, question), null);
    }

    /// <summary>Streams the answer text for a prepared context.</summary>
    public async IAsyncEnumerable<string> StreamAnswerAsync(
        QaContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (context.Prompt is null)
        {
            yield return context.Message ?? "No response generated.";
            yield break;
        }

        await foreach (var update in chatClient.GetStreamingResponseAsync(context.Prompt, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    public async Task<string> AskAssetQuestionAsync(
        string? assetId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var context = await PrepareAsync(assetId, question, cancellationToken: cancellationToken);
        if (context.Prompt is null)
        {
            return context.Message ?? "No response generated.";
        }

        var response = await chatClient.GetResponseAsync(context.Prompt, cancellationToken: cancellationToken);
        return response.Text ?? "No response generated.";
    }

    private static string BuildPrompt(Asset? asset, IReadOnlyList<AnswerSource> sources, string question)
    {
        var excerpts = string.Join("\n\n---\n\n", sources.Select(s =>
            $"[{s.Number}] {s.FileName} | Section: {s.SectionTitle}\n{s.Content}"));

        var target = new StringBuilder();
        if (asset is null)
        {
            target.AppendLine("No specific asset was selected; the excerpts may come from different manuals.");
        }
        else
        {
            target.AppendLine($"- Asset ID: {asset.Id}");
            target.AppendLine($"- Name: {asset.Name}");
            target.AppendLine($"- Manufacturer: {asset.Manufacturer}");
            target.AppendLine($"- Model: {asset.ModelNumber}");
        }

        return $"""
            You are an expert maintenance engineer assistant. Answer the technician's question using ONLY the manual excerpts provided below.
            If the answer is not present in the excerpts, state that explicitly.
            Cite sources inline with their bracketed number, e.g. [1] or [2][3]. Do not list the sources again at the end.
            Use short paragraphs or numbered steps; use Markdown for lists, bold and tables where it helps.

            TARGET ASSET:
            {target}
            RETRIEVED MANUAL EXCERPTS:
            {excerpts}

            TECHNICIAN QUESTION:
            {question}
            """;
    }
}
