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
/// Stores jobs in an Azure AI Search index. Swap for an Ultimo/EAM REST gateway later behind the same interface.
/// </summary>
public sealed class AzureAiSearchJobGateway : IJobGateway
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _jobsClient;
    private readonly string _indexName;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _indexReady;

    public AzureAiSearchJobGateway(IOptions<DocDetectivesOptions> options)
    {
        var cfg = options.Value.AzureSearch;
        var endpoint = new Uri(cfg.Endpoint);
        _indexName = cfg.JobsIndexName;

        var hasApiKey = !string.IsNullOrWhiteSpace(cfg.ApiKey)
                        && !cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

        if (hasApiKey)
        {
            var keyCredential = new AzureKeyCredential(cfg.ApiKey);
            _indexClient = new SearchIndexClient(endpoint, keyCredential);
            _jobsClient = new SearchClient(endpoint, _indexName, keyCredential);
        }
        else
        {
            TokenCredential credential = new DefaultAzureCredential();
            _indexClient = new SearchIndexClient(endpoint, credential);
            _jobsClient = new SearchClient(endpoint, _indexName, credential);
        }
    }

    public async Task CreateJobAsync(Job job, CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);

        var doc = new SearchDocument
        {
            ["id"] = job.Id,
            ["assetId"] = job.AssetId,
            ["title"] = job.Title,
            ["jobType"] = job.JobType,
            ["status"] = job.Status,
            ["sourceDocumentId"] = job.SourceDocumentId,
            ["sourceFileName"] = job.SourceFileName,
            ["inspectionDate"] = job.InspectionDate,
            ["inspector"] = job.Inspector,
            ["createdUtc"] = job.CreatedUtc,
            ["lines"] = job.Lines.Select(line => new SearchDocument
            {
                ["lineNumber"] = line.LineNumber,
                ["assetId"] = line.AssetId,
                ["component"] = line.Component,
                ["description"] = line.Description,
                ["criteria"] = line.Criteria,
                ["result"] = line.Result,
                ["measurement"] = line.Measurement,
                ["remarks"] = line.Remarks
            }).ToArray()
        };

        // Upload replaces the whole job, so re-ingesting a changed document never leaves stale lines behind.
        await _jobsClient.UploadDocumentsAsync([doc], cancellationToken: cancellationToken);
    }

    // Creates the jobs index on first use if it does not exist yet.
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
                        new SearchableField("title"),
                        new SimpleField("jobType", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("status", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("sourceDocumentId", SearchFieldDataType.String) { IsFilterable = true },
                        new SimpleField("sourceFileName", SearchFieldDataType.String),
                        new SimpleField("inspectionDate", SearchFieldDataType.String) { IsFilterable = true },
                        new SearchableField("inspector") { IsFilterable = true },
                        new SimpleField("createdUtc", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                        new ComplexField("lines", collection: true)
                        {
                            Fields =
                            {
                                new SimpleField("lineNumber", SearchFieldDataType.Int32),
                                new SimpleField("assetId", SearchFieldDataType.String) { IsFilterable = true },
                                new SearchableField("component") { IsFilterable = true },
                                new SearchableField("description"),
                                new SearchableField("criteria"),
                                new SearchableField("result") { IsFilterable = true },
                                new SimpleField("measurement", SearchFieldDataType.String),
                                new SearchableField("remarks")
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
