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

/// <summary>
/// The characteristic master table, stored in its own Azure AI Search index (created on first use).
/// </summary>
public sealed class AzureAiSearchCharacteristicCatalog : ICharacteristicCatalog
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _client;
    private readonly string _indexName;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _indexReady;

    public AzureAiSearchCharacteristicCatalog(IOptions<DocDetectivesOptions> options)
    {
        var cfg = options.Value.AzureSearch;
        var endpoint = new Uri(cfg.Endpoint);
        _indexName = cfg.CharacteristicsIndexName;

        var hasApiKey = !string.IsNullOrWhiteSpace(cfg.ApiKey)
                        && !cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var keyCredential = new AzureKeyCredential(cfg.ApiKey);
            _indexClient = new SearchIndexClient(endpoint, keyCredential);
            _client = new SearchClient(endpoint, _indexName, keyCredential);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(endpoint, credential);
            _client = new SearchClient(endpoint, _indexName, credential);
        }
    }

    public async Task<IReadOnlyList<CharacteristicDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        var definitions = new List<CharacteristicDefinition>();
        // No Size: the SDK then pages through every result, so the whole table is read however large it grows.
        var response = await _client.SearchAsync<SearchDocument>("*", new SearchOptions(), cancellationToken);

        await foreach (var result in response.Value.GetResultsAsync())
        {
            var d = result.Document;
            definitions.Add(new CharacteristicDefinition(
                Id: Text(d, "id") ?? string.Empty,
                Name: Text(d, "name") ?? string.Empty,
                ValueType: Text(d, "valueType") ?? CharacteristicValueType.Alphanumeric,
                Unit: Text(d, "unit"),
                UnitSymbol: Text(d, "unitSymbol"),
                Aliases: List(d, "aliases"),
                Options: List(d, "options"),
                EquipmentTypes: List(d, "equipmentTypes"),
                CreatedUtc: d.TryGetValue("createdUtc", out var c) && c is DateTimeOffset when ? when : DateTimeOffset.MinValue));
        }

        return definitions;
    }

    public async Task SaveAsync(IReadOnlyList<CharacteristicDefinition> definitions, CancellationToken cancellationToken = default)
    {
        if (definitions.Count == 0)
        {
            return;
        }

        await EnsureIndexAsync(cancellationToken);

        var docs = definitions.Select(d => new SearchDocument
        {
            ["id"] = d.Id,
            ["name"] = d.Name,
            ["valueType"] = d.ValueType,
            ["unit"] = d.Unit,
            ["unitSymbol"] = d.UnitSymbol,
            ["aliases"] = d.Aliases.ToArray(),
            ["options"] = d.Options.ToArray(),
            ["equipmentTypes"] = d.EquipmentTypes.ToArray(),
            ["createdUtc"] = d.CreatedUtc
        });

        await _client.MergeOrUploadDocumentsAsync(docs, cancellationToken: cancellationToken);
    }

    private static string? Text(SearchDocument d, string field) =>
        d.TryGetValue(field, out var v) && v is not null ? v.ToString() : null;

    private static IReadOnlyList<string> List(SearchDocument d, string field) =>
        d.TryGetValue(field, out var v) && v is IEnumerable<object> items
            ? items.Select(i => i?.ToString()).OfType<string>().ToList()
            : [];

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_indexReady)
            {
                return;
            }

            try
            {
                await _indexClient.GetIndexAsync(_indexName, cancellationToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                await _indexClient.CreateIndexAsync(new SearchIndex(_indexName)
                {
                    Fields =
                    {
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                        new SearchableField("name") { IsFilterable = true, IsSortable = true },
                        new SimpleField("valueType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("unit", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("unitSymbol", SearchFieldDataType.String),
                        new SearchableField("aliases", collection: true) { IsFilterable = true },
                        new SearchableField("options", collection: true),
                        new SimpleField("equipmentTypes", SearchFieldDataType.Collection(SearchFieldDataType.String)) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("createdUtc", SearchFieldDataType.DateTimeOffset) { IsSortable = true }
                    }
                }, cancellationToken);
            }

            _indexReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
