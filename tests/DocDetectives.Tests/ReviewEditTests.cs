using DocDetectives.Application.Ingestion;
using DocDetectives.Domain.Entities;

namespace DocDetectives.Tests;

/// <summary>Plain-words edits from the chat are applied to drafts safely: IDs and links kept, invalid values refused.</summary>
public class ReviewEditMergerTests
{
    private static JobPlanLine Line(int number, string title, string type, string? component = null, string? assetId = null) =>
        new(number, title, type, null, component is null ? null : $"EQ-{component}", component, assetId, null, null, "MC-1",
            type == JobPlanLineType.Inspection ? ["OK", "NOK"] : []);

    private static JobPlan Draft(params JobPlanLine[] lines) =>
        new("PM-DOC-1-MONTHLY", "Monthly check", JobPlanStatus.Draft, DocumentType.Manual, "doc-1", "pump.pdf",
            "DOC-1", "00001", "KSB", [], "every 1 months", 1, "Months", ["Grease"], [], DateTimeOffset.UtcNow, lines);

    [Fact]
    public void Frequency_change_keeps_id_and_rewrites_interval()
    {
        var draft = Draft(Line(1, "Check seal", JobPlanLineType.Inspection));
        var edit = ReviewEditMerger.ToEdit(draft) with { IntervalValue = 6, IntervalUnit = "month" };

        var outcome = ReviewEditMerger.Apply(draft, edit);

        Assert.Empty(outcome.Refused);
        Assert.Equal("PM-DOC-1-MONTHLY", outcome.Result.Id);
        Assert.Equal(6, outcome.Result.IntervalValue);
        Assert.Equal("Months", outcome.Result.IntervalUnit);
        Assert.Equal("6 months", outcome.Result.Interval);
        Assert.Equal("00001", outcome.Result.AssetId);
    }

    [Fact]
    public void Unknown_frequency_unit_is_refused_and_frequency_kept()
    {
        var draft = Draft(Line(1, "Check seal", JobPlanLineType.Inspection));
        var edit = ReviewEditMerger.ToEdit(draft) with { IntervalValue = 2, IntervalUnit = "fortnights" };

        var outcome = ReviewEditMerger.Apply(draft, edit);

        Assert.Single(outcome.Refused);
        Assert.Equal(1, outcome.Result.IntervalValue);
        Assert.Equal("Months", outcome.Result.IntervalUnit);
    }

    [Fact]
    public void Removing_and_adding_lines_renumbers_and_keeps_links()
    {
        var draft = Draft(
            Line(1, "Check seal", JobPlanLineType.Inspection, "Pump", "SUB-PUMP"),
            Line(2, "Grease bearing", JobPlanLineType.Task, "Motor", "SUB-MOTOR"),
            Line(3, "Check noise", JobPlanLineType.Inspection));
        var edit = ReviewEditMerger.ToEdit(draft);
        edit = edit with
        {
            Lines =
            [
                edit.Lines[2], // reordered first
                edit.Lines[0],
                new JobPlanLineEdit(null, "Check pump coupling", "inspection", null, "Pump", null, null, [])
            ]
        };

        var lines = ReviewEditMerger.Apply(draft, edit).Result.Lines;

        Assert.Equal([1, 2, 3], lines.Select(l => l.LineNumber));
        Assert.Equal(["Check noise", "Check seal", "Check pump coupling"], lines.Select(l => l.Title));
        Assert.Equal("SUB-PUMP", lines[1].AssetId);           // kept line keeps its asset
        Assert.Equal("SUB-PUMP", lines[2].AssetId);           // new line takes it from the same component
        Assert.Equal(JobPlanLineType.Inspection, lines[2].LineType);
        Assert.Equal(["OK", "NOK"], lines[2].AllowedResults); // inspection gets default results
        Assert.DoesNotContain(lines, l => l.Title == "Grease bearing");
    }

    [Fact]
    public void Removing_every_line_is_refused()
    {
        var draft = Draft(Line(1, "Check seal", JobPlanLineType.Inspection));
        var edit = ReviewEditMerger.ToEdit(draft) with { Lines = [] };

        var outcome = ReviewEditMerger.Apply(draft, edit);

        Assert.Single(outcome.Refused);
        Assert.Single(outcome.Result.Lines);
    }

    private static AssetProposal Proposal(bool rootExists = false) => new(
        "doc-1", "pump.pdf",
        [
            new Asset("ROOT", "Pump unit", "KSB", "Etanorm", "", "PUMP"),
            new Asset("ROOT-MOTOR", "Motor", "KSB", "", "", "PUMP", "ROOT", ["Bearing 6205"]),
            new Asset("ROOT-MOTOR-FAN", "Fan", "KSB", "", "", "PUMP", "ROOT-MOTOR"),
            new Asset("ROOT-SEAL", "Seal", "KSB", "", "", "PUMP", "ROOT")
        ],
        [],
        rootExists ? new HashSet<string> { "ROOT" } : null);

