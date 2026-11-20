using DocDetectives.Application.Skills;
using DocDetectives.Domain.Entities;
using DocDetectives.Tests.Corpus;

namespace DocDetectives.Tests;

/// <summary>
/// Confidence score checks for AssetMatchingSkill (0.50 floor, 0.85 auto-link threshold, persist only AutoLinked),
/// driven by the synthetic golden corpus and the synthetic asset export. No Azure access needed.
/// </summary>
public class ConfidenceScoreTests
{
    private static (AssetMatchingSkill Skill, CorpusCatalog Catalog, ScoringChat Chat) Build(double? fixedScore = null)
    {
        var catalog = new CorpusCatalog(CorpusData.Assets);
        var chat = new ScoringChat(fixedScore);
        return (new AssetMatchingSkill(catalog, chat), catalog, chat);
    }

    // ---- Corpus integrity -------------------------------------------------

    [Fact]
    public void Corpus_has_assets_and_all_status_kinds()
    {
        Assert.NotEmpty(CorpusData.Assets);
        Assert.Contains(CorpusData.Golden, r => r.ExpectedStatus == "AutoLinked");
        Assert.Contains(CorpusData.Golden, r => r.ExpectedStatus == "Created");
        Assert.Contains(CorpusData.Golden, r => r.ExpectedStatus == "n/a");
    }

    [Fact]
    public void Every_expected_asset_id_exists_with_the_same_manufacturer()
    {
        foreach (var row in CorpusData.Golden)
            foreach (var id in row.ExpectedAssetIds)
            {
                var asset = Assert.Single(CorpusData.Assets, a => a.Id == id);
                Assert.Equal(row.Manufacturer, asset.Manufacturer);
                Assert.Contains(asset.ModelNumber, row.Models);
            }
    }

    // ---- Golden corpus: expected statuses ---------------------------------

    [Theory]
    [MemberData(nameof(AutoLinkedFiles))]
    public async Task Known_manual_is_auto_linked_to_exactly_the_expected_assets(string file)
    {
        var row = CorpusData.Row(file);
        var (skill, catalog, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(row.ToManual());

        var auto = results.Where(r => r.Status == MappingStatus.AutoLinked).Select(r => r.AssetId).Order().ToList();
        Assert.Equal(row.ExpectedAssetIds.Order().ToList(), auto);
        Assert.Equal(row.ExpectedAssetIds.Order().ToList(), catalog.Links.Select(l => l.AssetId).Order().ToList());
        Assert.All(results.Where(r => r.Status == MappingStatus.AutoLinked), r => Assert.InRange(r.ConfidenceScore, 0.85, 1.0));
    }

    [Theory]
    [MemberData(nameof(CreatedFiles))]
    public async Task Manual_without_an_asset_is_never_auto_linked_or_persisted(string file)
    {
        var (skill, catalog, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row(file).ToManual());

        Assert.DoesNotContain(results, r => r.Status == MappingStatus.AutoLinked);
        Assert.Empty(catalog.Links);
    }

    [Theory]
    [MemberData(nameof(NotApplicableFiles))]
    public async Task Non_equipment_document_is_never_linked(string file)
    {
        var (skill, catalog, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row(file).ToManual());

        Assert.DoesNotContain(results, r => r.Status == MappingStatus.AutoLinked);
        Assert.Empty(catalog.Links);
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public async Task Every_result_obeys_the_score_rules(string file)
    {
        var (skill, _, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row(file).ToManual(), persistLinks: false);

        foreach (var r in results)
        {
            Assert.InRange(r.ConfidenceScore, 0.50, 1.0);
            Assert.Equal(r.ConfidenceScore >= 0.85 ? MappingStatus.AutoLinked : MappingStatus.NeedsReview, r.Status);
        }
    }

    [Fact]
    public async Task Corpus_has_no_wrong_links_and_no_missed_links()
    {
        int tp = 0, fp = 0, fn = 0;
        foreach (var row in CorpusData.Golden)
        {
            var (skill, _, _) = Build();
            var auto = (await skill.MatchManualToAssetsAsync(row.ToManual(), persistLinks: false))
                .Where(r => r.Status == MappingStatus.AutoLinked).Select(r => r.AssetId).ToHashSet();
            tp += auto.Intersect(row.ExpectedAssetIds).Count();
            fp += auto.Except(row.ExpectedAssetIds).Count();
            fn += row.ExpectedAssetIds.Except(auto).Count();
        }
        Assert.Equal(0, fp);
        Assert.Equal(0, fn);
        Assert.True(tp > 0);
    }

    // ---- Look-alike trap and family manuals --------------------------------

    [Fact]
    public async Task Sibling_model_goes_to_review_and_is_not_linked()
    {
        // LABSCI 15LV has no asset, but LABSCI 11L autoclaves from the same maker are candidates.
        var (skill, catalog, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row("LABSCI-15LV.pdf").ToManual());

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.Equal(MappingStatus.NeedsReview, r.Status);
            Assert.InRange(r.ConfidenceScore, 0.50, 0.849);
        });
        Assert.Empty(catalog.Links);
    }

