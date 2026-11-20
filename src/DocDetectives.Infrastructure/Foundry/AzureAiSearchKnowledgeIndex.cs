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
        if (chunks.Count == 0)
        {
            return;
        }

        // Creates or updates the docdetectives-manuals index schema before any chunks are written.
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

        // Writes chunk documents into docdetectives-manuals. chunkId is the key, so a later ingest replaces the same chunks.
        await _searchClient.MergeOrUploadDocumentsAsync(documents, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<ManualChunk>> SearchByAssetAsync(
        string? assetId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default) =>
        HybridSearchAsync(
            string.IsNullOrWhiteSpace(assetId) ? null : AssetFilter(assetId),
            queryEmbedding, queryText, topK, cancellationToken);

    public Task<IReadOnlyList<ManualChunk>> SearchByManualAsync(
        string manualId,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK = 5,
        CancellationToken cancellationToken = default) =>
        HybridSearchAsync(ManualFilter(manualId), queryEmbedding, queryText, topK, cancellationToken);

    public async Task<IReadOnlyList<ManualReference>> GetManualsForAssetAsync(
        string assetId,
        CancellationToken cancellationToken = default) =>
        await CollectManualsAsync(AssetFilter(assetId), includeSections: false, cancellationToken);

    public async Task<IReadOnlyList<ManualReference>> ListManualsAsync(CancellationToken cancellationToken = default) =>
        await CollectManualsAsync(filter: null, includeSections: false, cancellationToken);

    public async Task<ManualReference?> GetManualAsync(string manualId, CancellationToken cancellationToken = default) =>
        (await CollectManualsAsync(ManualFilter(manualId), includeSections: true, cancellationToken)).FirstOrDefault();

    private async Task<IReadOnlyList<ManualChunk>> HybridSearchAsync(
        string? filter,
        ReadOnlyMemory<float> queryEmbedding,
        string queryText,
        int topK,
        CancellationToken cancellationToken)
    {
        var searchOptions = new SearchOptions
        {
            Size = topK,
            Filter = filter,
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
                AssetIds: ReadStrings(doc, "assetIds"),
                Embedding: ReadOnlyMemory<float>.Empty));
        }

        return results;
    }

    /// <summary>
    /// Groups chunks into manuals. The index has one document per chunk and manualId is not facetable,
    /// so this pages through the matching chunks (only small fields are selected).
    /// </summary>
    private async Task<IReadOnlyList<ManualReference>> CollectManualsAsync(
        string? filter,
        bool includeSections,
        CancellationToken cancellationToken)
    {
        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = 1000,
            Select = { "chunkId", "manualId", "fileName", "assetIds" }
        };

        if (includeSections)
        {
            searchOptions.Select.Add("sectionTitle");
        }

        Response<SearchResults<SearchDocument>> response;
        try
        {
            response = await _searchClient.SearchAsync<SearchDocument>("*", searchOptions, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return []; // manuals index not created yet
        }

        var manuals = new Dictionary<string, ManualAccumulator>(StringComparer.Ordinal);
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var doc = result.Document;
            var manualId = doc["manualId"]?.ToString() ?? string.Empty;
            if (!manuals.TryGetValue(manualId, out var manual))
            {
                manual = new ManualAccumulator(doc["fileName"]?.ToString() ?? string.Empty);
                manuals[manualId] = manual;
            }

            manual.Count++;
            foreach (var id in ReadStrings(doc, "assetIds"))
            {
                manual.AssetIds.Add(id);
            }

            if (includeSections && doc["sectionTitle"]?.ToString() is { Length: > 0 } title)
            {
                // Chunk ids end in their position (e.g. "_0003"), so sorting by chunkId keeps the manual's order.
                manual.Sections.Add((doc["chunkId"]?.ToString() ?? string.Empty, title));
            }
        }

        return manuals
            .Select(m => new ManualReference(
                m.Key,
                m.Value.FileName,
                m.Value.Count,
                m.Value.AssetIds.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                includeSections
                    ? m.Value.Sections
                        .OrderBy(s => s.ChunkId, StringComparer.Ordinal)
                        .Select(s => s.Title)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : null))
            .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class ManualAccumulator(string fileName)
    {
        public string FileName { get; } = fileName;
        public int Count { get; set; }
        public HashSet<string> AssetIds { get; } = new(StringComparer.Ordinal);
        public List<(string ChunkId, string Title)> Sections { get; } = [];
    }

    private static IReadOnlyList<string> ReadStrings(SearchDocument doc, string field) =>
        doc.TryGetValue(field, out var value) && value is IEnumerable<object> items
            ? items.Select(i => i?.ToString()).OfType<string>().ToList()
            : [];

    private static string ManualFilter(string manualId) =>
        $"manualId eq '{manualId.Replace("'", "''")}'";

    private static string AssetFilter(string assetId) =>
        $"assetIds/any(id: id eq '{assetId.Replace("'", "''")}')";

    private async Task EnsureIndexExistsAsync(CancellationToken cancellationToken)
    {
        if (_indexVerified)
        {
            return;
        }

        const string vectorProfileName = "manual-vector-profile";
        const string hnswConfigName = "manual-hnsw-config";
        var dimensions = _cfg.EmbeddingDimensions > 0 ? _cfg.EmbeddingDimensions : 3072;

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
                new VectorSearchField("contentVector", dimensions, vectorProfileName)
            ],
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(vectorProfileName, hnswConfigName) },
                Algorithms = { new HnswAlgorithmConfiguration(hnswConfigName) }
            }
        };

        // Creates the docdetectives-manuals index, or updates its schema when the field list above changes.
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
        _indexVerified = true;
    }
}