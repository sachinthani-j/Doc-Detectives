using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Serialization;
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
/// Stores PM models (with their PM jobs and job lines) in an Azure AI Search index, in this app only.
/// </summary>
public sealed class AzureAiSearchPmModelGateway : IPmModelGateway
{
    private const int MaxModelsPerAsset = 200;

    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _modelsClient;
    private readonly string _indexName;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _indexReady;

    public AzureAiSearchPmModelGateway(IOptions<DocDetectivesOptions> options)
    {
        var cfg = options.Value.AzureSearch;
        var endpoint = new Uri(cfg.Endpoint);
        _indexName = cfg.PmModelsIndexName;

        // camelCase field names, matching the index below.
        var clientOptions = new SearchClientOptions
        {
            Serializer = new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var hasApiKey = !string.IsNullOrWhiteSpace(cfg.ApiKey)
                        && !cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var keyCredential = new AzureKeyCredential(cfg.ApiKey);
            _indexClient = new SearchIndexClient(endpoint, keyCredential);
            _modelsClient = new SearchClient(endpoint, _indexName, keyCredential, clientOptions);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(endpoint, credential);
            _modelsClient = new SearchClient(endpoint, _indexName, credential, clientOptions);
        }
    }

    public async Task SaveAsync(PmModel model, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        // Upload replaces the whole model, so a revised plan never keeps stale jobs or lines.
        await _modelsClient.UploadDocumentsAsync([PmModelDocument.From(model)], cancellationToken: cancellationToken);
    }

    public async Task<bool> ExistsAsync(string pmModelId, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        try
        {
            await _modelsClient.GetDocumentAsync<SearchDocument>(
                pmModelId, new GetDocumentOptions { SelectedFields = { "id" } }, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<PmModel>> GetForAssetAsync(string assetId, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        var id = SearchFilter.Create($"{assetId}");
        var options = new SearchOptions
        {
            Filter = $"assetId eq {id} or jobs/any(j: j/assetId eq {id})",
            Size = MaxModelsPerAsset
        };
        options.OrderBy.Add("createdUtc desc");

        var response = await _modelsClient.SearchAsync<PmModelDocument>("*", options, cancellationToken);
        var models = new List<PmModel>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            models.Add(result.Document.ToModel());
        }

        return models;
    }

    // Creates the PM models index on first use if it does not exist yet.
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
                var index = new SearchIndex(_indexName)
                {
                    Fields =
                    {
                        new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                        new SimpleField("assetId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SearchableField("description"),
                        new SimpleField("frequencyNumber", SearchFieldDataType.Int32) { IsFilterable = true },
                        new SimpleField("frequencyUnit", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("frequency", SearchFieldDataType.String),
                        new SimpleField("frequencyStartDate", SearchFieldDataType.String) { IsFilterable = true, IsSortable = true },
                        new SimpleField("sourceDocumentId", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("sourceFileName", SearchFieldDataType.String),
                        new SimpleField("createdUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                        new ComplexField("jobs", collection: true)
                        {
                            Fields =
                            {
                                new SimpleField("jobCode", SearchFieldDataType.String),
                                new SimpleField("assetId", SearchFieldDataType.String) { IsFilterable = true },
                                new SearchableField("component"),
                                new SearchableField("description"),
                                new SimpleField("jobType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                                new ComplexField("lines", collection: true)
                                {
                                    Fields =
                                    {
                                        new SimpleField("lineNumber", SearchFieldDataType.Int32),
                                        new SearchableField("title"),
                                        new SimpleField("lineType", SearchFieldDataType.String) { IsFilterable = true },
                                        new SearchableField("instruction"),
                                        new SimpleField("equipmentCode", SearchFieldDataType.String),
                                        new SimpleField("component", SearchFieldDataType.String),
                                        new SimpleField("assetId", SearchFieldDataType.String),
                                        new SimpleField("measurementPoint", SearchFieldDataType.String),
                                        new SimpleField("nominalValue", SearchFieldDataType.String),
                                        new SimpleField("measurementCode", SearchFieldDataType.String),
                                        new SimpleField("allowedResults", SearchFieldDataType.Collection(SearchFieldDataType.String))
                                    }
                                }
                            }
                        }
                    }
                };

                await _indexClient.CreateIndexAsync(index, cancellationToken);
            }

            _indexReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    // Index documents (camelCase via the serializer); mutable so they deserialize without a constructor.
    private sealed class PmModelDocument
    {
        public string Id { get; set; } = "";
        public string AssetId { get; set; } = "";
        public string Description { get; set; } = "";
        public int? FrequencyNumber { get; set; }
        public string? FrequencyUnit { get; set; }
        public string? Frequency { get; set; }
        public string? FrequencyStartDate { get; set; }
        public string SourceDocumentId { get; set; } = "";
        public string SourceFileName { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public List<PmJobDocument> Jobs { get; set; } = [];

        public static PmModelDocument From(PmModel m) => new()
        {
            Id = m.Id,
            AssetId = m.AssetId,
            Description = m.Description,
            FrequencyNumber = m.FrequencyNumber,
            FrequencyUnit = m.FrequencyUnit,
            Frequency = m.Frequency,
            FrequencyStartDate = m.FrequencyStartDate,
            SourceDocumentId = m.SourceDocumentId,
            SourceFileName = m.SourceFileName,
            CreatedUtc = m.CreatedUtc,
            Jobs = m.Jobs.Select(j => new PmJobDocument
            {
                JobCode = j.JobCode,
                AssetId = j.AssetId,
                Component = j.Component,
                Description = j.Description,
                JobType = j.JobType,
                Lines = j.Lines.Select(l => new PmLineDocument
                {
                    LineNumber = l.LineNumber,
                    Title = l.Title,
                    LineType = l.LineType,
                    Instruction = l.Instruction,
                    EquipmentCode = l.EquipmentCode,
                    Component = l.Component,
                    AssetId = l.AssetId,
                    MeasurementPoint = l.MeasurementPoint,
                    NominalValue = l.NominalValue,
                    MeasurementCode = l.MeasurementCode,
                    AllowedResults = l.AllowedResults.ToList()
                }).ToList()
            }).ToList()
        };

        public PmModel ToModel() => new(
            Id, AssetId, Description, FrequencyNumber, FrequencyUnit, Frequency, FrequencyStartDate,
            SourceDocumentId, SourceFileName, CreatedUtc,
            (Jobs ?? []).Select(j => new PmJob(
                j.JobCode, j.AssetId, j.Component, j.Description, j.JobType,
                (j.Lines ?? []).Select(l => new JobPlanLine(
                    l.LineNumber, l.Title, l.LineType, l.Instruction, l.EquipmentCode, l.Component, l.AssetId,
                    l.MeasurementPoint, l.NominalValue, l.MeasurementCode, l.AllowedResults ?? [])).ToList())).ToList());
    }

    private sealed class PmJobDocument
    {
        public string JobCode { get; set; } = "";
        public string AssetId { get; set; } = "";
        public string? Component { get; set; }
        public string Description { get; set; } = "";
        public string JobType { get; set; } = "";
        public List<PmLineDocument> Lines { get; set; } = [];
    }

    private sealed class PmLineDocument
    {
        public int LineNumber { get; set; }
        public string Title { get; set; } = "";
        public string LineType { get; set; } = "";
        public string? Instruction { get; set; }
        public string? EquipmentCode { get; set; }
        public string? Component { get; set; }
        public string? AssetId { get; set; }
        public string? MeasurementPoint { get; set; }
        public string? NominalValue { get; set; }
        public string? MeasurementCode { get; set; }
        public List<string> AllowedResults { get; set; } = [];
    }
}
