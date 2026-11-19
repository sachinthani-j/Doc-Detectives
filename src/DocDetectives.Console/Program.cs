using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using DocDetectives.Application.Ingestion;
using DocDetectives.Application.Services;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Infrastructure.Configuration;
using DocDetectives.Infrastructure.Foundry;
using DocDetectives.Infrastructure.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

var mode = GetArgValue(args, "--mode") ?? "ingest";
var customFolder = GetArgValue(args, "--dir");
var targetAssetId = GetArgValue(args, "--asset");

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.Configure<DocDetectivesOptions>(
    builder.Configuration.GetSection(DocDetectivesOptions.SectionName));

// 1. Register Azure AI Content Understanding Extractor (Microsoft Foundry)
builder.Services.AddHttpClient<IManualExtractor, AzureContentUnderstandingExtractor>();

// 2. Register Asset System Gateway & MCP Server Tools
builder.Services.AddSingleton<IAssetCatalogGateway, AzureAiSearchAssetCatalogGateway>();
builder.Services.AddSingleton<IJobGateway, AzureAiSearchJobGateway>();
builder.Services.AddSingleton<AssetSystemMcpTools>();

// 3. Register Microsoft.Extensions.AI Chat & Embedding Clients: OpenAI or Azure OpenAI (Foundry), set by "AiProvider".
//    AzureOpenAIClient derives from OpenAIClient, so everything below works the same for both.
builder.Services.AddSingleton<OpenAIClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;

    if (IsOpenAI(options))
    {
        var apiKey = IsSet(options.OpenAI.ApiKey)
            ? options.OpenAI.ApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (!IsSet(apiKey))
        {
            throw new InvalidOperationException(
                "AiProvider is 'OpenAI' but no key is set: fill DocDetectives:OpenAI:ApiKey in appsettings.json " +
                "or set the OPENAI_API_KEY environment variable.");
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey!));
    }

    var cfg = options.FoundryOpenAI;
    var endpoint = new Uri(cfg.Endpoint);
    return IsSet(cfg.ApiKey)
        ? new AzureOpenAIClient(endpoint, new AzureKeyCredential(cfg.ApiKey))
        : new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
});

// 4. Register RAG Index, Skills, and Ingestion Orchestrator
builder.Services.AddSingleton<IKnowledgeIndex, AzureAiSearchKnowledgeIndex>();
builder.Services.AddSingleton<MarkdownManualChunker>();
builder.Services.AddSingleton<ManualStructureExtractionSkill>();
builder.Services.AddSingleton<DocumentClassificationSkill>();
builder.Services.AddSingleton<InspectionExtractionSkill>();
builder.Services.AddSingleton<InspectionJobService>();
builder.Services.AddSingleton<AssetMatchingSkill>();
builder.Services.AddSingleton<TechnicalManualQaSkill>();
builder.Services.AddSingleton<ManualIngestionOrchestrator>();

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;
    var model = IsOpenAI(options) ? options.OpenAI.ChatModel : options.FoundryOpenAI.ChatDeployment;
    return sp.GetRequiredService<OpenAIClient>()
        .GetChatClient(model)
        .AsIChatClient();
});

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;
    var model = IsOpenAI(options) ? options.OpenAI.EmbeddingModel : options.FoundryOpenAI.EmbeddingDeployment;
    return sp.GetRequiredService<OpenAIClient>()
        .GetEmbeddingClient(model)
        .AsIEmbeddingGenerator();
});

