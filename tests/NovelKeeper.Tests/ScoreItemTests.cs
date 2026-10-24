using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>任意評価基準（項目別の採点）の規則のテスト</summary>
public class ScoreItemTests
{
    private static ScoreItem I(string id, string name, int max) => new() { Id = id, Name = name, Max = max };

    private static List<ScoreItem> L(params ScoreItem[] items) => items.ToList();

    private static readonly List<ScoreItem> Items = L(I("a", "シナリオ", 40), I("b", "絵", 30), I("c", "音楽", 30));

    private static Dictionary<string, string> T(string a = "", string b = "", string c = "") =>
        new() { ["a"] = a, ["b"] = b, ["c"] = c };

    // ---- 項目の設定 ----

    [Fact]
    public void Template_MustSumToExactly100()
    {
        Assert.Null(ScoreItemRules.ValidateTemplate(Items));
        Assert.Equal("入力エラー。採点基準は合計が100点になるように入力してください。", ScoreItemRules.ValidateTemplate(L(I("a", "A", 60), I("b", "B", 30))));
        Assert.Equal("入力エラー。採点基準は合計が100点になるように入力してください。", ScoreItemRules.ValidateTemplate(L(I("a", "A", 60), I("b", "B", 50))));
    }

    [Fact]
    public void Template_SingleItemWith100IsAllowed() =>
        Assert.Null(ScoreItemRules.ValidateTemplate(L(I("a", "総合", 100))));

    [Fact]
    public void Template_RejectsEmptyBlankDuplicateAndOutOfRange()
    {
        Assert.NotNull(ScoreItemRules.ValidateTemplate(L()));
        Assert.Contains("空", ScoreItemRules.ValidateTemplate(L(I("a", " ", 100))));
        Assert.Contains("同じ名前", ScoreItemRules.ValidateTemplate(L(I("a", "X", 50), I("b", "X ", 50))));
        Assert.Contains("1～100", ScoreItemRules.ValidateTemplate(L(I("a", "A", 0), I("b", "B", 100))));
        Assert.Contains("1～100", ScoreItemRules.ValidateTemplate(L(I("a", "A", 101))));
    }

    [Fact]
    public void Template_LimitsItemCount()
    {
        var many = Enumerable.Range(0, ScoreItemRules.MaxItems + 1).Select(i => I("i" + i, "項目" + i, 1)).ToList();
        Assert.Contains($"{ScoreItemRules.MaxItems} 個まで", ScoreItemRules.ValidateTemplate(many));
    }

    [Theory]
    [InlineData(1, new[] { 100 })]
    [InlineData(3, new[] { 34, 33, 33 })]
    [InlineData(4, new[] { 25, 25, 25, 25 })]
    public void Distribute_AlwaysSumsTo100(int count, int[] expected)
    {
        var d = ScoreItemRules.Distribute(count);
        Assert.Equal(expected, d);
        Assert.Equal(100, d.Sum());
    }

    // ---- 作品ごとの採点 ----

    [Fact]
    public void Check_AllBlank_MeansNoItemScores_SoManualScoreStays()
    {
        var r = ScoreItemRules.Check(Items, T());
        Assert.False(r.AnyEntered);
        Assert.True(r.Ok);
        Assert.Null(r.Total);
    }

    [Fact]
    public void Check_AllEntered_SumsTotal()
    {
        var r = ScoreItemRules.Check(Items, T("35", "20", "25"));
        Assert.True(r.Ok);
        Assert.Equal(80, r.Total);
    }

    [Fact]
    public void Check_FullMarks_Is100()
    {
        Assert.Equal(100, ScoreItemRules.Check(Items, T("40", "30", "30")).Total);
    }

    [Fact]
    public void Check_OverMax_IsErrorOnThatItem()
    {
        var r = ScoreItemRules.Check(Items, T("41", "20", "25"));
        Assert.False(r.Ok);
        Assert.Equal("入力エラー。0～40点の範囲で入力してください。", r.Errors["a"]);
        Assert.Null(r.Total);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("3.5")]
    public void Check_NotAWholeNonNegativeNumber_IsError(string text)
    {
        var r = ScoreItemRules.Check(Items, T(text, "20", "25"));
        Assert.False(r.Ok);
        Assert.True(r.Errors.ContainsKey("a"));
    }

    [Fact]
    public void Check_PartialEntry_IsErrorOnMissingItems()
    {
        var r = ScoreItemRules.Check(Items, T("35", "", "25"));
        Assert.False(r.Ok);
        Assert.Equal("入力エラー。0～30点の範囲で入力してください。", r.Errors["b"]);
        Assert.False(r.Errors.ContainsKey("a"));
        Assert.Null(r.Total);
    }

