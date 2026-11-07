using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>任意評価基準の大項目・小項目（2階層）と、調整点（加点・減点）のテスト</summary>
public class ScoreItemGroupTests
{
    private static ScoreItem I(string id, string name, int max, int? basePoint = null) => new() { Id = id, Name = name, Max = max, Base = basePoint };

    private static ScoreItem G(string id, string name, params ScoreItem[] children) => new() { Id = id, Name = name, Children = children.ToList() };

    private static ScoreItem A(string id, int min, int max, int? basePoint = null) => new() { Id = id, Name = "調整点", Adjust = true, Min = min, Max = max, Base = basePoint };

    /// <summary>利用者が使いたい例（物語 20／キャラ 40／H 10／表現 20／快適性 10 と、調整点）</summary>
    private static List<ScoreItem> Example() =>
    [
        G("story", "物語", I("s1", "日常・掛け合い", 10), I("s2", "締め方", 10)),
        G("chara", "キャラ", I("c1", "魅力", 15), I("c2", "恋愛描写", 10), I("c3", "音声", 5), I("c4", "個別ルート", 10)),
        I("h", "H", 10),
        G("art", "表現", I("e1", "絵", 10), I("e2", "音楽", 10)),
        G("comfort", "快適性", I("k1", "システム・テンポ", 10)),
        A("adj", -10, 10),
    ];

    private static Dictionary<string, string> Texts(params (string Id, string Text)[] t) => t.ToDictionary(x => x.Id, x => x.Text);

    /// <summary>例の基準で、調整点のほかを全部入力した文字（合計 70）</summary>
    private static Dictionary<string, string> Filled(string adjust = "") => Texts(
        ("s1", "8"), ("s2", "7"), ("c1", "12"), ("c2", "8"), ("c3", "4"), ("c4", "7"), ("h", "6"), ("e1", "8"), ("e2", "5"), ("k1", "5"), ("adj", adjust));

    // ---- 項目の設定 ----

    [Fact]
    public void Example_IsValid_AndGroupMaxIsTheSumOfChildren()
    {
        var t = Example();
        Assert.Null(ScoreItemRules.ValidateTemplate(t));
        Assert.Equal(100, ScoreItemRules.Sum(t)); // 調整点は 100 点の内訳に入れない
        Assert.Equal(40, ScoreItemRules.GroupMax(t[1]));
        Assert.Equal(10, ScoreItemRules.GroupMax(t[2])); // 小項目のない項目は自分の最高点
        Assert.Equal(
            ["物語/日常・掛け合い", "物語/締め方", "キャラ/魅力", "キャラ/恋愛描写", "キャラ/音声", "キャラ/個別ルート", "H", "表現/絵", "表現/音楽", "快適性/システム・テンポ", "調整点"],
            ScoreItemRules.Fields(t).Select(f => f.Label));
        Assert.Equal(10, ScoreItemRules.Leaves(t).Count);
        Assert.Equal("adj", ScoreItemRules.AdjustField(t)!.Id);
    }

    [Fact]
    public void Template_GroupMaxStoredInSettingIsIgnored()
    {
        var t = Example();
        t[1].Max = 999; // 設定ファイルに書かれた大項目の最高点は使わない
        Assert.Null(ScoreItemRules.ValidateTemplate(t));
        Assert.Equal(40, ScoreItemRules.GroupMax(t[1]));
    }

    [Fact]
    public void Template_SameChildNameUnderDifferentGroups_IsAllowed_ButNotInTheSameGroup()
    {
        Assert.Null(ScoreItemRules.ValidateTemplate([G("a", "物語", I("a1", "その他", 50)), G("b", "キャラ", I("b1", "その他", 50))]));
        Assert.Contains("同じ名前", ScoreItemRules.ValidateTemplate([G("a", "物語", I("a1", "その他", 50), I("a2", "その他 ", 50))]));
        Assert.Contains("空", ScoreItemRules.ValidateTemplate([G("a", " ", I("a1", "X", 100))]));
        Assert.Contains("空", ScoreItemRules.ValidateTemplate([G("a", "物語", I("a1", "", 100))]));
    }

