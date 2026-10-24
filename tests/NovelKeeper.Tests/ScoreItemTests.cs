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

    // ---- CSV ----

    [Fact]
    public void Csv_AppendsItemColumnsAfterPrice_OnlyWhenItemsGiven()
    {
        var g = new Game { Id = 1, Title = "T", MyScore = 80, Review = Scored(("a", 35), ("b", 20), ("c", 25)) };
        var without = CsvExporter.Build([g]).Split("\r\n")[0];
        Assert.EndsWith("購入額(円)", without);

        var with = CsvExporter.Build([g], false, Items).Split("\r\n");
        Assert.EndsWith("購入額(円),シナリオ(40),絵(30),音楽(30)", with[0]);
        Assert.EndsWith(",35,20,25", with[1]);
    }

    [Fact]
    public void Csv_ItemColumns_AreBlankForScoreOnlyGames()
    {
        var g = new Game { Id = 1, Title = "T", MyScore = 80 };
        var line = CsvExporter.Build([g], false, Items).Split("\r\n")[1];
        Assert.EndsWith(",,,", line);
    }
}
