using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;

namespace DocDetectives.Application.Ingestion;

public sealed record ManualProcessingReport(
    ExtractedManual ExtractedManual,
    IReadOnlyList<AssetMappingResult> Mappings,
    int EmbeddedChunkCount);

public sealed class ManualIngestionOrchestrator(
    IManualExtractor manualExtractor,
    AssetMatchingSkill matchingSkill,
    MarkdownManualChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IKnowledgeIndex knowledgeIndex)
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".docx", ".doc", ".png", ".jpg", ".jpeg", ".tiff" };

    public IReadOnlyList<string> GetManualFilesFromFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Manuals folder not found at path: {Path.GetFullPath(folderPath)}");
        }

        return Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ManualProcessingReport> ProcessManualAsync(
        string filePath,
        double autoLinkThreshold = 0.85,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract multimodal content via Azure AI Content Understanding
        await using var stream = File.OpenRead(filePath);
        var fileName = Path.GetFileName(filePath);
        var extracted = await manualExtractor.ExtractAsync(stream, fileName, cancellationToken);

        // 2. Match manual metadata against existing system assets
        var mappings = await matchingSkill.MatchManualToAssetsAsync(
            extracted,
            autoLinkThreshold,
            cancellationToken);

        var linkedAssetIds = mappings
            .Where(m => m.Status == MappingStatus.AutoLinked)
            .Select(m => m.AssetId)
            .Distinct()
            .ToList();

        // 3. Chunk Markdown along section headings
        var rawChunks = chunker.ChunkManual(extracted, linkedAssetIds);
        if (rawChunks.Count == 0)
        {
            return new ManualProcessingReport(extracted, mappings, 0);
        }

        // 4. Generate vector embeddings for all extracted chunks
        var embeddingInputs = rawChunks.Select(c =>
            $"Manual: {c.FileName} | Section: {c.SectionTitle}\n{c.Content}");

        var embeddings = await embeddingGenerator.GenerateAsync(
            embeddingInputs,
            cancellationToken: cancellationToken);

        var vectorizedChunks = rawChunks
            .Select((chunk, idx) => chunk with { Embedding = embeddings[idx].Vector })
            .ToList();

        // 5. Store embedded chunks in Azure AI Search
        await knowledgeIndex.UpsertChunksAsync(vectorizedChunks, cancellationToken);

        return new ManualProcessingReport(extracted, mappings, vectorizedChunks.Count);
    }
}