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
using System.Text;

namespace DocDetectives.Infrastructure.Foundry;

public sealed class AzureAiSearchAssetCatalogGateway : IAssetCatalogGateway
{
    private readonly SearchClient _assetSearchClient;
    private readonly SearchIndexClient _indexClient;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _extraFieldsReady;
    private readonly DocDetectives.Infrastructure.Configuration.AzureSearchOptions _cfg;

    public AzureAiSearchAssetCatalogGateway(IOptions<DocDetectivesOptions> options)
    {
        _cfg = options.Value.AzureSearch;
        var endpoint = new Uri(_cfg.Endpoint);
        var hasApiKey = !string.IsNullOrWhiteSpace(_cfg.ApiKey)
                        && !_cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var keyCredential = new AzureKeyCredential(_cfg.ApiKey);
            _assetSearchClient = new SearchClient(endpoint, _cfg.AssetsIndexName, keyCredential);
            _indexClient = new SearchIndexClient(endpoint, keyCredential);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _assetSearchClient = new SearchClient(endpoint, _cfg.AssetsIndexName, credential);
            _indexClient = new SearchIndexClient(endpoint, credential);
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
        var doc = await FindDocumentByAssetIdAsync(assetId, cancellationToken);
        return doc is null ? null : MapDocumentToAsset(doc);
    }