    [Fact]
    public void Template_RejectsBrokenShapes_WithoutThrowing()
    {
        Assert.Contains("壊れて", ScoreItemRules.ValidateTemplate([I("a", "A", 100), null!]));
        Assert.Contains("壊れて", ScoreItemRules.ValidateTemplate([new ScoreItem { Id = "a", Name = "A", Children = [null!] }]));
        Assert.Contains("壊れて", ScoreItemRules.ValidateTemplate([I("a", "A", 50), I("a", "B", 50)])); // 同じ ID
        Assert.Contains("壊れて", ScoreItemRules.ValidateTemplate([G("a", "A", I("a", "B", 100))]));
        Assert.Contains("壊れて", ScoreItemRules.ValidateTemplate([new ScoreItem { Id = null!, Name = "A", Max = 100 }]));
        Assert.Contains("小項目の下", ScoreItemRules.ValidateTemplate([G("a", "A", G("b", "B", I("c", "C", 100)))]));
        Assert.Contains("小項目には置けません", ScoreItemRules.ValidateTemplate([G("a", "A", I("b", "B", 100), A("x", -5, 5))]));
        Assert.Contains("小項目を付けられません", ScoreItemRules.ValidateTemplate([I("b", "B", 100), new ScoreItem { Id = "x", Name = "調整", Adjust = true, Children = [I("c", "C", 5)] }]));
        Assert.Contains("1 つまで", ScoreItemRules.ValidateTemplate([I("b", "B", 100), A("x", -5, 5), A("y", -5, 5)]));
        Assert.NotNull(ScoreItemRules.ValidateTemplate(null));
    }

    [Fact]
    public void Template_OnlyAdjust_IsRejected()
    {
        Assert.Contains("点数を入れる項目がありません", ScoreItemRules.ValidateTemplate([A("x", -10, 10)]));
    }

    [Theory]
    [InlineData(-10, 10, true)]
    [InlineData(0, 5, true)]
    [InlineData(-5, 0, true)]
    [InlineData(-100, 100, true)]
    [InlineData(1, 10, false)]   // 下限が 0 より大きい
    [InlineData(-10, -1, false)] // 上限が 0 より小さい
    [InlineData(0, 0, false)]    // 範囲がない
    [InlineData(-101, 10, false)]
    [InlineData(-10, 101, false)]
    public void Template_AdjustRange(int min, int max, bool ok)
    {
        var error = ScoreItemRules.ValidateTemplate([I("b", "B", 100), A("x", min, max)]);
        if (ok) Assert.Null(error);
        else Assert.Contains("調整点の範囲", error);
    }

    [Fact]
    public void Template_AdjustBase_MustBeInsideRange_AndCanBeNegative()
    {
        Assert.Null(ScoreItemRules.ValidateTemplate([I("b", "B", 100), A("x", -10, 10, -3)]));
        Assert.Contains("調整点の基準点", ScoreItemRules.ValidateTemplate([I("b", "B", 100), A("x", -10, 10, 11)]));
    }

    // ---- 作品ごとの採点 ----

    [Fact]
    public void Check_BlankAdjust_CountsAsZero()
    {
        var c = ScoreItemRules.Check(Example(), Filled());
        Assert.True(c.Ok);
        Assert.Equal(70, c.Total);
        Assert.False(c.Scores.ContainsKey("adj")); // 空欄の調整点は点数として残さない
        Assert.DoesNotContain(c.Scores.Keys, id => id is "story" or "chara" or "art" or "comfort"); // 大項目には点数を持たせない
    }

    [Fact]
    public void Check_AdjustAddsOrSubtracts()
    {
        Assert.Equal(75, ScoreItemRules.Check(Example(), Filled("5")).Total);
        Assert.Equal(62, ScoreItemRules.Check(Example(), Filled("-8")).Total);
        Assert.Equal(80, ScoreItemRules.Check(Example(), Filled("+10")).Total);
    }

    [Fact]
    public void Check_AdjustOutOfRange_IsAnError()
    {
        var c = ScoreItemRules.Check(Example(), Filled("11"));
        Assert.Equal("入力エラー。-10～10点の範囲で入力してください。", c.Errors["adj"]);
        Assert.Null(c.Total);
    }

    [Fact]
    public void Check_TotalOutside0To100_IsAnErrorOnTheAdjust()
    {
        var t = new List<ScoreItem> { I("a", "A", 100), A("x", -20, 20) };
        var over = ScoreItemRules.Check(t, Texts(("a", "95"), ("x", "10")));
        Assert.Contains("合計が 0～100点", over.Errors["x"]);
        Assert.Null(over.Total);
        var under = ScoreItemRules.Check(t, Texts(("a", "5"), ("x", "-10")));
        Assert.Contains("合計が 0～100点", under.Errors["x"]);
        Assert.Equal(100, ScoreItemRules.Check(t, Texts(("a", "90"), ("x", "10"))).Total);
    }