    [Fact]
    public async Task Family_manual_scores_each_model_independently()
    {
        var (skill, _, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row("Hyster A380 H2.0-3.0XT.pdf").ToManual());

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(MappingStatus.AutoLinked, r.Status));
    }

    // ---- Boundaries on a real corpus manual --------------------------------

    private static ExtractedManual Labsci11L() => CorpusData.Row("LABSCI-11L.pdf").ToManual();

    [Theory]
    [InlineData(0.499, null)]
    [InlineData(0.50, MappingStatus.NeedsReview)]
    [InlineData(0.849, MappingStatus.NeedsReview)]
    [InlineData(0.85, MappingStatus.AutoLinked)]
    [InlineData(1.00, MappingStatus.AutoLinked)]
    public async Task Fixed_score_maps_to_expected_status(double score, MappingStatus? expected)
    {
        var (skill, catalog, _) = Build(score);

        var results = await skill.MatchManualToAssetsAsync(Labsci11L());

        if (expected is null)
        {
            Assert.Empty(results);
            Assert.Empty(catalog.Links);
            return;
        }
        Assert.Equal(2, results.Count);   // two LABSCI 11L assets in the corpus
        Assert.All(results, r => Assert.Equal(expected, r.Status));
        Assert.Equal(expected == MappingStatus.AutoLinked ? 2 : 0, catalog.Links.Count);
    }

    [Fact]
    public async Task Score_below_floor_is_dropped_even_with_a_low_custom_threshold()
    {
        var (skill, _, _) = Build(0.40);

        Assert.Empty(await skill.MatchManualToAssetsAsync(Labsci11L(), autoLinkThreshold: 0.30));
    }

    [Theory]
    [InlineData(0.99, MappingStatus.NeedsReview)]
    [InlineData(0.95, MappingStatus.AutoLinked)]
    public async Task Custom_threshold_changes_the_status_of_a_scripted_score(double threshold, MappingStatus expected)
    {
        var (skill, _, _) = Build();   // exact model + manufacturer scores 0.95

        var results = await skill.MatchManualToAssetsAsync(Labsci11L(), autoLinkThreshold: threshold);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(expected, r.Status));
    }

    [Fact]
    public async Task PersistLinks_false_never_writes()
    {
        var (skill, catalog, _) = Build();

        var results = await skill.MatchManualToAssetsAsync(Labsci11L(), persistLinks: false);

        Assert.All(results, r => Assert.Equal(MappingStatus.AutoLinked, r.Status));
        Assert.Empty(catalog.Links);
    }

    [Fact]
    public async Task Persisted_link_carries_manual_score_and_status()
    {
        var (skill, catalog, _) = Build();

        await skill.MatchManualToAssetsAsync(CorpusData.Row("pf12445-pw3000-en.pdf").ToManual());

        Assert.Equal(3, catalog.Links.Count);   // three Crown PW 3000 assets
        Assert.All(catalog.Links, l =>
        {
            Assert.Equal("pf12445-pw3000-en", l.ManualId);
            Assert.Equal(0.95, l.Score);
            Assert.Equal(MappingStatus.AutoLinked, l.Status);
        });
    }

    [Fact]
    public async Task No_candidates_means_no_llm_call()
    {
        var (skill, _, chat) = Build();

        var results = await skill.MatchManualToAssetsAsync(CorpusData.Row("Heraeus-Biofuge-Fresco-Manual.pdf").ToManual());

        Assert.Empty(results);
        Assert.Equal(0, chat.Calls);
    }

    public static IEnumerable<object[]> AutoLinkedFiles() => CorpusData.FilesWithStatus("AutoLinked");
    public static IEnumerable<object[]> CreatedFiles() => CorpusData.FilesWithStatus("Created");
    public static IEnumerable<object[]> NotApplicableFiles() => CorpusData.FilesWithStatus("n/a");
    public static IEnumerable<object[]> AllFiles() => CorpusData.AllFiles();
}