    public async Task<Asset?> FindAssetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default)
    {
        var doc = await FindDocumentAsync("serialNumber", serialNumber, cancellationToken);
        return doc is null ? null : MapDocumentToAsset(doc);
    }

    public async Task<IReadOnlyList<Asset>> GetChildAssetsAsync(
        string parentAssetId,
        CancellationToken cancellationToken = default)
    {
        var options = new SearchOptions
        {
            Filter = $"{_cfg.ParentAssetIdField} eq {SearchFilter.Create($"{parentAssetId}")}",
            Size = 200
        };

        var response = await _assetSearchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
        var children = new List<Asset>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            children.Add(MapDocumentToAsset(result.Document));
        }

        return children;
    }

    public async Task CreateAssetsAsync(
        IReadOnlyList<Asset> assets,
        string manualId,
        CancellationToken cancellationToken = default)
    {
        // Held for the whole write so a parallel ingest cannot interleave another asset update.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureExtraFieldsAsync(cancellationToken);

            // The manual link is written in the same upload: a lookup straight after an upload may not see the new documents yet.
            // Every field written here must exist in the assets index schema, or the upload is rejected.
            var docs = assets.Select(asset => new SearchDocument
            {
                [_cfg.AssetKeyField] = ToDocumentKey(asset.Id),
                [_cfg.AssetIdField] = asset.Id,
                ["name"] = asset.Name,
                ["manufacturer"] = asset.Manufacturer,
                ["modelNumber"] = asset.ModelNumber,
                ["serialNumber"] = asset.SerialNumber,
                ["category"] = asset.Category,
                [_cfg.ParentAssetIdField] = asset.ParentAssetId,
                [_cfg.SparePartsField] = (asset.SpareParts ?? []).ToArray(),
            [_cfg.ManufactureYearField] = asset.ManufactureYear,
            [_cfg.ManufacturerNameField] = asset.ManufacturerName,
            [_cfg.CharacteristicsField] = ToCharacteristicDocuments(asset.Characteristics ?? []),
                [_cfg.LinkedManualIdsField] = new[] { manualId }
            });

            // One batch for the whole tree; MergeOrUpload keeps re-ingestion idempotent.
            await _assetSearchClient.MergeOrUploadDocumentsAsync(docs, cancellationToken: cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // Adds the spare-parts (bill of materials) and characteristics fields to the assets index once, if missing.
    // Adding fields is non-breaking in Azure AI Search: existing documents simply have no value for them.
    private async Task EnsureExtraFieldsAsync(CancellationToken cancellationToken)
    {
        if (_extraFieldsReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_extraFieldsReady)
            {
                return;
            }

            SearchIndex index = await _indexClient.GetIndexAsync(_cfg.AssetsIndexName, cancellationToken);
            var changed = false;

            if (!index.Fields.Any(f => f.Name == _cfg.SparePartsField))
            {
                index.Fields.Add(new SearchableField(_cfg.SparePartsField, collection: true) { IsFilterable = true });
                changed = true;
            }

            if (!index.Fields.Any(f => f.Name == _cfg.ManufacturerNameField))
            {
                index.Fields.Add(new SearchableField(_cfg.ManufacturerNameField) { IsFilterable = true, IsFacetable = true });
                changed = true;
            }

            if (!index.Fields.Any(f => f.Name == _cfg.ManufactureYearField))
            {
                index.Fields.Add(new SimpleField(_cfg.ManufactureYearField, SearchFieldDataType.Int32) { IsFilterable = true });
                changed = true;
            }

            if (!index.Fields.Any(f => f.Name == _cfg.CharacteristicsField))
            {
                index.Fields.Add(new ComplexField(_cfg.CharacteristicsField, collection: true)
                {
                    Fields =
                    {
                        new SimpleField("characteristicId", SearchFieldDataType.String) { IsFilterable = true },
                        new SearchableField("name") { IsFilterable = true },
                        new SimpleField("valueType", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("numericValue", SearchFieldDataType.Double) { IsFilterable = true },
                        new SimpleField("unit", SearchFieldDataType.String),
                        new SimpleField("unitSymbol", SearchFieldDataType.String),
                        new SimpleField("booleanValue", SearchFieldDataType.Boolean) { IsFilterable = true },
                        new SearchableField("textValue") { IsFilterable = true },
                        new SimpleField("dateValue", SearchFieldDataType.String),
                        new SimpleField("source", SearchFieldDataType.String),
                        new SimpleField("extractedUtc", SearchFieldDataType.DateTimeOffset)
                    }
                });
                changed = true;
            }

            if (changed)
            {
                await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
            }

            _extraFieldsReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    public async Task UpdateCharacteristicsAsync(
        string assetId,
        IReadOnlyList<AssetCharacteristic> characteristics,
        CancellationToken cancellationToken = default)
    {
        await EnsureExtraFieldsAsync(cancellationToken);

        var existingDoc = await FindDocumentByAssetIdAsync(assetId, cancellationToken);
        if (existingDoc is null)
        {
            return;
        }

        // A complex collection is replaced as a whole, so the caller passes the full merged list.
        var patchDoc = new SearchDocument
        {
            [_cfg.AssetKeyField] = existingDoc[_cfg.AssetKeyField],
            [_cfg.CharacteristicsField] = ToCharacteristicDocuments(characteristics)
        };

        await _assetSearchClient.MergeDocumentsAsync([patchDoc], cancellationToken: cancellationToken);
    }

    public async Task UpdateManufacturerDetailsAsync(
        string assetId,
        string? manufacturerName,
        int? manufactureYear,
        CancellationToken cancellationToken = default)
    {
        if (manufacturerName is null && manufactureYear is null)
        {
            return;
        }

        await EnsureExtraFieldsAsync(cancellationToken);

        var existingDoc = await FindDocumentByAssetIdAsync(assetId, cancellationToken);
        if (existingDoc is null)
        {
            return;
        }

        // Merge writes only the fields present, so the one not being set keeps its value.
        var patchDoc = new SearchDocument { [_cfg.AssetKeyField] = existingDoc[_cfg.AssetKeyField] };
        if (manufacturerName is not null)
        {
            patchDoc[_cfg.ManufacturerNameField] = manufacturerName;
        }

        if (manufactureYear is not null)
        {
            patchDoc[_cfg.ManufactureYearField] = manufactureYear;
        }

        await _assetSearchClient.MergeDocumentsAsync([patchDoc], cancellationToken: cancellationToken);
    }

    public async Task LinkManualToAssetAsync(
        string assetId,
        string manualId,
        double confidenceScore,
        MappingStatus status,
        CancellationToken cancellationToken = default)
    {
        // Read-modify-write of the linked-manual list. One writer at a time, or a parallel ingest can drop an id.
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var existingDoc = await FindDocumentByAssetIdAsync(assetId, cancellationToken);
            if (existingDoc is null)
            {
                return;
            }

            var manualIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingDoc.TryGetValue(_cfg.LinkedManualIdsField, out var rawIds) && rawIds is IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    if (item?.ToString() is { Length: > 0 } id)
                    {
                        manualIds.Add(id);
                    }
                }
            }

            if (!manualIds.Add(manualId))
            {
                return; // already linked
            }

            var patchDoc = new SearchDocument
            {
                [_cfg.AssetKeyField] = existingDoc[_cfg.AssetKeyField],
                [_cfg.LinkedManualIdsField] = manualIds.ToArray()
            };

            await _assetSearchClient.MergeDocumentsAsync([patchDoc], cancellationToken: cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // The index key is not the asset ID itself (it is an encoded form of it), so look documents up by the assetId field.
    private Task<SearchDocument?> FindDocumentByAssetIdAsync(string assetId, CancellationToken cancellationToken) =>
        FindDocumentAsync(_cfg.AssetIdField, assetId, cancellationToken);

    // Exact match on a filterable field; returns the first document found.
    private async Task<SearchDocument?> FindDocumentAsync(string field, string value, CancellationToken cancellationToken)
    {
        var options = new SearchOptions
        {
            // SearchFilter.Create quotes and escapes the value; the field name must stay unquoted.
            Filter = $"{field} eq {SearchFilter.Create($"{value}")}",
            Size = 1
        };

        var response = await _assetSearchClient.SearchAsync<SearchDocument>("*", options, cancellationToken);
        await foreach (var result in response.Value.GetResultsAsync())
        {
            return result.Document;
        }

        return null;
    }

    // Same key format as the existing documents: URL-safe Base64 of the asset ID without '=' padding ("37911" -> "Mzc5MTE").
    private static string ToDocumentKey(string assetId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(assetId))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
            Id: ReadField(_cfg.AssetIdField, "assetId", "id"),
            Name: ReadField("name", "assetName", "description"),
            Manufacturer: ReadField("manufacturer", "brand", "oem"),
            ModelNumber: ReadField("modelNumber", "model", "partNumber"),
            SerialNumber: ReadField("serialNumber", "serial"),
            Category: ReadField("category", "assetType", "equipmentCategory"),
            ParentAssetId: ReadField(_cfg.ParentAssetIdField) is { Length: > 0 } parentId ? parentId : null,
            SpareParts: doc.TryGetValue(_cfg.SparePartsField, out var rawParts) && rawParts is IEnumerable<object> parts
                ? parts.Select(p => p?.ToString()).OfType<string>().ToList()
                : [],
            Characteristics: doc.TryGetValue(_cfg.CharacteristicsField, out var rawChars) && rawChars is IEnumerable<object> chars
                ? chars.OfType<SearchDocument>().Select(MapCharacteristic).ToList()
                : [],
            // The export uses 0 for "unknown year".
            ManufactureYear: int.TryParse(ReadField(_cfg.ManufactureYearField), out var year) && year > 0 ? year : null,
            ManufacturerName: ReadField(_cfg.ManufacturerNameField) is { Length: > 0 } makerName ? makerName : null);
    }

    private static SearchDocument[] ToCharacteristicDocuments(IReadOnlyList<AssetCharacteristic> characteristics) =>
        characteristics.Select(c => new SearchDocument
        {
            ["characteristicId"] = c.CharacteristicId,
            ["name"] = c.Name,
            ["valueType"] = c.ValueType,
            ["numericValue"] = c.NumericValue,
            ["unit"] = c.Unit,
            ["unitSymbol"] = c.UnitSymbol,
            ["booleanValue"] = c.BooleanValue,
            ["textValue"] = c.TextValue,
            ["dateValue"] = c.DateValue,
            ["source"] = c.Source,
            ["extractedUtc"] = c.ExtractedUtc
        }).ToArray();

    private static AssetCharacteristic MapCharacteristic(SearchDocument c)
    {
        string? Text(string field) => c.TryGetValue(field, out var v) && v is not null ? v.ToString() : null;

        return new AssetCharacteristic(
            CharacteristicId: Text("characteristicId") ?? string.Empty,
            Name: Text("name") ?? string.Empty,
            ValueType: Text("valueType") ?? CharacteristicValueType.Alphanumeric,
            NumericValue: c.TryGetValue("numericValue", out var n) && n is not null ? Convert.ToDouble(n) : null,
            Unit: Text("unit"),
            UnitSymbol: Text("unitSymbol"),
            BooleanValue: c.TryGetValue("booleanValue", out var b) && b is bool flag ? flag : null,
            TextValue: Text("textValue"),
            DateValue: Text("dateValue"),
            Source: Text("source"),
            ExtractedUtc: c.TryGetValue("extractedUtc", out var d) && d is DateTimeOffset when ? when : DateTimeOffset.MinValue);
    }
}