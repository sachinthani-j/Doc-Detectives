using DocDetectives.Domain.Abstractions;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Single-purpose skill that answers technician questions strictly grounded in manuals linked to an AssetId.
/// </summary>
public sealed class TechnicalManualQaSkill(
    IChatClient chatClient,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IAssetCatalogGateway assetCatalog,
    IKnowledgeIndex knowledgeIndex)
{
    public async Task<string> AskAssetQuestionAsync(
        string assetId,
        string question,
        CancellationToken cancellationToken = default)
    {
        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return $"Asset '{assetId}' was not found in the system.";
        }

        var queryEmbeddings = await embeddingGenerator.GenerateAsync(
            [question],
            cancellationToken: cancellationToken);

        var relevantChunks = await knowledgeIndex.SearchByAssetAsync(
            assetId,
            queryEmbeddings[0].Vector,
            question,
            topK: 5,
            cancellationToken);

        if (relevantChunks.Count == 0)
        {
            return $"No technical manual sections are currently linked to asset {asset.Id} ({asset.Name}).";
        }

        var contextBlocks = relevantChunks.Select((c, i) =>
            $"[Source {i + 1}: {c.FileName} | Section: {c.SectionTitle}]\n{c.Content}");

        var prompt = $"""
            You are an expert maintenance engineer assistant. Answer the technician's question using ONLY the manual excerpts provided below.
            If the answer is not present in the excerpts, state that explicitly. Always cite the Source file name and Section title.

            TARGET ASSET:
            - Asset ID: {asset.Id}
            - Name: {asset.Name}
            - Manufacturer: {asset.Manufacturer}
            - Model: {asset.ModelNumber}

            RETRIEVED MANUAL EXCERPTS:
            {string.Join("\n\n---\n\n", contextBlocks)}

            TECHNICIAN QUESTION:
            {question}
            """;

        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return response.Text ?? "No response generated.";
    }
}