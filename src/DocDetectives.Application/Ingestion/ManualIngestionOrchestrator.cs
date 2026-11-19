using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace DocDetectives.Application.Ingestion;

public sealed record ManualProcessingReport(
    ExtractedManual ExtractedManual,
    IReadOnlyList<AssetMappingResult> Mappings,
    int EmbeddedChunkCount);

public sealed class ManualIngestionOrchestrator(
    IManualExtractor manualExtractor,
    ManualStructureExtractionSkill structureExtractionSkill,
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
        // 1. Extract layout-aware Markdown via Azure AI Content Understanding
        await using var stream = File.OpenRead(filePath);
        var fileName = Path.GetFileName(filePath);
        var rawManual = await manualExtractor.ExtractAsync(stream, fileName, cancellationToken);

        // 2. Extract typed equipment asset structure via LLM Structured Outputs
        var structure = await structureExtractionSkill.ExtractStructureAsync(rawManual, cancellationToken);
        var enrichedManual = rawManual with
        {
            Manufacturer = structure.Manufacturer ?? rawManual.Manufacturer,
            ModelNumbers = structure.ModelNumbers.Count > 0 ? structure.ModelNumbers : rawManual.ModelNumbers,
            EquipmentCategory = structure.EquipmentCategory ?? rawManual.EquipmentCategory,
            AssetStructure = structure
        };

        // 3. Match extracted asset metadata against existing system assets
        var mappings = await matchingSkill.MatchManualToAssetsAsync(
            enrichedManual,
            autoLinkThreshold,
            cancellationToken);

        var linkedAssetIds = mappings
            .Where(m => m.Status == MappingStatus.AutoLinked)
            .Select(m => m.AssetId)
            .Distinct()
            .ToList();

        // 4. Build chunks: prepend the extracted structured asset summary + section Markdown chunks
        var allChunks = new List<ManualChunk>
        {
            BuildStructuredSummaryChunk(enrichedManual, structure, linkedAssetIds)
        };
        allChunks.AddRange(chunker.ChunkManual(enrichedManual, linkedAssetIds));

        // 5. Generate vector embeddings for all extracted info & manual sections
        var embeddingInputs = allChunks.Select(c =>
            $"Manual: {c.FileName} | Manufacturer: {enrichedManual.Manufacturer ?? "Unknown"} | " +
            $"Models: {string.Join(", ", enrichedManual.ModelNumbers)} | Section: {c.SectionTitle}\n{c.Content}");

        var embeddings = await embeddingGenerator.GenerateAsync(
            embeddingInputs,
            cancellationToken: cancellationToken);

        var vectorizedChunks = allChunks
            .Select((chunk, idx) => chunk with { Embedding = embeddings[idx].Vector })
            .ToList();

        // 6. Store embedded chunks & extracted structured data in Azure AI Search
        await knowledgeIndex.UpsertChunksAsync(vectorizedChunks, cancellationToken);

        return new ManualProcessingReport(enrichedManual, mappings, vectorizedChunks.Count);
    }

    private static ManualChunk BuildStructuredSummaryChunk(
        ExtractedManual manual,
        EquipmentAssetStructure structure,
        IReadOnlyList<string> assetIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Manufacturer: {structure.Manufacturer ?? "Unknown"}");
        sb.AppendLine($"Model Numbers: {string.Join(", ", structure.ModelNumbers)}");
        sb.AppendLine($"Equipment Category: {structure.EquipmentCategory ?? "Unknown"}");
        if (!string.IsNullOrWhiteSpace(structure.DocumentCode))
        {
            sb.AppendLine($"Document Code: {structure.DocumentCode}");
        }

        if (structure.TechnicalSpecifications.Count > 0)
        {
            sb.AppendLine("\nTechnical Specifications:");
            foreach (var spec in structure.TechnicalSpecifications)
            {
                sb.AppendLine($"- {spec.Parameter}: {spec.Value}");
            }
        }

        if (structure.SafetyWarnings.Count > 0)
        {
            sb.AppendLine("\nSafety Warnings:");
            foreach (var warning in structure.SafetyWarnings)
            {
                sb.AppendLine($"- {warning}");
            }
        }

        if (!string.IsNullOrWhiteSpace(structure.CalibrationAndMaintenance))
        {
            sb.AppendLine($"\nCalibration & Maintenance:\n{structure.CalibrationAndMaintenance}");
        }

        if (structure.SpareParts.Count > 0)
        {
            sb.AppendLine("\nSpare Parts & Accessories:");
            foreach (var part in structure.SpareParts)
            {
                sb.AppendLine($"- {part.PartName} (Article #: {part.ArticleNumber})");
            }
        }

        if (structure.FaultCodes.Count > 0)
        {
            sb.AppendLine("\nFault Codes & Troubleshooting:");
            foreach (var fault in structure.FaultCodes)
            {
                sb.AppendLine($"- {fault.CodeOrSymptom}: Cause = {fault.Cause} | Resolution = {fault.Resolution}");
            }
        }

        sb.AppendLine("\nStructured JSON:");
        sb.AppendLine(JsonSerializer.Serialize(structure));

        return new ManualChunk(
            ChunkId: $"{manual.ManualId}_structured_summary",
            ManualId: manual.ManualId,
            FileName: manual.FileName,
            SectionTitle: "Extracted Asset Summary & Specifications",
            Content: sb.ToString().Trim(),
            AssetIds: assetIds,
            Embedding: ReadOnlyMemory<float>.Empty);
    }
}