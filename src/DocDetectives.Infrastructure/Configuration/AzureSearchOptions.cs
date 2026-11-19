public sealed class AzureSearchOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string IndexName { get; set; } = "docdetectives-manuals";
    public int EmbeddingDimensions { get; set; } = 3072;
}