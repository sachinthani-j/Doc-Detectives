using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocDetectives.Application.Ingestion;

public sealed record ManualProcessingReport(
    ExtractedManual ExtractedManual,
    IReadOnlyList<AssetMappingResult> Mappings,
    int EmbeddedChunkCount,
    string AssetCreationNote);

public sealed class ManualIngestionOrchestrator(
    IManualExtractor manualExtractor,
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

    public async Task<ManualProcessingReport> ProcessManualAsync(
        string filePath,
        double autoLinkThreshold = 0.85,
        bool autoCreateAssets = true,
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
        IReadOnlyList<AssetMappingResult> mappings = await matchingSkill.MatchManualToAssetsAsync(
            enrichedManual,
            autoLinkThreshold,
            cancellationToken);

        // 3b. No existing asset matched at all: create one if the manual has enough information.
        //     Any NeedsReview candidate blocks creation to avoid near-duplicate assets.
        var newAsset = TryBuildAssetFromManual(structure, enrichedManual.Manufacturer, out var missingInfo);
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
        else if (newAsset is null)
        {
            assetCreationNote = $"Skipped: the manual has no {missingInfo}.";
        }
        else
        {
            await assetCatalog.CreateAssetAsync(newAsset, enrichedManual.ManualId, cancellationToken);
            assetCreationNote = $"Created asset '{newAsset.Id}'.";

            mappings =
            [
                new AssetMappingResult(
                    AssetId: newAsset.Id,
                    ManualId: enrichedManual.ManualId,
                    ConfidenceScore: 1.0,
                    Reasoning: $"No existing asset matched; created '{newAsset.Name}' from the manual.",
                    Status: MappingStatus.Created)
            ];
        }

        var linkedAssetIds = mappings
            .Where(m => m.Status is MappingStatus.AutoLinked or MappingStatus.Created)
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

        // 6. Store embedded chunks & extracted structured data in Azure AI Search
        await knowledgeIndex.UpsertChunksAsync(vectorizedChunks, cancellationToken);

        return new ManualProcessingReport(enrichedManual, mappings, vectorizedChunks.Count, assetCreationNote);
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

    /// <summary>
    /// Returns an asset when the manual names the equipment and its manufacturer (both mandatory).
    /// Model numbers and category are optional; several models are stored together as one comma-separated value.
    /// </summary>
    private static Asset? TryBuildAssetFromManual(
        EquipmentAssetStructure structure,
        string? fallbackManufacturer,
        out string? missingInfo)
    {
        var manufacturer = (structure.Manufacturer ?? fallbackManufacturer)?.Trim();
        var name = structure.EquipmentName?.Trim();

        missingInfo = (string.IsNullOrEmpty(manufacturer), string.IsNullOrEmpty(name)) switch
        {
            (true, true) => "manufacturer and no equipment name",
            (true, false) => "manufacturer",
            (false, true) => "equipment name",
            _ => null
        };

        if (missingInfo is not null)
        {
            return null;
        }

        var models = structure.ModelNumbers
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new Asset(
            Id: BuildAssetId(manufacturer, name),
            Name: name,
            Manufacturer: manufacturer,
            ModelNumber: string.Join(", ", models),
            SerialNumber: string.Empty, // manuals describe an equipment type, not a physical unit
            Category: structure.EquipmentCategory?.Trim() ?? string.Empty);
    }

    // Deterministic, Azure Search key-safe ID (letters, digits, '-') so re-ingestion updates the same asset.
    private static string BuildAssetId(string manufacturer, string name)
    {
        static string Clean(string value) =>
            Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]+", "-").Trim('-');

        var id = $"AUTO-{Clean(manufacturer)}-{Clean(name)}";
        return id.Length <= 128 ? id : id[..128].TrimEnd('-');
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