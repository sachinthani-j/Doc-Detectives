using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DocDetectives.Infrastructure.Foundry;

public sealed class AzureAiSearchKnowledgeIndex : IKnowledgeIndex
{
    private readonly SearchClient _searchClient;
    private readonly SearchIndexClient _indexClient;
    private readonly DocDetectives.Infrastructure.Configuration.AzureSearchOptions _cfg;
    private bool _indexVerified;

    public AzureAiSearchKnowledgeIndex(IOptions<DocDetectivesOptions> options)
    {
        _cfg = options.Value.AzureSearch;
        var endpoint = new Uri(_cfg.Endpoint);
        var hasApiKey = !string.IsNullOrWhiteSpace(_cfg.ApiKey)
                        && !_cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var credential = new AzureKeyCredential(_cfg.ApiKey);
            _searchClient = new SearchClient(endpoint, _cfg.IndexName, credential);
            _indexClient = new SearchIndexClient(endpoint, credential);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _searchClient = new SearchClient(endpoint, _cfg.IndexName, credential);
            _indexClient = new SearchIndexClient(endpoint, credential);
        }
    }

    public async Task UpsertChunksAsync(
        IReadOnlyList<ManualChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexExistsAsync(cancellationToken);

        var documents = chunks.Select(c => new SearchDocument
        {
            ["chunkId"] = c.ChunkId,
            ["manualId"] = c.ManualId,
            ["fileName"] = c.FileName,
            ["sectionTitle"] = c.SectionTitle,
            ["content"] = c.Content,
            ["assetIds"] = c.AssetIds.ToArray(),
            ["contentVector"] = c.Embedding.ToArray()
        });

        await _searchClient.MergeOrUploadDocumentsAsync(documents, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<ManualChunk>> SearchByAssetAsync(
        string assetId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var escapedAssetId = assetId.Replace("'", "''");
        var searchOptions = new SearchOptions
        {
            Size = topK,
            Filter = $"assetIds/any(id: id eq '{escapedAssetId}')",
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = topK,
                        Fields = { "contentVector" }
                    }
                }
            }
        };

        var response = await _searchClient.SearchAsync<SearchDocument>(
            queryText,
            searchOptions,
            cancellationToken);

        var results = new List<ManualChunk>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var doc = result.Document;
            results.Add(new ManualChunk(
                ChunkId: doc["chunkId"]?.ToString() ?? string.Empty,
                ManualId: doc["manualId"]?.ToString() ?? string.Empty,
                FileName: doc["fileName"]?.ToString() ?? string.Empty,
                SectionTitle: doc["sectionTitle"]?.ToString() ?? string.Empty,
                Content: doc["content"]?.ToString() ?? string.Empty,
                AssetIds: [assetId],
                Embedding: ReadOnlyMemory<float>.Empty));
        }

        return results;
    }

    private async Task EnsureIndexExistsAsync(CancellationToken cancellationToken)
    {
        if (_indexVerified)
        {
            return;
        }

        const string vectorProfileName = "manual-vector-profile";
        const string hnswConfigName = "manual-hnsw-config";

        var index = new SearchIndex(_cfg.IndexName)
        {
            Fields =
            [
                new SimpleField("chunkId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SimpleField("manualId", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("fileName") { IsFilterable = true },
                new SearchableField("sectionTitle"),
                new SearchableField("content"),
                new SimpleField("assetIds", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true },
                new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = _cfg.EmbeddingDimensions,
                    VectorSearchProfileName = vectorProfileName
                }
            ],
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(vectorProfileName, hnswConfigName) },
                Algorithms = { new HnswAlgorithmConfiguration(hnswConfigName) }
            }
        };

        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
        _indexVerified = true;
    }
}