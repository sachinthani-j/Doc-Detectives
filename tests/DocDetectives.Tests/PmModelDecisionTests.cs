using DocDetectives.Application.Ingestion;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Tests.Corpus;
using Microsoft.Extensions.AI;

namespace DocDetectives.Tests;

/// <summary>
/// The PM model rules: a manual only gets PM models when the equipment warrants them ("when in doubt, leave it out").
/// The LLM is replaced by a fixed answer.
/// </summary>
public class PmModelDecisionTests
{
    private const string ScheduleJson = """
        "plans":[{"title":"Pump inspection","interval":"yearly","intervalValue":1,"intervalUnit":"Years",
          "lines":[{"title":"Check seal for leaks","lineType":"Inspection","allowedResults":["OK","NOK"]}]}]
        """;

    private static async Task<JobPlanReport> DraftAsync(string answerJson)
    {
        var catalog = new CorpusCatalog([]);
        var service = new JobPlanService(
            new JobPlanExtractionSkill(new FixedChat(answerJson)),
            new AssetReferenceResolver(catalog, new AssetMatchingSkill(catalog, new ScoringChat())),
            catalog,
            new NoPmModels());

        var manual = new ExtractedManual("doc-1", "pump.pdf", "# Maintenance", "KSB", [], "Pump", "{}");
        return await service.DraftAsync(manual, DocumentType.Manual, assetId: null);
    }

    [Fact]
    public async Task Equipment_that_does_not_warrant_pm_gets_no_pm_model_and_says_why()
    {
        var report = await DraftAsync("""
            {"pmModelNeeded":false,"pmModelReason":"Standalone thermometer; calibrate when needed.",
             "materials":[],"safetyNotes":[],
            """ + ScheduleJson + "}");

        Assert.Empty(report.Plans);
        Assert.Equal("No PM model: Standalone thermometer; calibrate when needed.", report.Note);
    }

    [Fact]
    public async Task Production_equipment_gets_a_pm_model_per_interval()
    {
        var report = await DraftAsync("""
            {"pmModelNeeded":true,"pmModelReason":"Production pump with several maintainable parts.",
             "materials":[],"safetyNotes":[],
            """ + ScheduleJson + "}");

        var plan = Assert.Single(report.Plans);
        Assert.StartsWith("PM-", plan.Id);
        Assert.Equal(1, plan.IntervalValue);
    }

    private sealed class FixedChat(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoPmModels : IPmModelGateway
    {
        public Task SaveAsync(PmModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string pmModelId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<PmModel>> GetForAssetAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PmModel>>([]);
    }
}
