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
builder.Services.AddSingleton<AssetSystemMcpTools>();

// 3. Register Microsoft.Extensions.AI Chat & Embedding Clients (Foundry OpenAI)
builder.Services.AddSingleton<AzureOpenAIClient>(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value.FoundryOpenAI;
    var endpoint = new Uri(cfg.Endpoint);
    var hasApiKey = !string.IsNullOrWhiteSpace(cfg.ApiKey)
                    && !cfg.ApiKey.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);

    return hasApiKey
        ? new AzureOpenAIClient(endpoint, new AzureKeyCredential(cfg.ApiKey))
        : new AzureOpenAIClient(endpoint, new DefaultAzureCredential());
});

// 4. Register RAG Index, Skills, and Ingestion Orchestrator
builder.Services.AddSingleton<IKnowledgeIndex, AzureAiSearchKnowledgeIndex>();
builder.Services.AddSingleton<MarkdownManualChunker>();
builder.Services.AddSingleton<ManualStructureExtractionSkill>();
builder.Services.AddSingleton<AssetMatchingSkill>();
builder.Services.AddSingleton<TechnicalManualQaSkill>();
builder.Services.AddSingleton<ManualIngestionOrchestrator>();

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value.FoundryOpenAI;
    return sp.GetRequiredService<AzureOpenAIClient>()
        .GetChatClient(cfg.ChatDeployment)
        .AsIChatClient();
});

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var cfg = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value.FoundryOpenAI;
    return sp.GetRequiredService<AzureOpenAIClient>()
        .GetEmbeddingClient(cfg.EmbeddingDeployment)
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
    Console.WriteLine($"Found {files.Count} manual file(s) to extract and embed.\n");

    for (var i = 0; i < files.Count; i++)
    {
        var file = files[i];
        Console.WriteLine($"[{i + 1}/{files.Count}] Processing '{Path.GetFileName(file)}'...");

        var report = await orchestrator.ProcessManualAsync(file, threshold, autoCreateAssets);
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