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
    IReadOnlyList<Asset> CreatedAssets,
    IReadOnlyList<Asset> ProposedAssets,
    IReadOnlySet<string>? ExistingAssetIds = null);

/// <summary>
/// An asset tree waiting for review: nothing is created until a person approves it.
/// Characteristics are extracted already and applied when the root asset is created.
/// When the manual matched an existing asset, the tree mixes existing EAM assets (in ExistingAssetIds; never
/// rewritten) with the missing sub-assets the manual describes (created on approval).
/// </summary>
public sealed record AssetProposal(
    string ManualId,
    string SourceFileName,
    IReadOnlyList<Asset> Assets,
    IReadOnlyList<ExtractedCharacteristic> Characteristics,
    IReadOnlySet<string>? ExistingAssetIds = null)
{
    public bool IsExisting(string assetId) => ExistingAssetIds?.Contains(assetId) == true;

    /// <summary>The assets approval would create.</summary>
    public IReadOnlyList<Asset> NewAssets => Assets.Where(a => !IsExisting(a.Id)).ToList();
}

/// <summary>
/// Result for one uploaded file: exactly one of Manual or Inspection is set, depending on the document type.
/// JobPlans is set when job plan drafting is enabled; Characteristics for manuals with a known asset.
/// </summary>
public sealed record DocumentProcessingReport(
    string DocumentId,
    string FileName,
    DocumentClassification Classification,
    ManualProcessingReport? Manual,
    InspectionProcessingReport? Inspection,
    JobPlanReport? JobPlans,
    CharacteristicReport? Characteristics = null,
    AssetProposal? AssetProposal = null,
    string? ManufacturerDetailsNote = null);

/// <summary>
/// The steps of <see cref="ManualIngestionOrchestrator.ProcessDocumentAsync"/>, in order.
/// Inspection documents jump from <see cref="AnalyzingStructure"/> (classification) straight to <see cref="Completed"/>.
/// </summary>
public enum IngestionStage
{
    ExtractingContent,
    AnalyzingStructure,
    MatchingAssets,
    EmbeddingChunks,
    StoringInIndex,
    Completed
}

