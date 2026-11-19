using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DocDetectives.Application.Skills;

/// <summary>
/// Single-purpose skill that evaluates extracted manual metadata against candidate system assets.
/// </summary>
public sealed class AssetMatchingSkill(
    IChatClient chatClient,
    IAssetCatalogGateway assetCatalog)
{
    public sealed record AssetMatchEvaluation(List<CandidateScore> Matches);

    public sealed record CandidateScore(
        string AssetId,
        double ConfidenceScore,
        string Reasoning);

    public async Task<IReadOnlyList<AssetMappingResult>> MatchManualToAssetsAsync(
        ExtractedManual manual,
        double autoLinkThreshold = 0.85,
        CancellationToken cancellationToken = default)
    {
        var primaryModel = manual.ModelNumbers.FirstOrDefault();
        var candidates = await assetCatalog.SearchAssetsAsync(
            manual.Manufacturer,
            primaryModel,
            cancellationToken);

        // Broaden search if strict manufacturer+model lookup returned nothing
        if (candidates.Count == 0)
        {
            candidates = await assetCatalog.SearchAssetsAsync(null, null, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var manualSnippet = manual.MarkdownContent.Length > 2000
            ? manual.MarkdownContent[..2000]
            : manual.MarkdownContent;

        var prompt = $$"""
            You are an industrial asset-matching specialist. Match the technical manual below to the candidate assets.
            Account for model number variants (e.g., "Series 400X" vs "S400X-REV2").
            Only return assets that genuinely correspond to this manual. Assign a confidence score between 0.0 and 1.0.

            MANUAL METADATA:
            - FileName: {{manual.FileName}}
            - Extracted Manufacturer: {{manual.Manufacturer ?? "Unknown"}}
            - Extracted Model Numbers: {{string.Join(", ", manual.ModelNumbers)}}
            - Equipment Category: {{manual.EquipmentCategory ?? "Unknown"}}
            - Opening Content Excerpt:
            {{manualSnippet}}

            CANDIDATE ASSETS IN SYSTEM:
            {{JsonSerializer.Serialize(candidates)}}
            """;

        var response = await chatClient.GetResponseAsync<AssetMatchEvaluation>(
            prompt,
            cancellationToken: cancellationToken);

        var results = new List<AssetMappingResult>();

        if (response.Result?.Matches is null)
        {
            return results;
        }

        foreach (var match in response.Result.Matches.Where(m => m.ConfidenceScore >= 0.50))
        {
            var status = match.ConfidenceScore >= autoLinkThreshold
                ? MappingStatus.AutoLinked
                : MappingStatus.PendingReview;

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
                match.AssetId,
                manual.ManualId,
                match.ConfidenceScore,
                match.Reasoning,
                status));
        }

        return results;
    }
}