// Optional: Run as an MCP stdio server for external Foundry/IDE agents
if (mode.Equals("mcp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(AssetSystemMcpTools).Assembly);

    await builder.Build().RunAsync();
    return;
}

using var host = builder.Build();
var options = host.Services.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;
var manualsFolder = customFolder ?? options.ManualsFolder;

switch (mode.ToLowerInvariant())
{
    case "ingest":
        await RunFullIngestionAsync(
            host.Services,
            manualsFolder,
            options.AutoLinkConfidenceThreshold,
            options.AutoCreateAssets);
        break;

    case "chat":
        await RunAssetChatAsync(host.Services, targetAssetId);
        break;

    default:
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --mode ingest --dir ./manuals   (Extract, match assets, & embed manuals into Azure AI Search)");
        Console.WriteLine("  dotnet run -- --mode chat   --asset PUMP-102  (Ask technical questions scoped to an asset)");
        Console.WriteLine("  dotnet run -- --mode mcp                      (Start stdio MCP server exposing asset tools)");
        break;
}

static async Task RunFullIngestionAsync(
    IServiceProvider services,
    string folderPath,
    double threshold,
    bool autoCreateAssets)
{
    var orchestrator = services.GetRequiredService<ManualIngestionOrchestrator>();
    var files = orchestrator.GetManualFilesFromFolder(folderPath);

    Console.WriteLine($"Scanning folder: {Path.GetFullPath(folderPath)}");
    Console.WriteLine($"Found {files.Count} document(s) to process.\n");

    for (var i = 0; i < files.Count; i++)
    {
        var file = files[i];
        Console.WriteLine($"[{i + 1}/{files.Count}] Processing '{Path.GetFileName(file)}'...");

        var document = await orchestrator.ProcessDocumentAsync(file, threshold, autoCreateAssets);
        Console.WriteLine($"  Document Type   : {document.Classification.DocumentType} ({document.Classification.Reason})");

        if (document.Inspection is { } inspection)
        {
            PrintInspection(document.DocumentId, inspection);
            Console.WriteLine();
            continue;
        }

        var report = document.Manual!;
        var structure = report.ExtractedManual.AssetStructure;

        Console.WriteLine($"  Manual ID       : {report.ExtractedManual.ManualId}");
        Console.WriteLine($"  Manufacturer    : {report.ExtractedManual.Manufacturer ?? "Unknown"}");
        Console.WriteLine($"  Equipment Name  : {structure?.EquipmentName ?? "Unknown"}");
        Console.WriteLine($"  Models          : {string.Join(", ", report.ExtractedManual.ModelNumbers)}");
        Console.WriteLine($"  Category        : {report.ExtractedManual.EquipmentCategory ?? "Unknown"}");

        if (structure is not null)
        {
            Console.WriteLine($"  Extracted Data  : {structure.TechnicalSpecifications.Count} specs, " +
                              $"{structure.SpareParts.Count} spare parts, {structure.FaultCodes.Count} fault codes, " +
                              $"{structure.SafetyWarnings.Count} safety warnings");
        }

        Console.WriteLine($"  Embedded Chunks : {report.EmbeddedChunkCount} vector(s) stored in Azure AI Search");

        if (report.Mappings.Count == 0)
        {
            Console.WriteLine("  Asset Matches   : None");
        }
        else
        {
            foreach (var map in report.Mappings)
            {
                Console.WriteLine($"  Asset Match     : [{map.AssetId}] | Score: {map.ConfidenceScore:P0} | Status: {map.Status} | {map.Reasoning}");
            }
        }

        Console.WriteLine($"  Asset Creation  : {report.AssetCreationNote}");
        PrintAssetTree(report.CreatedAssets);
        Console.WriteLine();
    }

    Console.WriteLine("Ingestion and embedding complete.");
}

static async Task RunAssetChatAsync(IServiceProvider services, string? assetId)
{
    if (string.IsNullOrWhiteSpace(assetId))
    {
        Console.Write("Enter Asset ID (or press Enter to search all manuals): ");
        assetId = Console.ReadLine()?.Trim() ?? string.Empty;
    }

    var qaSkill = services.GetRequiredService<TechnicalManualQaSkill>();
    var scopeLabel = string.IsNullOrWhiteSpace(assetId) ? "ALL MANUALS" : $"Asset [{assetId}]";
    Console.WriteLine($"\nConnected to {scopeLabel}. Type 'exit' to quit.\n");

    while (true)
    {
        Console.Write("Technician > ");
        var question = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(question) || question.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        var answer = await qaSkill.AskAssetQuestionAsync(assetId, question);
        Console.WriteLine($"\nDocDetectives > {answer}\n");
    }
}

static void PrintInspection(string documentId, InspectionProcessingReport report)
{
    var doc = report.Inspection;
    Console.WriteLine($"  Document ID     : {documentId}");
    Console.WriteLine($"  Inspection      : {doc.Title ?? "Untitled"}" +
                      (doc.InspectionDate is null ? "" : $" | Date: {doc.InspectionDate}") +
                      (doc.Inspector is null ? "" : $" | Inspector: {doc.Inspector}"));
    Console.WriteLine($"  Asset Stated    : ID: {doc.AssetId ?? "-"} | Serial: {doc.SerialNumber ?? "-"} | " +
                      $"Name: {doc.AssetName ?? "-"} | Make/Model: {doc.Manufacturer ?? "-"} {doc.ModelNumber ?? ""}");
    Console.WriteLine($"  Asset           : {(report.Asset is null ? "None" : $"[{report.Asset.Id}] {report.Asset.Name}")}");
    Console.WriteLine($"  Asset Match     : {report.AssetResolution}");
    Console.WriteLine($"  Job             : {report.JobNote}");

    if (report.Job is not { } job)
    {
        return;
    }

    // Plain ASCII only: the default Windows console code page cannot display characters such as '•'.
    foreach (var line in job.Lines)
    {
        var target = line.AssetId == job.AssetId ? $"{line.AssetId} (job asset)" : line.AssetId;
        Console.WriteLine($"                    {line.LineNumber,3}. {line.Description}");
        Console.WriteLine($"                         Equipment: [{target}]");

        string?[] details =
        [
            line.Criteria is null ? null : $"Criteria: {line.Criteria}",
            line.Result is null ? null : $"Result: {line.Result}",
            line.Measurement is null ? null : $"Measurement: {line.Measurement}",
            line.Remarks is null ? null : $"Remarks: {line.Remarks}"
        ];

        foreach (var detail in details.OfType<string>())
        {
            Console.WriteLine($"                         {detail}");
        }
    }
}

static void PrintAssetTree(IReadOnlyList<DocDetectives.Domain.Entities.Asset> assets)
{
    if (assets.Count == 0)
    {
        return;
    }

    void Print(DocDetectives.Domain.Entities.Asset asset, int depth)
    {
        var indent = new string(' ', 20 + depth * 4);
        Console.WriteLine($"{indent}{asset.Name}  [{asset.Id}]");

        foreach (var part in asset.SpareParts ?? [])
        {
            // Plain ASCII: the default Windows console code page cannot display characters such as '•'.
            Console.WriteLine($"{indent}    - Spare part: {part}");
        }

        foreach (var child in assets.Where(a => a.ParentAssetId == asset.Id))
        {
            Print(child, depth + 1);
        }
    }

    Print(assets[0], 0);
}

static bool IsOpenAI(DocDetectivesOptions options) =>
    string.Equals(options.AiProvider, AiProviders.OpenAI, StringComparison.OrdinalIgnoreCase);

// A key counts as set when it is not empty and not a "<YOUR_...>" placeholder.
static bool IsSet(string? key) =>
    !string.IsNullOrWhiteSpace(key) && !key.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

static string? GetArgValue(string[] args, string key)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}