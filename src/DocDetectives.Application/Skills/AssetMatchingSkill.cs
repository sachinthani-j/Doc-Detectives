using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Skills;

public sealed class AssetMatchingSkill(
    IAssetCatalogGateway assetCatalog,
    IChatClient chatClient)
{
    public async Task<IReadOnlyList<AssetMappingResult>> MatchManualToAssetsAsync(
        ExtractedManual manual,
        double autoLinkThreshold = 0.85,
        CancellationToken cancellationToken = default)
    {
        // 1. Build search terms from all extracted model numbers, falling back to category or file name
        var modelQuery = manual.ModelNumbers.Count > 0
            ? string.Join(" ", manual.ModelNumbers)
            : manual.EquipmentCategory ?? Path.GetFileNameWithoutExtension(manual.FileName);

        var candidates = await assetCatalog.SearchAssetsAsync(
            manual.Manufacturer,
            modelQuery,
            cancellationToken);

        // 2. Fallback: if manufacturer + model returned no hits in the index, search by model/category alone
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(manual.Manufacturer))
        {
            candidates = await assetCatalog.SearchAssetsAsync(
                manufacturer: null,
                modelKeyword: modelQuery,
                cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateLines = string.Join("\n", candidates.Select(a =>
            $"- AssetId: {a.Id} | Name: {a.Name} | Manufacturer: {a.Manufacturer} | Model: {a.ModelNumber} | Category: {a.Category}"));

        var previewLength = Math.Min(manual.MarkdownContent.Length, 1500);
        var excerpt = manual.MarkdownContent[..previewLength];

        var prompt = $$"""
            You are an industrial asset-mapping specialist. Compare the extracted equipment manual metadata against the candidate assets from the asset index and score each relevant match from 0.0 to 1.0.
            Only include assets that genuinely match the equipment described in the manual (score >= 0.50).

            EXTRACTED MANUAL METADATA:
            - Manual ID: {{manual.ManualId}}
            - File Name: {{manual.FileName}}
            - Manufacturer: {{manual.Manufacturer ?? "Unknown"}}
            - Model Numbers: {{(manual.ModelNumbers.Count > 0 ? string.Join(", ", manual.ModelNumbers) : "Unknown")}}
            - Equipment Category: {{manual.EquipmentCategory ?? "Unknown"}}
            - Document Code: {{manual.AssetStructure?.DocumentCode ?? "Unknown"}}

            MANUAL OPENING EXCERPT:
            {{excerpt}}

            CANDIDATE ASSETS FROM INDEX:
            {{candidateLines}}
            """;

        // 3. Score candidate assets using structured output
        var evaluation = await chatClient.GetResponseAsync<AssetMatchBatchResponse>(
            prompt,
            cancellationToken: cancellationToken);

        var matches = evaluation.Result?.Matches ?? [];
        var results = new List<AssetMappingResult>();

        foreach (var match in matches.Where(m => m.ConfidenceScore >= 0.50))
        {
            var status = match.ConfidenceScore >= autoLinkThreshold
                ? MappingStatus.AutoLinked
                : MappingStatus.NeedsReview;

            if (status == MappingStatus.AutoLinked)
            {
                await assetCatalog.LinkManualToAssetAsync(
                    match.AssetId,
                    manual.ManualId,
                    match.ConfidenceScore,
                    status,
                    cancellationToken);
            }

            results.Add(new AssetMappingResult(
                AssetId: match.AssetId,
                ManualId: manual.ManualId,
                ConfidenceScore: match.ConfidenceScore,
                Status: status,
                Reasoning: match.Reasoning));
        }

        return results;
    }

    private sealed record AssetMatchBatchResponse(List<AssetMatchCandidateScore> Matches);

    private sealed record AssetMatchCandidateScore(
        string AssetId,
        double ConfidenceScore,
        string Reasoning);
}