    [Fact]
    public void Check_OnlyAdjustEntered_NeedsTheOtherFields()
    {
        var c = ScoreItemRules.Check(Example(), Texts(("adj", "5")));
        Assert.True(c.AnyEntered);
        Assert.False(c.Ok);
        Assert.Equal(10, c.Errors.Count); // 調整点のほかの 10 欄
        Assert.False(c.Errors.ContainsKey("adj"));
    }

    [Fact]
    public void Check_AllBlank_MeansNoItemScores() =>
        Assert.False(ScoreItemRules.Check(Example(), new Dictionary<string, string>()).AnyEntered);

    [Fact]
    public void GroupSubtotal_SumsChildrenScores()
    {
        var t = Example();
        var c = ScoreItemRules.Check(t, Filled());
        Assert.Equal(31, ScoreItemRules.GroupSubtotal(t[1], c.Scores));
        Assert.Equal(15, ScoreItemRules.GroupSubtotal(t[0], c.Scores));
    }

    [Fact]
    public void BaseFill_CoversChildrenAndAdjust()
    {
        var t = new List<ScoreItem> { G("g", "物語", I("a", "A", 60, 30), I("b", "B", 40)), A("x", -5, 5, 0) };
        Assert.Equal(new Dictionary<string, int> { ["a"] = 30, ["x"] = 0 }, ScoreItemRules.BaseFill(t, Texts(("a", ""), ("b", ""))));
        Assert.True(ScoreItemRules.HasBase(t));
        Assert.Equal(30, ScoreItemRules.BaseSum(t));
    }

    // ---- 記録と、基準が変わったときの判定 ----

    private static Review Scored(List<ScoreItem> t, Dictionary<string, string> texts)
    {
        var r = new Review();
        ScoreItemRules.Apply(r, t, ScoreItemRules.Check(t, texts));
        return r;
    }

    [Fact]
    public void Apply_StoresChildrenAndAdjust_WithFullNames()
    {
        var r = Scored(Example(), Filled("-3"));
        Assert.Equal(67, r.ItemScores!.Values.Sum()); // 合計（調整点も含む）が得点になる
        Assert.False(r.ItemScores.ContainsKey("chara"));
        Assert.Equal(new ScoreBasis("キャラ/魅力", 15), r.ItemBasis!["c1"]);
        Assert.Equal(new ScoreBasis("H", 10), r.ItemBasis["h"]);
        Assert.Equal(new ScoreBasis("調整点", 10, -10), r.ItemBasis["adj"]);
        Assert.False(ScoreItemRules.NeedsRescore(Example(), r));
        Assert.False(ScoreItemRules.TotalMismatch(r, 67));
    }

    [Fact]
    public void AddingAdjustLater_DoesNotAskToRescore()
    {
        var before = new List<ScoreItem> { I("a", "シナリオ", 60), I("b", "絵", 40) };
        var r = Scored(before, Texts(("a", "50"), ("b", "30")));
        var after = new List<ScoreItem> { I("a", "シナリオ", 60), I("b", "絵", 40), A("x", -10, 10) };
        Assert.False(ScoreItemRules.NeedsRescore(after, r));
        Assert.False(ScoreItemRules.TotalMismatch(r, 80));
    }

    [Fact]
    public void ChangingAdjustRange_AsksToRescore_OnlyWhenTheGameHasAnAdjustScore()
    {
        var t = new List<ScoreItem> { I("a", "A", 100), A("x", -10, 10) };
        var r = Scored(t, Texts(("a", "70"), ("x", "5")));
        var wider = new List<ScoreItem> { I("a", "A", 100), A("x", -20, 20) };
        Assert.Equal(["x"], ScoreItemRules.StaleItems(wider, r));
    }

    [Fact]
    public void ChangingAdjustRange_DoesNotAskToRescore_WhenTheAdjustWasLeftBlank()
    {
        var t = new List<ScoreItem> { I("a", "A", 100), A("x", -10, 10) };
        var r = Scored(t, Texts(("a", "70"), ("x", "")));
        Assert.Equal(70, r.ItemScores!.Values.Sum());
        var narrower = new List<ScoreItem> { I("a", "A", 100), A("x", -5, 5) };
        Assert.False(ScoreItemRules.NeedsRescore(narrower, r));
    }