    [Fact]
    public void Proposal_rename_remove_and_add()
    {
        var proposal = Proposal();
        var edit = ReviewEditMerger.ToEdit(proposal);
        edit = edit with
        {
            Assets =
            [
                edit.Assets[0],
                edit.Assets[1] with { Name = "Drive motor" },
                edit.Assets[2],
                // Seal removed; a new sub-asset added under the motor
                new ProposedAssetEdit(null, "Terminal box", null, null, "ROOT-MOTOR", false, [])
            ]
        };

        var outcome = ReviewEditMerger.Apply(proposal, edit);
        var assets = outcome.Result.Assets;

        Assert.Empty(outcome.Refused);
        Assert.Equal("Drive motor", assets.Single(a => a.Id == "ROOT-MOTOR").Name);
        Assert.DoesNotContain(assets, a => a.Id == "ROOT-SEAL");
        var added = assets.Single(a => a.Name == "Terminal box");
        Assert.Equal("ROOT-TERMINAL-BOX", added.Id);
        Assert.Equal("ROOT-MOTOR", added.ParentAssetId);
    }

    [Fact]
    public void Removing_a_sub_asset_removes_its_new_children()
    {
        var proposal = Proposal();
        var edit = ReviewEditMerger.ToEdit(proposal);
        edit = edit with { Assets = [edit.Assets[0], edit.Assets[3]] }; // motor removed, fan should go too

        var assets = ReviewEditMerger.Apply(proposal, edit).Result.Assets;

        Assert.Equal(["ROOT", "ROOT-SEAL"], assets.Select(a => a.Id));
    }

    [Fact]
    public void Existing_assets_are_never_changed_and_the_main_asset_stays()
    {
        var proposal = Proposal(rootExists: true);
        var edit = ReviewEditMerger.ToEdit(proposal);
        edit = edit with { Assets = [edit.Assets[0] with { Name = "Renamed" }, edit.Assets[1], edit.Assets[2], edit.Assets[3]] };

        var outcome = ReviewEditMerger.Apply(proposal, edit);

        Assert.Single(outcome.Refused);
        Assert.Equal("Pump unit", outcome.Result.Assets[0].Name);

        var removeRoot = ReviewEditMerger.Apply(Proposal(), ReviewEditMerger.ToEdit(Proposal()) with { Assets = [] });
        Assert.Contains(removeRoot.Result.Assets, a => a.Id == "ROOT");
        Assert.NotEmpty(removeRoot.Refused);
    }

    private static ExtractedCharacteristic Numeric(string name, double value, string unit) =>
        new(name, name, CharacteristicValueType.Numeric, unit, unit, value, null, null, null, $"{name}: {value} {unit}");

    [Fact]
    public void Characteristic_value_change_drop_and_refusals()
    {
        IReadOnlyList<ExtractedCharacteristic> values =
        [
            Numeric("Voltage", 400, "V"),
            Numeric("Power", 1.5, "kW"),
            Numeric("Speed", 2900, "rpm")
        ];
        var edit = ReviewEditMerger.ToEdit(values);
        edit = edit with
        {
            Values =
            [
                edit.Values[0] with { Value = "230" },          // changed
                edit.Values[1] with { Value = "about one" },    // not a number: refused, kept
                // Speed dropped
                new CharacteristicValueEdit("Colour", "Blue", null) // not extracted: refused
            ]
        };

        var outcome = ReviewEditMerger.Apply(values, edit);

        Assert.Equal(2, outcome.Refused.Count);
        Assert.Equal(["Voltage", "Power"], outcome.Result.Select(c => c.Name));
        Assert.Equal(230, outcome.Result[0].NumericValue);
        Assert.Equal(1.5, outcome.Result[1].NumericValue);
    }
}

/// <summary>Typed replies in the chat review.</summary>
public class ReplyParserTests
{
    [Theory]
    [InlineData("yes", ReplyKind.Yes)]
    [InlineData("Submit.", ReplyKind.Yes)]
    [InlineData("looks good", ReplyKind.Yes)]
    [InlineData("no", ReplyKind.No)]
    [InlineData("skip", ReplyKind.No)]
    [InlineData("approve all", ReplyKind.ApproveAll)]
    [InlineData("skip all", ReplyKind.SkipAll)]
    [InlineData("all", ReplyKind.All)]
    [InlineData("none", ReplyKind.None)]
    [InlineData("undo", ReplyKind.Undo)]
    [InlineData("retry", ReplyKind.Retry)]
    public void Short_replies(string text, ReplyKind kind) => Assert.Equal(kind, ReplyParser.Parse(text)?.Kind);

    [Theory]
    [InlineData("1, 3", new[] { 1, 3 })]
    [InlineData("1 and 3", new[] { 1, 3 })]
    [InlineData("2-4", new[] { 2, 3, 4 })]
    [InlineData("#2", new[] { 2 })]
    public void Number_lists(string text, int[] numbers)
    {
        var reply = ReplyParser.Parse(text);
        Assert.Equal(ReplyKind.Numbers, reply?.Kind);
        Assert.Equal(numbers, reply!.Numbers);
    }

    [Theory]
    [InlineData("set the frequency to every 6 months")]
    [InlineData("Can you remove line 2?")]
    [InlineData("please rename the pump to Vacuum Pump")]
    public void Change_requests_are_edits_not_answers(string text)
    {
        Assert.Null(ReplyParser.Parse(text));
        Assert.True(ReplyParser.LooksLikeEdit(text));
    }

    [Theory]
    [InlineData("What is the maximum pressure?")]
    [InlineData("how often should the oil be changed")]
    public void Questions_are_not_edits(string text)
    {
        Assert.False(ReplyParser.LooksLikeEdit(text));
        Assert.True(ReplyParser.LooksLikeQuestion(text));
    }
}
