using DocDetectives.Application.Ingestion;
using DocDetectives.Application.Skills;
using DocDetectives.Domain.Abstractions;
using DocDetectives.Domain.Entities;
using DocDetectives.Tests.Corpus;

namespace DocDetectives.Tests;

/// <summary>A manual linked to several assets gets the approved PM model on each of them, with the same PM jobs and lines.</summary>
public class PmModelsOnLinkedAssetsTests
{
    private static readonly Asset[] Assets =
    [
        new("TOWER-1", "Cooling tower 1", "BAC", "RCT", "S1", "Cooling tower"),
        new("TOWER-1-FAN", "Fan motor - Cooling tower 1", "BAC", "", "", "Motor", ParentAssetId: "TOWER-1"),
        new("TOWER-2", "Cooling tower 2", "BAC", "RCT", "S2", "Cooling tower"),
        new("TOWER-2-FAN", "Fan motor - Cooling tower 2", "BAC", "", "", "Motor", ParentAssetId: "TOWER-2"),
        new("TOWER-3", "Cooling tower 3", "BAC", "RCT", "S3", "Cooling tower")
    ];

    private static JobPlanLine Line(string title, string? component, string? assetId) =>
        new(0, title, JobPlanLineType.Inspection, null, null, component, assetId, null, null, null, ["OK", "NOK"]);

    [Fact]
    public async Task Each_linked_asset_gets_its_own_pm_model_with_its_own_sub_assets()
    {
        var catalog = new CorpusCatalog(Assets);
        var store = new StoredPmModels();
        var service = new JobPlanService(
            new JobPlanExtractionSkill(new ScoringChat()),
            new AssetReferenceResolver(catalog, new AssetMatchingSkill(catalog, new ScoringChat())),
            catalog,
            store);

        // Drafted on TOWER-1: the fan line already points at TOWER-1's fan motor.
        var draft = new JobPlan("PM-RCV03-MONTHLY", "Monthly checks", JobPlanStatus.Draft, DocumentType.Manual, "doc", "bac.pdf",
            "RCV03", "TOWER-1", "BAC", [], "Monthly", 1, "Months", [], [], DateTimeOffset.UtcNow,
            [Line("Check water level", null, null), Line("Check fan motor", "Fan motor", "TOWER-1-FAN")]);

        var models = await service.CreatePmModelsAsync(draft, ["TOWER-1", "TOWER-2", "TOWER-3", "TOWER-2"]);

        Assert.Equal(["TOWER-1", "TOWER-2", "TOWER-3"], models.Select(m => m.AssetId));
        Assert.Equal(["PM-RCV03-MONTHLY-TOWER-1", "PM-RCV03-MONTHLY-TOWER-2", "PM-RCV03-MONTHLY-TOWER-3"], models.Select(m => m.Id));
        Assert.Equal(3, store.Saved.Count);
        Assert.All(models, m => Assert.Equal(2, m.Jobs.Sum(j => j.Lines.Count)));

        // Each tower's fan line goes to its own fan motor; a tower without one keeps the line on itself.
        Assert.Equal(["TOWER-1", "TOWER-1-FAN"], models[0].Jobs.Select(j => j.AssetId));
        Assert.Equal(["TOWER-2", "TOWER-2-FAN"], models[1].Jobs.Select(j => j.AssetId));
        Assert.Equal("TOWER-3", Assert.Single(models[2].Jobs).AssetId);
    }

    private sealed class StoredPmModels : IPmModelGateway
    {
        public List<PmModel> Saved { get; } = [];
        public Task SaveAsync(PmModel model, CancellationToken cancellationToken = default) { Saved.Add(model); return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string pmModelId, CancellationToken cancellationToken = default) => Task.FromResult(Saved.Any(m => m.Id == pmModelId));
        public Task<IReadOnlyList<PmModel>> GetForAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PmModel>>(Saved.Where(m => m.AssetId == assetId).ToList());
    }
}
