using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace DocDetectives.Infrastructure.Foundry;

public sealed class AzureAiSearchAssetCatalogGateway : IAssetCatalogGateway
{
    private readonly SearchClient _assetSearchClient;
    private readonly DocDetectives.Infrastructure.Configuration.AzureSearchOptions _cfg;

    public AzureAiSearchAssetCatalogGateway(IOptions<DocDetectivesOptions> options)
    {
        _cfg = options.Value.AzureSearch;
        var endpoint = new Uri(_cfg.Endpoint);
        var hasApiKey = !string.IsNullOrWhiteSpace(_cfg.ApiKey)
                        && !_cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            _assetSearchClient = new SearchClient(endpoint, _cfg.AssetsIndexName, new AzureKeyCredential(_cfg.ApiKey));
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _assetSearchClient = new SearchClient(endpoint, _cfg.AssetsIndexName, credential);
        }
    }

    public async Task<IReadOnlyList<Asset>> SearchAssetsAsync(
        string? manufacturer,
        string? modelKeyword,
        CancellationToken cancellationToken = default)
    {
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            terms.Add(manufacturer.Trim());
        }

        if (!string.IsNullOrWhiteSpace(modelKeyword))
        {
            terms.Add(modelKeyword.Trim());
        }

        var searchText = terms.Count > 0 ? string.Join(" ", terms) : "*";
        var searchOptions = new SearchOptions
        {
            Size = 15,
            QueryType = SearchQueryType.Simple
        };

        var response = await _assetSearchClient.SearchAsync<SearchDocument>(
            searchText,
            searchOptions,
            cancellationToken);

        var assets = new List<Asset>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var doc = result.Document;
            assets.Add(MapDocumentToAsset(doc));
        }

        return assets;
    }

    public async Task<Asset?> GetAssetByIdAsync(string assetId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _assetSearchClient.GetDocumentAsync<SearchDocument>(
                assetId,
                cancellationToken: cancellationToken);

            return MapDocumentToAsset(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default)
    {
        var existingManualIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var existingDoc = await _assetSearchClient.GetDocumentAsync<SearchDocument>(
                assetId,
                cancellationToken: cancellationToken);

            if (existingDoc.Value.TryGetValue("manualIds", out var rawIds) && rawIds is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is { Length: > 0 } id)
                    {
                        existingManualIds.Add(id);
                    }
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return;
        }

        existingManualIds.Add(manualId);

        var patchDoc = new SearchDocument
        {
            [_cfg.AssetKeyField] = assetId,
            ["manualIds"] = existingManualIds.ToArray()
        };

        await _assetSearchClient.MergeDocumentsAsync([patchDoc], cancellationToken: cancellationToken);
    }

    private Asset MapDocumentToAsset(SearchDocument doc)
    {
        string ReadField(params string[] candidateNames)
        {
            foreach (var name in candidateNames)
            {
                if (doc.TryGetValue(name, out var val) && val is not null)
                {
                    var text = val.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            return string.Empty;
        }

        return new Asset(
            Id: ReadField(_cfg.AssetKeyField, "id", "assetId"),
            Name: ReadField("name", "assetName", "description"),
            Manufacturer: ReadField("manufacturer", "brand", "oem"),
            ModelNumber: ReadField("modelNumber", "model", "partNumber"),
            SerialNumber: ReadField("serialNumber", "serial"),
            Category: ReadField("category", "assetType", "equipmentCategory"));
    }
}