namespace DocDetectives.Infrastructure.Configuration;

public sealed class DocDetectivesOptions
{
    public const string SectionName = "DocDetectives";

    public string ManualsFolder { get; set; } = "./manuals";
    public double AutoLinkConfidenceThreshold { get; set; } = 0.85;
    public bool AutoCreateAssets { get; set; } = true;

    /// <summary>
    /// Show each new asset (with sub-assets, spare parts and characteristics) for review before creating it.
    /// Turn off for unattended runs: new assets are then created immediately.
    /// </summary>
    public bool ReviewNewAssets { get; set; } = true;

    /// <summary>Draft job plans from every manual and inspection document (one extra AI call per document).</summary>
    public bool GenerateJobPlans { get; set; } = true;

    /// <summary>Also create a job from each inspection document (off: inspection documents only give job plans).</summary>
    public bool CreateInspectionJobs { get; set; } = false;

    /// <summary>Fill asset characteristics from manuals (only empty ones; new characteristics join the master table).</summary>
    public bool ExtractCharacteristics { get; set; } = true;

    /// <summary>
    /// How many documents to ingest at once. Each document still goes through Content Understanding,
    /// classification, extraction, matching, and embedding in that order.
    /// </summary>
    public int IngestionParallelism { get; set; } = 3;
    public ContentUnderstandingOptions FoundryContentUnderstanding { get; set; } = new();
    /// <summary>Which service runs chat and embeddings: "OpenAI" (api.openai.com) or "AzureOpenAI" (Foundry).</summary>
    public string AiProvider { get; set; } = AiProviders.AzureOpenAI;
    public FoundryOpenAIOptions FoundryOpenAI { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public AzureSearchOptions AzureSearch { get; set; } = new();
}

public static class AiProviders
{
    public const string OpenAI = "OpenAI";
    public const string AzureOpenAI = "AzureOpenAI";
}

public sealed class OpenAIOptions
{
    /// <summary>OpenAI API key; when empty the OPENAI_API_KEY environment variable is used.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public string ChatModel { get; set; } = "gpt-4.1";

    /// <summary>Must stay the model the search index was built with, or stored vectors and query vectors won't match.</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";
}

public sealed class ContentUnderstandingOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string AnalyzerId { get; set; } = "prebuilt-documentSearch";
    public string ApiVersion { get; set; } = "2025-11-01";
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Folder of saved Content Understanding responses, keyed by the SHA-256 of the file bytes
    /// (the same hash the manual id is cut from). Empty disables the cache.
    /// </summary>
    public string CacheFolder { get; set; } = "./.cache/content-understanding";
}

public sealed class FoundryOpenAIOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = "gpt-6.2-astra";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-large";
}

public sealed class AzureSearchOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string IndexName { get; set; } = "docdetectives-manuals";
    public string AssetsIndexName { get; set; } = "assets";
    public string JobsIndexName { get; set; } = "docdetectives-jobs";
    public string PmModelsIndexName { get; set; } = "docdetectives-pmmodels";
    public string CharacteristicsIndexName { get; set; } = "docdetectives-characteristics";
    public string AssetKeyField { get; set; } = "key";
    public string AssetIdField { get; set; } = "assetId";
    public string LinkedManualIdsField { get; set; } = "linkedManualIds";
    public string ParentAssetIdField { get; set; } = "parentAssetId";
    public string SparePartsField { get; set; } = "spareParts";
    public string CharacteristicsField { get; set; } = "characteristics";
    public string ManufactureYearField { get; set; } = "manufactureYear";
    public string ManufacturerNameField { get; set; } = "manufacturerName";
    public int EmbeddingDimensions { get; set; } = 3072;
}