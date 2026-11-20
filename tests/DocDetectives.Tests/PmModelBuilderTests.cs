using DocDetectives.Application.Services;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Tests;

/// <summary>A drafted PM model becomes a PM model with one PM job per piece of equipment, each with its own lines.</summary>
public class PmModelBuilderTests
{
    private static JobPlanLine Line(string title, string type, string? assetId = null, string? component = null) =>
        new(0, title, type, null, null, component, assetId, null, null, null,
            type == JobPlanLineType.Inspection ? ["OK", "NOK"] : []);

    private static JobPlan Draft(string? assetId, params JobPlanLine[] lines) =>
        new("PM-DOC-1-YEARLY", "Pump inspection", JobPlanStatus.Draft, DocumentType.Manual, "doc-1", "pump.pdf",
            "DOC-1", assetId, "KSB", [], "Yearly", 1, "Years", [], [], DateTimeOffset.UtcNow, lines);

    [Fact]
    public void No_asset_means_no_pm_model()
    {
        Assert.Null(PmModelBuilder.Build(Draft(null, Line("Check seal", JobPlanLineType.Inspection))));
    }

    [Fact]
    public void Lines_on_the_asset_form_pm_job_001_with_frequency_from_the_manual()
    {
        var model = PmModelBuilder.Build(Draft("00001",
            Line("Check seal for leaks", JobPlanLineType.Inspection),
            Line("Check vibration", JobPlanLineType.Inspection)))!;

        Assert.Equal("PM-DOC-1-YEARLY", model.Id);
        Assert.Equal("00001", model.AssetId);
        Assert.Equal(1, model.FrequencyNumber);
        Assert.Equal("Years", model.FrequencyUnit);

        var job = Assert.Single(model.Jobs);
        Assert.Equal("001", job.JobCode);
        Assert.Equal("00001", job.AssetId);
        Assert.Equal(PmJobType.Inspection, job.JobType);
        Assert.Equal([1, 2], job.Lines.Select(l => l.LineNumber));
        Assert.All(job.Lines, l => Assert.Equal(["OK", "NOK"], l.AllowedResults));
    }

    [Fact]
    public void Sub_asset_lines_get_their_own_pm_job_after_the_main_asset()
    {
        var model = PmModelBuilder.Build(Draft("00001",
            Line("Check motor bearing", JobPlanLineType.Inspection, "00001-M", "Motor"),
            Line("Lubricate pump bearing", JobPlanLineType.Task),
            Line("Check pump seal", JobPlanLineType.Inspection, "00001")))!;

        Assert.Equal(2, model.Jobs.Count);

        var main = model.Jobs[0];
        Assert.Equal(("001", "00001"), (main.JobCode, main.AssetId));
        Assert.Equal(PmJobType.PeriodicalMaintenance, main.JobType);
        Assert.Equal(["Lubricate pump bearing", "Check pump seal"], main.Lines.Select(l => l.Title));

        var motor = model.Jobs[1];
        Assert.Equal(("002", "00001-M", "Motor"), (motor.JobCode, motor.AssetId, motor.Component));
        Assert.Equal("Pump inspection - Motor", motor.Description);
        Assert.Equal(1, Assert.Single(motor.Lines).LineNumber);
    }
}
