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
/// Stores approved job plans in an Azure AI Search index (drafts are never stored).
/// Swap for an EAM import (Excel template or REST API) later behind the same interface.
/// </summary>
public sealed class AzureAiSearchJobPlanGateway : IJobPlanGateway
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _plansClient;
    private readonly string _indexName;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _indexReady;

    public AzureAiSearchJobPlanGateway(IOptions<DocDetectivesOptions> options)
    {
        var cfg = options.Value.AzureSearch;
        var endpoint = new Uri(cfg.Endpoint);
        _indexName = cfg.JobPlansIndexName;

        var hasApiKey = !string.IsNullOrWhiteSpace(cfg.ApiKey)
                        && !cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var keyCredential = new AzureKeyCredential(cfg.ApiKey);
            _indexClient = new SearchIndexClient(endpoint, keyCredential);
            _plansClient = new SearchClient(endpoint, _indexName, keyCredential);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(endpoint, credential);
            _plansClient = new SearchClient(endpoint, _indexName, credential);
        }
    }

    public async Task SaveAsync(IReadOnlyList<JobPlan> plans, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        // Upload replaces the whole plan, so an edited or revised plan never keeps stale lines.
        await _plansClient.UploadDocumentsAsync(plans.Select(ToDocument), cancellationToken: cancellationToken);
    }

    public async Task<string?> GetStatusAsync(string jobPlanId, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        try
        {
            var existing = await _plansClient.GetDocumentAsync<SearchDocument>(
                jobPlanId,
                new GetDocumentOptions { SelectedFields = { "status" } },
                cancellationToken);

            return existing.Value.TryGetValue("status", out var status) ? status?.ToString() : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static SearchDocument ToDocument(JobPlan plan) => new()
    {
        ["id"] = plan.Id,
        ["title"] = plan.Title,
        ["status"] = plan.Status,
        ["sourceType"] = plan.SourceType.ToString(),
        ["sourceDocumentId"] = plan.SourceDocumentId,
        ["sourceFileName"] = plan.SourceFileName,
        ["documentCode"] = plan.DocumentCode,
        ["assetId"] = plan.AssetId,
        ["manufacturer"] = plan.Manufacturer,
        ["modelNumbers"] = plan.ModelNumbers.ToArray(),
        ["interval"] = plan.Interval,
        ["intervalValue"] = plan.IntervalValue,
        ["intervalUnit"] = plan.IntervalUnit,
        ["materials"] = plan.Materials.ToArray(),
        ["safetyNotes"] = plan.SafetyNotes.ToArray(),
        ["createdUtc"] = plan.CreatedUtc,
        ["lines"] = plan.Lines.Select(line => new SearchDocument
        {
            ["lineNumber"] = line.LineNumber,
            ["title"] = line.Title,
            ["lineType"] = line.LineType,
            ["instruction"] = line.Instruction,
            ["equipmentCode"] = line.EquipmentCode,
            ["component"] = line.Component,
            ["assetId"] = line.AssetId,
            ["measurementPoint"] = line.MeasurementPoint,
            ["nominalValue"] = line.NominalValue,
            ["measurementCode"] = line.MeasurementCode,
            ["allowedResults"] = line.AllowedResults.ToArray()
        }).ToArray()
    };

    // Creates the job plans index on first use if it does not exist yet.
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
                        new SearchableField("title"),
                        new SimpleField("status", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("sourceType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("sourceDocumentId", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("sourceFileName", SearchFieldDataType.String),
                        new SearchableField("documentCode") { IsFilterable = true },
                        new SimpleField("assetId", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SearchableField("manufacturer") { IsFilterable = true },
                        new SearchableField("modelNumbers", collection: true) { IsFilterable = true },
                        new SimpleField("interval", SearchFieldDataType.String),
                        new SimpleField("intervalValue", SearchFieldDataType.Int32) { IsFilterable = true },
                        new SimpleField("intervalUnit", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SearchableField("materials", collection: true),
                        new SearchableField("safetyNotes", collection: true),
                        new SimpleField("createdUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                        new ComplexField("lines", collection: true)
                        {
                            Fields =
                            {
                                new SimpleField("lineNumber", SearchFieldDataType.Int32),
                                new SearchableField("title"),
                                new SimpleField("lineType", SearchFieldDataType.String) { IsFilterable = true },
                                new SearchableField("instruction"),
                                new SearchableField("equipmentCode") { IsFilterable = true },
                                new SearchableField("component") { IsFilterable = true },
                                new SimpleField("assetId", SearchFieldDataType.String) { IsFilterable = true },
                                new SimpleField("measurementPoint", SearchFieldDataType.String),
                                new SimpleField("nominalValue", SearchFieldDataType.String),
                                new SimpleField("measurementCode", SearchFieldDataType.String),
                                new SimpleField("allowedResults", SearchFieldDataType.Collection(SearchFieldDataType.String))
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
}
