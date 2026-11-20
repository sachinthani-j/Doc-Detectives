using System.ClientModel;
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
using Microsoft.Extensions.Options;
using OpenAI;

namespace DocDetectives.Infrastructure;

/// <summary>
/// Registers every DocDetectives service (Azure clients, indexes, skills, orchestrator) so the
/// Web host (and any future host, e.g. a worker or MCP server) shares one setup.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddDocDetectives(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DocDetectivesOptions>(configuration.GetSection(DocDetectivesOptions.SectionName));

        // 1. Azure AI Content Understanding extractor (Microsoft Foundry)
        services.AddHttpClient<IManualExtractor, AzureContentUnderstandingExtractor>();

        // 2. Asset catalog, jobs, PM models & characteristics (Azure AI Search) and MCP tools
        services.AddSingleton<IAssetCatalogGateway, AzureAiSearchAssetCatalogGateway>();
        services.AddSingleton<IJobGateway, AzureAiSearchJobGateway>();
        services.AddSingleton<IPmModelGateway, AzureAiSearchPmModelGateway>();
        services.AddSingleton<ICharacteristicCatalog, AzureAiSearchCharacteristicCatalog>();
        services.AddSingleton<AssetSystemMcpTools>();

        // 3. Microsoft.Extensions.AI chat & embedding clients: OpenAI or Azure OpenAI (Foundry), set by "AiProvider".
        //    AzureOpenAIClient derives from OpenAIClient, so everything below works the same for both.
        services.AddSingleton<OpenAIClient>(sp =>
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

        services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;
            var model = IsOpenAI(options) ? options.OpenAI.ChatModel : options.FoundryOpenAI.ChatDeployment;
            return sp.GetRequiredService<OpenAIClient>()
                .GetChatClient(model)
                .AsIChatClient();
        });

        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DocDetectivesOptions>>().Value;
            var model = IsOpenAI(options) ? options.OpenAI.EmbeddingModel : options.FoundryOpenAI.EmbeddingDeployment;
            return sp.GetRequiredService<OpenAIClient>()
                .GetEmbeddingClient(model)
                .AsIEmbeddingGenerator();
        });

        // 4. RAG index, skills and ingestion orchestrator
        services.AddSingleton<IKnowledgeIndex, AzureAiSearchKnowledgeIndex>();
        services.AddSingleton<MarkdownManualChunker>();
        services.AddSingleton<ManualStructureExtractionSkill>();
        services.AddSingleton<DocumentClassificationSkill>();
        services.AddSingleton<InspectionExtractionSkill>();
        services.AddSingleton<AssetReferenceResolver>();
        services.AddSingleton<InspectionJobService>();
        services.AddSingleton<JobPlanExtractionSkill>();
        services.AddSingleton<JobPlanService>();
        services.AddSingleton<CharacteristicExtractionSkill>();
        services.AddSingleton<CharacteristicService>();
        services.AddSingleton<AssetProposalService>();
        services.AddSingleton<AssetMatchingSkill>();
        services.AddSingleton<ReviewEditSkill>();
        services.AddSingleton<TechnicalManualQaSkill>();
        services.AddSingleton<ManualIngestionOrchestrator>();

        return services;
    }

    private static bool IsOpenAI(DocDetectivesOptions options) =>
        string.Equals(options.AiProvider, AiProviders.OpenAI, StringComparison.OrdinalIgnoreCase);

    // A key counts as set when it is not empty and not a "<YOUR_...>" placeholder.
    private static bool IsSet(string? key) =>
        !string.IsNullOrWhiteSpace(key) && !key.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase);
}