    [Fact]
    public void Check_ZeroIsAValidScore()
    {
        var r = ScoreItemRules.Check(Items, T("0", "0", "0"));
        Assert.True(r.Ok);
        Assert.Equal(0, r.Total);
    }

    // ---- 記録と、基準が変わったときの判定 ----

    private static Review Scored(params (string Id, int Score)[] scores)
    {
        var r = new Review();
        ScoreItemRules.Apply(r, Items, ScoreItemRules.Check(Items,
            scores.ToDictionary(s => s.Id, s => s.Score.ToString())));
        return r;
    }

    [Fact]
    public void Apply_StoresScoresAndBasis_AndBlankClearsThem()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        Assert.Equal(80, r.ItemScores!.Values.Sum());
        Assert.Equal(new ScoreBasis("絵", 30), r.ItemBasis!["b"]);

        ScoreItemRules.Apply(r, Items, ScoreItemRules.Check(Items, T()));
        Assert.Null(r.ItemScores);
        Assert.Null(r.ItemBasis);
    }

    [Fact]
    public void Apply_UntouchedStaleItems_KeepOldBasis_UntilRescored()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        var changed = new List<ScoreItem> { I("a", "シナリオ", 40), I("b", "絵", 35), I("c", "音楽", 25) };
        var texts = T("35", "20", "25");

        // 何も触らずに保存しても、基準が変わった項目は赤いまま
        ScoreItemRules.Apply(r, changed, ScoreItemRules.Check(changed, texts), new HashSet<string>());
        Assert.Equal(new[] { "b", "c" }, ScoreItemRules.StaleItems(changed, r).OrderBy(x => x));

        // 絵だけ採点し直す（同じ値でも、触れば確認したことになる）
        ScoreItemRules.Apply(r, changed, ScoreItemRules.Check(changed, texts), new HashSet<string> { "b" });
        Assert.Equal(new[] { "c" }, ScoreItemRules.StaleItems(changed, r));
    }

    [Fact]
    public void Stale_IsEmpty_ForScoreOnlyGames()
    {
        Assert.Empty(ScoreItemRules.StaleItems(Items, new Review()));
        Assert.False(ScoreItemRules.NeedsRescore(Items, new Review()));
    }

    [Fact]
    public void Stale_FlagsOnlyChangedItems()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        Assert.Empty(ScoreItemRules.StaleItems(Items, r));

        // 絵の最高点を 30→25、音楽を 30→35 に変えた（合計は 100 のまま）
        var changed = new List<ScoreItem> { I("a", "シナリオ", 40), I("b", "絵", 25), I("c", "音楽", 35) };
        Assert.Equal(new[] { "b", "c" }, ScoreItemRules.StaleItems(changed, r).OrderBy(x => x));
        Assert.True(ScoreItemRules.NeedsRescore(changed, r));
    }

    [Fact]
    public void Stale_RenameCountsAsChange_ButIdKeepsTheScore()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        var renamed = new List<ScoreItem> { I("a", "物語", 40), I("b", "絵", 30), I("c", "音楽", 30) };
        Assert.Equal(new[] { "a" }, ScoreItemRules.StaleItems(renamed, r));
        Assert.Equal("35", ScoreItemRules.CellText(renamed[0], r));
    }

    [Fact]
    public void NewAndRemovedItems_AreDetected()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        var added = new List<ScoreItem> { I("a", "シナリオ", 40), I("b", "絵", 30), I("d", "新項目", 30) };
        Assert.Contains("d", ScoreItemRules.StaleItems(added, r));
        Assert.True(ScoreItemRules.HasRemovedItems(added, r)); // c が設定から消えた
        Assert.True(ScoreItemRules.NeedsRescore(added, r));
    }

    [Fact]
    public void TotalMismatch_DetectsScoreChangedBySyncOrManualEdit()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        Assert.False(ScoreItemRules.TotalMismatch(r, 80));
        Assert.True(ScoreItemRules.TotalMismatch(r, 85));
        Assert.True(ScoreItemRules.TotalMismatch(r, null));
        Assert.False(ScoreItemRules.TotalMismatch(new Review(), 85)); // 項目別の採点がない作品は対象外
    }

    [Fact]
    public void Review_Clone_CopiesItemScoresDeeply()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        var copy = r.Clone();
        copy.ItemScores!["a"] = 1;
        copy.ItemBasis!.Remove("a");
        Assert.Equal(35, r.ItemScores!["a"]);
        Assert.True(r.ItemBasis!.ContainsKey("a"));
    }

    [Fact]
    public void Review_ItemScores_DoNotCountAsAChangeToSendToEgs()
    {
        var a = new Review();
        var b = Scored(("a", 35), ("b", 20), ("c", 25));
        Assert.Empty(Review.Diff(a, b));
    }

    [Fact]
    public void Review_ItemScores_SurviveJsonRoundTrip()
    {
        var r = Scored(("a", 35), ("b", 20), ("c", 25));
        var json = System.Text.Json.JsonSerializer.Serialize(r);
        var back = System.Text.Json.JsonSerializer.Deserialize<Review>(json)!;
        Assert.Equal(r.ItemScores, back.ItemScores);
        Assert.Equal(r.ItemBasis, back.ItemBasis);
    }

    [Fact]
    public void Review_WithoutItemScores_LoadsFromOldJson()
    {
        var back = System.Text.Json.JsonSerializer.Deserialize<Review>("{\"Hitokoto\":\"x\"}")!;
        Assert.Null(back.ItemScores);
        Assert.Equal("x", back.Hitokoto);
    }

    // ---- 基準を最大3つ使い分ける ----

    [Fact]
    public void Review_ScoreTemplate_DefaultsToFirst_AndSurvivesJsonAndClone()
    {
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<Review>("{\"Hitokoto\":\"x\"}")!.ScoreTemplate); // 古い記録は基準1
        var r = new Review { ScoreTemplate = 2 };
        Assert.Equal(2, r.Clone().ScoreTemplate);
        Assert.Equal(2, System.Text.Json.JsonSerializer.Deserialize<Review>(System.Text.Json.JsonSerializer.Serialize(r))!.ScoreTemplate);
        Assert.Empty(Review.Diff(new Review(), r)); // 基準の番号は、批評空間に送る変更ではない
    }

    [Fact]
    public void Config_TemplateItemsAndNames_FallBackToDefaults()
    {
        var cfg = new EgsConfig();
        cfg.SetScoreItems(1, L(I("x", "A", 100)));
        Assert.Single(cfg.ScoreItemsOf(1));
        Assert.Empty(cfg.ScoreItemsOf(2));
        Assert.Equal("基準1", cfg.ScoreTemplateName(0));
        cfg.ScoreTemplateNames = ["", "ADV用", " "];
        Assert.Equal(["基準1", "ADV用", "基準3"], Enumerable.Range(0, 3).Select(cfg.ScoreTemplateName));
    }

    [Fact]
    public void Settings_UsesTheWorksOwnTemplate_AndFallsBackToTheFirstUsableOne()
    {
        var old = (ScoreItemSettings.Templates, ScoreItemSettings.Names);
        try
        {
            ScoreItemSettings.Templates = [Array.Empty<ScoreItem>(), L(I("p", "物語", 100)), L(I("q", "戦闘", 60), I("r", "演出", 40))];
            Assert.Equal([1, 2], ScoreItemSettings.UsableTemplates());
            Assert.Equal(2, ScoreItemSettings.TemplateOf(new Review { ScoreTemplate = 2 }));
            Assert.Equal(1, ScoreItemSettings.TemplateOf(new Review { ScoreTemplate = 0 })); // 基準1は未使用 → 使える最初の基準
            Assert.Equal(["物語", "戦闘", "演出"], ScoreItemSettings.AllItems().Select(i => i.Name));

            // 作品が使う基準の項目で、採点し直しの要否を判定する
            var r = new Review { ScoreTemplate = 2, ItemScores = new() { ["q"] = 50, ["r"] = 30 }, ItemBasis = new() { ["q"] = new("戦闘", 60), ["r"] = new("演出", 40) } };
            var g = new Game { Review = r };
            ScoreItemSettings.Enabled = true;
            Assert.False(g.ScoreNeedsRescore);
            r.ScoreTemplate = 1; // 別の基準の項目には、この点数の項目がない
            Assert.True(g.ScoreNeedsRescore);
        }
        finally
        {
            (ScoreItemSettings.Templates, ScoreItemSettings.Names) = old;
            ScoreItemSettings.Enabled = false;
        }
    }

    [Fact]
    public void Csv_AddsNumberedColumnsPerTemplateAtTheEnd_ByTheNumberOfItems()
    {
        var t1 = L(I("a", "シナリオ", 50), I("b", "絵", 50));
        var t2 = L();
        var t3 = L(I("c", "戦闘", 60), I("d", "演出", 20), I("e", "音", 20));
        IReadOnlyList<IReadOnlyList<ScoreItem>> templates = [t1, t2, t3];
        var g1 = new Game { Id = 1, Title = "A", Review = new Review { ItemScores = new() { ["a"] = 40, ["b"] = 30 } } };
        var g3 = new Game { Id = 2, Title = "B", Review = new Review { ScoreTemplate = 2, ItemScores = new() { ["c"] = 45, ["d"] = 10, ["e"] = 15 } } };
        var lines = CsvExporter.Build([g1, g3], false, templates).Split("\r\n");
        Assert.EndsWith("購入額(円),評価1_1(シナリオ),評価1_2(絵),評価3_1(戦闘),評価3_2(演出),評価3_3(音)", lines[0]); // 項目のない基準2の列は作らない
        Assert.EndsWith(",40,30,,,", lines[1]);
        Assert.EndsWith(",,,45,10,15", lines[2]);
        Assert.Equal(CsvExporter.ColumnNames(false, templates).Count, lines[0].Split(',').Length);
    }


    // ---- CSV ----

    [Fact]
    public void Csv_AppendsItemColumnsAfterPrice_OnlyWhenItemsGiven()
    {
        var g = new Game { Id = 1, Title = "T", MyScore = 80, Review = Scored(("a", 35), ("b", 20), ("c", 25)) };
        var without = CsvExporter.Build([g]).Split("\r\n")[0];
        Assert.EndsWith("購入額(円)", without);

        var with = CsvExporter.Build([g], false, [Items]).Split("\r\n");
        Assert.EndsWith("購入額(円),評価1_1(シナリオ),評価1_2(絵),評価1_3(音楽)", with[0]);
        Assert.EndsWith(",35,20,25", with[1]);
    }

    [Fact]
    public void Csv_ItemColumns_AreBlankForScoreOnlyGames()
    {
        var g = new Game { Id = 1, Title = "T", MyScore = 80 };
        var line = CsvExporter.Build([g], false, [Items]).Split("\r\n")[1];
        Assert.EndsWith(",,,", line);
    }

    // ---- 基準点 ----

    private static List<ScoreItem> WithBase(int? a, int? b, int? c) =>
        L(new() { Id = "a", Name = "シナリオ", Max = 40, Base = a }, new() { Id = "b", Name = "絵", Max = 30, Base = b }, new() { Id = "c", Name = "音楽", Max = 30, Base = c });

    [Fact]
    public void Base_IsOptional_AndValidatedWithinZeroToMax()
    {
        Assert.Null(ScoreItemRules.ValidateTemplate(WithBase(null, null, null)));
        Assert.Null(ScoreItemRules.ValidateTemplate(WithBase(28, 0, 30)));
        Assert.NotNull(ScoreItemRules.ValidateTemplate(WithBase(41, null, null))); // 最高点を超える
        Assert.NotNull(ScoreItemRules.ValidateTemplate(WithBase(-1, null, null))); // 数でない入力は -1 として渡る
    }

    [Fact]
    public void BaseFill_FillsOnlyEmptyItemsThatHaveABase_AndNeverOverwrites()
    {
        var items = WithBase(28, null, 21);
        var fill = ScoreItemRules.BaseFill(items, T(a: "", b: "", c: "5"));
        Assert.Equal(new Dictionary<string, int> { ["a"] = 28 }, fill); // b は基準点なし、c は入力済み
        Assert.Empty(ScoreItemRules.BaseFill(WithBase(null, null, null), T()));
        Assert.Equal(2, ScoreItemRules.BaseFill(items, T()).Count);
    }

    [Fact]
    public void BaseFill_TreatsInvalidTextAsEntered()
    {
        Assert.Empty(ScoreItemRules.BaseFill(WithBase(28, null, null), T(a: "abc")));
    }

    [Fact]
    public void BaseSum_AndHasBase()
    {
        Assert.False(ScoreItemRules.HasBase(WithBase(null, null, null)));
        Assert.True(ScoreItemRules.HasBase(WithBase(null, 3, null)));
        Assert.Equal(49, ScoreItemRules.BaseSum(WithBase(28, null, 21)));
    }

    [Fact]
    public void Base_SurvivesConfigJson_AndDoesNotMakeAnOldScoreStale()
    {
        var cfg = new EgsConfig();
        cfg.SetScoreItems(0, WithBase(28, null, 21));
        var back = System.Text.Json.JsonSerializer.Deserialize<EgsConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Assert.Equal(new int?[] { 28, null, 21 }, back.ScoreItemsOf(0).Select(i => i.Base).ToArray());

        // 基準点を変えても、採点済みの作品は「採点し直し」にならない
        var review = new Review();
        var items = Items;
        ScoreItemRules.Apply(review, items, ScoreItemRules.Check(items, T("30", "20", "10")));
        Assert.False(ScoreItemRules.NeedsRescore(WithBase(28, 20, 21), review));
    }
}
