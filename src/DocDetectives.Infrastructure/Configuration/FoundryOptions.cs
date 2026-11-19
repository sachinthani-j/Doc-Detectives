namespace DocDetectives.Infrastructure.Configuration;

public sealed class DocDetectivesOptions
{
    public const string SectionName = "DocDetectives";

    public string ManualsFolder { get; set; } = "./manuals";
    public double AutoLinkConfidenceThreshold { get; set; } = 0.85;
    public bool AutoCreateAssets { get; set; } = true;
    public ContentUnderstandingOptions FoundryContentUnderstanding { get; set; } = new();
    public FoundryOpenAIOptions FoundryOpenAI { get; set; } = new();
    public AzureSearchOptions AzureSearch { get; set; } = new();
}

public sealed class ContentUnderstandingOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string AnalyzerId { get; set; } = "prebuilt-documentSearch";
    public string ApiVersion { get; set; } = "2025-11-01";
    public int PollIntervalSeconds { get; set; } = 2;
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
    public string AssetKeyField { get; set; } = "key";
    public string AssetIdField { get; set; } = "assetId";
    public string LinkedManualIdsField { get; set; } = "linkedManualIds";
    public int EmbeddingDimensions { get; set; } = 3072;
}