public sealed class ManualIngestionOrchestrator(
    IManualExtractor manualExtractor,
    DocumentClassificationSkill classificationSkill,
    InspectionJobService inspectionJobService,
    JobPlanService jobPlanService,
    CharacteristicService characteristicService,
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
    /// inspection documents become a job on the inspected asset. Both can also produce draft job plans.
    /// </summary>
    public async Task<DocumentProcessingReport> ProcessDocumentAsync(
        string filePath,
        double autoLinkThreshold = 0.85,
        bool autoCreateAssets = true,
        bool generateJobPlans = true,
        bool extractCharacteristics = true,
        bool reviewNewAssets = true,
        bool createInspectionJobs = false,
        IProgress<IngestionStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Extract layout-aware Markdown via Azure AI Content Understanding
        progress?.Report(IngestionStage.ExtractingContent);
        ExtractedManual rawDocument;
        await using (var stream = File.OpenRead(filePath))
        {
            rawDocument = await manualExtractor.ExtractAsync(stream, Path.GetFileName(filePath), cancellationToken);
        }

        // 2. Decide what kind of document this is
        progress?.Report(IngestionStage.AnalyzingStructure);
        var classification = await classificationSkill.ClassifyAsync(rawDocument, cancellationToken);

        if (classification.DocumentType == DocumentType.Inspection)
        {
            // Jobs are off by default: the checklist becomes a draft job plan, which finds its asset itself.
            var inspection = createInspectionJobs
                ? await inspectionJobService.ProcessAsync(rawDocument, autoLinkThreshold, cancellationToken)
                : null;
            var inspectionPlans = generateJobPlans
                ? await jobPlanService.DraftAsync(
                    rawDocument, DocumentType.Inspection, inspection?.Asset?.Id, autoLinkThreshold, cancellationToken)
                : null;

            progress?.Report(IngestionStage.Completed);
            return new DocumentProcessingReport(
                rawDocument.ManualId, rawDocument.FileName, classification, null, inspection, inspectionPlans);
        }

        var manual = await ProcessManualAsync(rawDocument, autoLinkThreshold, autoCreateAssets, reviewNewAssets, progress, cancellationToken);

        // The manual's asset: the best auto-linked existing asset, otherwise the one just created or proposed.
        // (When the root matched, created/proposed assets are only the missing sub-assets, so the match comes first.)
        // A proposed asset keeps its ID through review, so drafted job plans can already point to it.
        var manualAssetId = manual.Mappings
                                .Where(m => m.Status == MappingStatus.AutoLinked)
                                .OrderByDescending(m => m.ConfidenceScore)
                                .FirstOrDefault()?.AssetId
                            ?? manual.CreatedAssets.FirstOrDefault()?.Id
                            ?? manual.ProposedAssets.FirstOrDefault()?.Id;
        var rootExists = manual.ExistingAssetIds?.Contains(manualAssetId ?? string.Empty) == true
                         || manual.Mappings.Any(m => m.Status == MappingStatus.AutoLinked);

        // The enriched manual carries the extracted manufacturer and models onto the plans.
        var manualPlans = generateJobPlans
            ? await jobPlanService.DraftAsync(
                manual.ExtractedManual, DocumentType.Manual, manualAssetId, autoLinkThreshold, cancellationToken)
            : null;

        // A proposed new root asset does not exist yet: extract its characteristics now and apply them on approval.
        if (manual.ProposedAssets.Count > 0 && !rootExists)
        {
            var proposedCharacteristics = extractCharacteristics
                ? await characteristicService.ExtractAsync(manual.ProposedAssets[0], manual.ExtractedManual, cancellationToken)
                : [];

            var proposal = new AssetProposal(
                manual.ExtractedManual.ManualId,
                manual.ExtractedManual.FileName,
                manual.ProposedAssets,
                proposedCharacteristics);

            progress?.Report(IngestionStage.Completed);
            return new DocumentProcessingReport(
                rawDocument.ManualId, rawDocument.FileName, classification, manual, null, manualPlans, null, proposal);
        }

        // Characteristics go straight onto an existing (or just created) asset; only empty ones are filled.
        var characteristics = extractCharacteristics && manualAssetId is not null
            ? await characteristicService.FillFromManualAsync(manualAssetId, manual.ExtractedManual, cancellationToken)
            : null;

        // The root matched an existing asset but the manual has sub-assets the EAM lacks: those wait for review,
        // shown together with the existing ones they hang under ("link existing A and B, create new C").
        var partialProposal = manual.ProposedAssets.Count > 0
            ? new AssetProposal(
                manual.ExtractedManual.ManualId,
                manual.ExtractedManual.FileName,
                manual.ProposedAssets,
                [],
                manual.ExistingAssetIds)
            : null;

        // A matched existing asset gets the manufacturer name and build year from the manual, if it has none yet.
        var manufacturerNote = manualAssetId is not null && rootExists
            ? await FillManufacturerDetailsAsync(
                manualAssetId,
                manual.ExtractedManual.Manufacturer,
                AssetTreeBuilder.ValidYear(manual.ExtractedManual.AssetStructure?.ManufactureYear),
                cancellationToken)
            : null;

        progress?.Report(IngestionStage.Completed);
        return new DocumentProcessingReport(
            rawDocument.ManualId, rawDocument.FileName, classification, manual, null, manualPlans, characteristics,
            partialProposal, manufacturerNote);
    }

    // Only empty fields are filled; a value already on the asset is never overwritten.
    private async Task<string> FillManufacturerDetailsAsync(
        string assetId,
        string? manufacturerName,
        int? manufactureYear,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manufacturerName) && manufactureYear is null)
        {
            return "Manufacturer name and build year not found in the manual.";
        }

        var asset = await assetCatalog.GetAssetByIdAsync(assetId, cancellationToken);
        if (asset is null)
        {
            return $"Skipped: asset '{assetId}' not found.";
        }

        var setName = string.IsNullOrWhiteSpace(asset.ManufacturerName) && !string.IsNullOrWhiteSpace(manufacturerName)
            ? manufacturerName.Trim()
            : null;
        var setYear = asset.ManufactureYear is null ? manufactureYear : null;

        if (setName is null && setYear is null)
        {
            return $"Kept existing values (manufacturer name: {asset.ManufacturerName ?? "-"}, year: {asset.ManufactureYear?.ToString() ?? "-"}).";
        }

        await assetCatalog.UpdateManufacturerDetailsAsync(assetId, setName, setYear, cancellationToken);
        return string.Join(", ", new[]
        {
            setName is null ? null : $"set manufacturer name '{setName}'",
            setYear is null ? null : $"set build year {setYear}"
        }.OfType<string>()) + $" on asset '{assetId}'.";
    }

    private async Task<ManualProcessingReport> ProcessManualAsync(
        ExtractedManual rawManual,
        double autoLinkThreshold,
        bool autoCreateAssets,
        bool reviewNewAssets,
        IProgress<IngestionStage>? progress,
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
        progress?.Report(IngestionStage.MatchingAssets);
        IReadOnlyList<AssetMappingResult> mappings = await matchingSkill.MatchManualToAssetsAsync(
            enrichedManual,
            autoLinkThreshold,
            cancellationToken: cancellationToken);

        // 4b. No existing asset matched at all: create one if the manual has enough information.
        //     Any NeedsReview candidate blocks creation to avoid near-duplicate assets.
        //     The new root asset comes with its sub-asset hierarchy and spare parts (bill of materials).
        var newAssets = AssetTreeBuilder.Build(structure, enrichedManual.Manufacturer, out var missingInfo);
        IReadOnlyList<Asset> createdAssets = [];
        IReadOnlyList<Asset> proposedAssets = [];
        IReadOnlySet<string>? existingAssetIds = null;
        string assetCreationNote;

        if (mappings.Any(m => m.Status == MappingStatus.AutoLinked))
        {
            // Map the manual's structure onto the matched asset: existing sub-assets are linked as they are,
            // sub-assets the EAM lacks are proposed (or created) under the right existing parent.
            var bestMatch = mappings
                .Where(m => m.Status == MappingStatus.AutoLinked)
                .OrderByDescending(m => m.ConfidenceScore)
                .First();
            var existingRoot = newAssets is { Count: > 1 } && autoCreateAssets
                ? await assetCatalog.GetAssetByIdAsync(bestMatch.AssetId, cancellationToken)
                : null;

            if (existingRoot is null)
            {
                assetCreationNote = "Not needed: linked to an existing asset.";
            }
            else
            {
                var tree = await AssetTreeReconciler.ReconcileAsync(newAssets!, existingRoot, assetCatalog, cancellationToken);
                var linkedSubs = tree.ExistingAssetIds.Count - 1;
                var newOnes = tree.NewAssets.ToList();

                if (newOnes.Count == 0)
                {
                    assetCreationNote =
                        $"Not needed: linked to existing '{existingRoot.Id}'; all {linkedSubs} sub-asset(s) from the manual already exist.";
                }
                else if (reviewNewAssets)
                {
                    proposedAssets = tree.Assets;
                    existingAssetIds = tree.ExistingAssetIds;
                    assetCreationNote =
                        $"Linked to existing '{existingRoot.Id}' and {linkedSubs} existing sub-asset(s); " +
                        $"{newOnes.Count} new sub-asset(s) from the manual proposed, waiting for review.";
                }
                else
                {
                    await assetCatalog.CreateAssetsAsync(newOnes, enrichedManual.ManualId, cancellationToken);
                    createdAssets = newOnes;
                    existingAssetIds = tree.ExistingAssetIds;
                    assetCreationNote =
                        $"Linked to existing '{existingRoot.Id}' and {linkedSubs} existing sub-asset(s); " +
                        $"created {newOnes.Count} new sub-asset(s) from the manual.";
                }
            }
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
        else if (reviewNewAssets)
        {
            // Nothing is created yet: the asset tree is shown for review after all documents are processed.
            proposedAssets = newAssets;
            var root = newAssets[0];
            assetCreationNote =
                $"Proposed new asset '{root.Id}' with {newAssets.Count - 1} sub-asset(s) and " +
                $"{newAssets.Sum(a => a.SpareParts?.Count ?? 0)} spare part(s); waiting for review.";

            mappings =
            [
                new AssetMappingResult(
                    AssetId: root.Id,
                    ManualId: enrichedManual.ManualId,
                    ConfidenceScore: 1.0,
                    Reasoning: $"No existing asset matched; proposed '{root.Name}' from the manual.",
                    Status: MappingStatus.Proposed)
            ];
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
        // Proposed asset IDs are included: they stay the same through review.
        var linkedAssetIds = mappings
            .Where(m => m.Status is MappingStatus.AutoLinked or MappingStatus.Created or MappingStatus.Proposed)
            .Select(m => m.AssetId)
            .Concat(createdAssets.Select(a => a.Id))
            .Concat(proposedAssets.Select(a => a.Id))
            .Concat(existingAssetIds ?? (IEnumerable<string>)[])
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
        progress?.Report(IngestionStage.EmbeddingChunks);
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
        progress?.Report(IngestionStage.StoringInIndex);
        await knowledgeIndex.UpsertChunksAsync(vectorizedChunks, cancellationToken);

        return new ManualProcessingReport(enrichedManual, mappings, vectorizedChunks.Count, assetCreationNote, createdAssets, proposedAssets, existingAssetIds);
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