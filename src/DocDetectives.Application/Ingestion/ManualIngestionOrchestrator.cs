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
    int EmbeddedChunkCount,
    string AssetCreationNote,
    IReadOnlyList<Asset> CreatedAssets);

/// <summary>Result for one uploaded file: exactly one of Manual or Inspection is set, depending on the document type.</summary>
public sealed record DocumentProcessingReport(
    string DocumentId,
    string FileName,
    DocumentClassification Classification,
    ManualProcessingReport? Manual,
    InspectionProcessingReport? Inspection);

public sealed class ManualIngestionOrchestrator(
    IManualExtractor manualExtractor,
    DocumentClassificationSkill classificationSkill,
    InspectionJobService inspectionJobService,
    ManualStructureExtractionSkill structureExtractionSkill,
    AssetMatchingSkill matchingSkill,
    IAssetCatalogGateway assetCatalog,
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

    /// <summary>
    /// Reads any uploaded document once, then routes it: manuals are matched to assets and embedded for chat,
    /// inspection documents become a job on the inspected asset.
    /// </summary>
    public async Task<DocumentProcessingReport> ProcessDocumentAsync(
        string filePath,
        double autoLinkThreshold = 0.85,
        bool autoCreateAssets = true,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract layout-aware Markdown via Azure AI Content Understanding
        ExtractedManual rawDocument;
        await using (var stream = File.OpenRead(filePath))
        {
            rawDocument = await manualExtractor.ExtractAsync(stream, Path.GetFileName(filePath), cancellationToken);
        }

        // 2. Decide what kind of document this is
        var classification = await classificationSkill.ClassifyAsync(rawDocument, cancellationToken);

        if (classification.DocumentType == DocumentType.Inspection)
        {
            var inspection = await inspectionJobService.ProcessAsync(rawDocument, autoLinkThreshold, cancellationToken);
            return new DocumentProcessingReport(rawDocument.ManualId, rawDocument.FileName, classification, null, inspection);
        }

        var manual = await ProcessManualAsync(rawDocument, autoLinkThreshold, autoCreateAssets, cancellationToken);
        return new DocumentProcessingReport(rawDocument.ManualId, rawDocument.FileName, classification, manual, null);
    }

    private async Task<ManualProcessingReport> ProcessManualAsync(
        ExtractedManual rawManual,
        double autoLinkThreshold,
        bool autoCreateAssets,
        CancellationToken cancellationToken)
    {

        // 3. Extract typed equipment asset structure via LLM Structured Outputs
        var structure = await structureExtractionSkill.ExtractStructureAsync(rawManual, cancellationToken);
        var enrichedManual = rawManual with
        {
            Manufacturer = structure.Manufacturer ?? rawManual.Manufacturer,
            ModelNumbers = structure.ModelNumbers.Count > 0 ? structure.ModelNumbers : rawManual.ModelNumbers,
            EquipmentCategory = structure.EquipmentCategory ?? rawManual.EquipmentCategory,
            AssetStructure = structure
        };

        // 4. Match extracted asset metadata against existing system assets
        IReadOnlyList<AssetMappingResult> mappings = await matchingSkill.MatchManualToAssetsAsync(
            enrichedManual,
            autoLinkThreshold,
            cancellationToken: cancellationToken);

        // 4b. No existing asset matched at all: create one if the manual has enough information.
        //     Any NeedsReview candidate blocks creation to avoid near-duplicate assets.
        //     The new root asset comes with its sub-asset hierarchy and spare parts (bill of materials).
        var newAssets = AssetTreeBuilder.Build(structure, enrichedManual.Manufacturer, out var missingInfo);
        IReadOnlyList<Asset> createdAssets = [];
        string assetCreationNote;

        if (mappings.Any(m => m.Status == MappingStatus.AutoLinked))
        {
            assetCreationNote = "Not needed: linked to an existing asset.";
        }
        else if (mappings.Count > 0)
        {
            assetCreationNote =
                $"Skipped: {mappings.Count} existing asset(s) are a possible match and need review " +
                "(score below the auto-link threshold). Creating one could duplicate them.";
        }
        else if (!autoCreateAssets)
        {
            assetCreationNote = "Skipped: AutoCreateAssets is turned off in configuration.";
        }
        else if (newAssets is null)
        {
            assetCreationNote = $"Skipped: the manual has no {missingInfo}.";
        }
        else
        {
            await assetCatalog.CreateAssetsAsync(newAssets, enrichedManual.ManualId, cancellationToken);
            createdAssets = newAssets;
            var root = newAssets[0];
            assetCreationNote =
                $"Created asset '{root.Id}' with {newAssets.Count - 1} sub-asset(s) and " +
                $"{newAssets.Sum(a => a.SpareParts?.Count ?? 0)} spare part(s).";

            mappings =
            [
                new AssetMappingResult(
                    AssetId: root.Id,
                    ManualId: enrichedManual.ManualId,
                    ConfidenceScore: 1.0,
                    Reasoning: $"No existing asset matched; created '{root.Name}' from the manual.",
                    Status: MappingStatus.Created)
            ];
        }

        // Sub-assets are linked too, so chat scoped to e.g. the pump still finds this manual.
        var linkedAssetIds = mappings
            .Where(m => m.Status is MappingStatus.AutoLinked or MappingStatus.Created)
            .Select(m => m.AssetId)
            .Concat(createdAssets.Select(a => a.Id))
            .Distinct()
            .ToList();

        // 5. Build chunks: prepend the extracted structured asset summary + section Markdown chunks
        var allChunks = new List<ManualChunk>
        {
            BuildStructuredSummaryChunk(enrichedManual, structure, linkedAssetIds)
        };
        allChunks.AddRange(chunker.ChunkManual(enrichedManual, linkedAssetIds));

        // 6. Generate vector embeddings for all extracted info & manual sections
        //    Only the text sent for embedding is shortened; the full Content is still stored and returned to the LLM.
        var embeddingInputs = allChunks.Select(c => TruncateForEmbedding(
            $"Manual: {c.FileName} | Manufacturer: {enrichedManual.Manufacturer ?? "Unknown"} | " +
            $"Models: {string.Join(", ", enrichedManual.ModelNumbers)} | Section: {c.SectionTitle}\n" +
            StripStructuredJson(c.Content)));

        var embeddings = await embeddingGenerator.GenerateAsync(
            embeddingInputs,
            cancellationToken: cancellationToken);

        var vectorizedChunks = allChunks
            .Select((chunk, idx) => chunk with { Embedding = embeddings[idx].Vector })
            .ToList();

        // 7. Store embedded chunks & extracted structured data in Azure AI Search
        await knowledgeIndex.UpsertChunksAsync(vectorizedChunks, cancellationToken);

        return new ManualProcessingReport(enrichedManual, mappings, vectorizedChunks.Count, assetCreationNote, createdAssets);
    }

    // The embedding model accepts at most 8192 tokens per input. Technical text (part numbers, tables)
    // can be as dense as ~2.5 characters per token, so 16,000 characters stays safely below the limit.
    private const int MaxEmbeddingCharacters = 16_000;
    private const string StructuredJsonMarker = "\nStructured JSON:";

    private static string TruncateForEmbedding(string text) =>
        text.Length <= MaxEmbeddingCharacters ? text : text[..MaxEmbeddingCharacters];

    // The summary chunk repeats all of its data as JSON; that adds tokens to the embedding but no meaning.
    private static string StripStructuredJson(string content)
    {
        var index = content.IndexOf(StructuredJsonMarker, StringComparison.Ordinal);
        return index >= 0 ? content[..index] : content;
    }

    private static ManualChunk BuildStructuredSummaryChunk(
        ExtractedManual manual,
        EquipmentAssetStructure structure,
        IReadOnlyList<string> assetIds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Manufacturer: {structure.Manufacturer ?? "Unknown"}");
        sb.AppendLine($"Equipment Name: {structure.EquipmentName ?? "Unknown"}");
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
                var owner = string.IsNullOrWhiteSpace(part.BelongsTo) ? "" : $" [fitted to: {part.BelongsTo}]";
                sb.AppendLine($"- {part.PartName} (Article #: {part.ArticleNumber}){owner}");
            }
        }

        if (structure.SubAssets.Count > 0)
        {
            sb.AppendLine("\nAsset Hierarchy (sub-assets):");
            foreach (var sub in structure.SubAssets)
            {
                sb.AppendLine($"- {sub.Name} (parent: {sub.ParentName ?? "whole equipment"}) - {sub.Reason}");
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