    [Fact]
    public void RenamingGroup_MarksItsChildrenStale()
    {
        var r = Scored(Example(), Filled());
        var t = Example();
        t[0].Name = "シナリオ";
        Assert.Equal(["s1", "s2"], ScoreItemRules.StaleItems(t, r).OrderBy(x => x));
    }

    [Fact]
    public void TurningAnItemIntoAGroup_LeavesTheOldScoreAsRemoved()
    {
        var before = new List<ScoreItem> { I("a", "キャラ", 60), I("b", "絵", 40) };
        var r = Scored(before, Texts(("a", "50"), ("b", "30")));
        var after = new List<ScoreItem> { G("a", "キャラ", I("a1", "魅力", 30), I("a2", "音声", 30)), I("b", "絵", 40) };
        Assert.True(ScoreItemRules.HasRemovedItems(after, r)); // 大項目 a の点数は、もう入れる欄がない
        Assert.True(ScoreItemRules.NeedsRescore(after, r));
        Assert.Equal(["a1", "a2"], ScoreItemRules.StaleItems(after, r).OrderBy(x => x));
    }

    // ---- 前の版の設定・記録との互換 ----

    [Fact]
    public void OldFlatConfig_AndOldReview_StillWork()
    {
        // 前の版の設定ファイル（Children・Adjust・Min がない）
        var cfg = System.Text.Json.JsonSerializer.Deserialize<EgsConfig>(
            """{ "ScoreItemsEnabled": true, "ScoreItems": [ { "Id": "a", "Name": "シナリオ", "Max": 40 }, { "Id": "b", "Name": "絵", "Max": 30, "Base": 15 }, { "Id": "c", "Name": "音楽", "Max": 30 } ] }""")!;
        Assert.Null(ScoreItemRules.ValidateTemplate(cfg.ScoreItems));
        Assert.Equal(["a", "b", "c"], ScoreItemRules.Fields(cfg.ScoreItems).Select(f => f.Id));
        Assert.Equal(["シナリオ", "絵", "音楽"], ScoreItemRules.Fields(cfg.ScoreItems).Select(f => f.Label));
        Assert.All(cfg.ScoreItems, i => Assert.False(i.IsGroup || i.Adjust));

        // 前の版で採点した記録（ScoreBasis に Min がない）
        var review = System.Text.Json.JsonSerializer.Deserialize<Review>(
            """{ "ItemScores": { "a": 35, "b": 20, "c": 25 }, "ItemBasis": { "a": { "Name": "シナリオ", "Max": 40 }, "b": { "Name": "絵", "Max": 30 }, "c": { "Name": "音楽", "Max": 30 } } }""")!;
        Assert.Equal(new ScoreBasis("絵", 30), review.ItemBasis!["b"]);
        Assert.False(ScoreItemRules.NeedsRescore(cfg.ScoreItems, review));
        Assert.False(ScoreItemRules.TotalMismatch(review, 80));
        Assert.Equal("35", ScoreItemRules.CellText(cfg.ScoreItems[0], review));
    }

    [Fact]
    public void GroupedConfig_SurvivesJsonRoundTrip()
    {
        var cfg = new EgsConfig { ScoreItems = Example() };
        var back = System.Text.Json.JsonSerializer.Deserialize<EgsConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!;
        Assert.Null(ScoreItemRules.ValidateTemplate(back.ScoreItems));
        Assert.Equal(ScoreItemRules.Fields(Example()).Select(f => (f.Id, f.Label, f.Min, f.Max)),
            ScoreItemRules.Fields(back.ScoreItems).Select(f => (f.Id, f.Label, f.Min, f.Max)));
    }

    // ---- CSV ----

    [Fact]
    public void Csv_HasOneColumnPerField_WithGroupNames_AndAnAdjustColumn()
    {
        var t = new List<ScoreItem> { G("g", "キャラ", I("a", "魅力", 60), I("b", "音声", 20)), I("h", "H", 20), A("x", -10, 10) };
        var g = new Game { Id = 1, Title = "T", Review = Scored(t, Texts(("a", "50"), ("b", "10"), ("h", "15"), ("x", "-5"))) };
        var lines = CsvExporter.Build([g], false, [t]).Split("\r\n");
        Assert.EndsWith("購入額(円),評価1_1(キャラ/魅力),評価1_2(キャラ/音声),評価1_3(H),評価1_4(調整点)", lines[0]);
        Assert.EndsWith(",50,10,15,'-5", lines[1]); // マイナスは、ほかの欄と同じく ' を付けて書く（表計算ソフトが式として読まないように）
    }
}
