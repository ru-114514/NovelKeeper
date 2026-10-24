using System.Text.Json;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>投稿設定のレイアウト（パネルの置き場所）のテスト</summary>
public class PostLayoutTests
{
    private static PostPanel P(PostPanelKind kind, int row, int col, int span = 1, int rowSpan = 1) =>
        new() { Kind = kind, Row = row, Col = col, Span = span, RowSpan = rowSpan };

    private static void AssertNoOverlap(PostLayout l)
    {
        for (var i = 0; i < l.Panels.Count; i++)
            for (var j = i + 1; j < l.Panels.Count; j++)
            {
                var a = l.Panels[i]; var b = l.Panels[j];
                var overlap = a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan && a.Col < b.Col + b.Span && b.Col < a.Col + a.Span;
                Assert.False(overlap, $"パネルが重なっている：{a.Kind} と {b.Kind}");
            }
    }

    [Fact]
    public void Default_Has2Columns_TitleOnFirstRowAcrossAllColumns_AndSixPanelsBelow()
    {
        var l = PostLayout.CreateDefault();
        Assert.Equal(2, l.Columns);
        Assert.Equal(PostLayout.CurrentVersion, l.Version);
        Assert.Equal(7, l.Panels.Count);
        var t = l.Panels[0];
        Assert.Equal((PostPanelKind.Title, 0, 0, 2, 2), (t.Kind, t.Row, t.Col, t.Span, t.RowSpan));
        Assert.Equal(new[] { (2, 0), (2, 1), (4, 0), (4, 1), (6, 0), (6, 1) }, l.Panels.Skip(1).Select(p => (p.Row, p.Col)));
        AssertNoOverlap(l);
    }

    [Fact]
    public void Add_PutsNewPanelAtTopmostLeftmostFreeSlot_IncludingNextToTallPanels()
    {
        var l = PostLayout.CreateDefault(); // 作品（3,1）は縦に何段もまたぐ
        var works = l.Panels.Single(p => p.Kind == PostPanelKind.Works);
        Assert.True(works.RowSpan > 2);

        var a = l.Add(PostPanelKind.DaysPlayed);
        Assert.Equal((8, 0), (a.Row, a.Col)); // 作品の隣の、空いた段（ブランドの下）
        var b = l.Add(PostPanelKind.PeakHour);
        Assert.Equal((10, 0), (b.Row, b.Col));

        l.Panels.Remove(l.Panels.Single(p => p.Kind == PostPanelKind.Score)); // (2,0) を空ける
        var c = l.Add(PostPanelKind.Score, rowSpan: 1);
        Assert.Equal((4, 0), (c.Row, c.Col)); // 上の段の空きを優先する
        AssertNoOverlap(l);
    }

    [Fact]
    public void Add_SkipsGapsThatAreTooSmall()
    {
        var l = new PostLayout { Columns = 3 };
        l.Panels.Add(P(PostPanelKind.Score, 0, 1));                  // 0段目の左と右が1つずつ空く
        Assert.Equal((1, 0), l.FindSlot(2));                         // 横2は0段目に入らない
        Assert.Equal((0, 0), l.FindSlot(1));
        l.Panels.Add(P(PostPanelKind.Score, 0, 0));
        l.Panels.Add(P(PostPanelKind.Score, 0, 2));                  // 0段目は埋まった
        l.Panels.Add(P(PostPanelKind.Score, 1, 0));
        Assert.Equal((1, 1), l.FindSlot(1));
        Assert.Equal((1, 1), l.FindSlot(1, rowSpan: 3));             // 縦3も、(1,1)から3段続けて空いている
        Assert.Equal((1, 1), l.FindSlot(2));                         // 横2は、1段目の右2つが空いている
        l.Panels.Add(P(PostPanelKind.Score, 1, 1));
        Assert.Equal((2, 0), l.FindSlot(2));                         // それも埋まると、次の段
    }

    [Fact]
    public void TallPanel_NeedsAllItsRowsFree()
    {
        var l = new PostLayout { Columns = 2 };
        l.Panels.Add(P(PostPanelKind.Score, 1, 0)); // 左の2段目だけ埋まっている
        Assert.Equal((0, 1), l.FindSlot(1, rowSpan: 3)); // 左の0段目から縦3は入らない → 右の0段目
        l.Panels.Add(P(PostPanelKind.Score, 2, 1));
        Assert.Equal((2, 0), l.FindSlot(1, rowSpan: 3)); // 左は、2段目から3段続けて空いている
        l.Panels.Add(P(PostPanelKind.Score, 4, 0));
        Assert.Equal((3, 1), l.FindSlot(1, rowSpan: 3)); // 左が塞がると、右の3段目から3段続けて空いている
    }

    [Fact]
    public void TryPlace_RejectsOverlap_OverflowAndBadSpan_AndLeavesPanelUnchanged()
    {
        var l = PostLayout.CreateDefault();
        var p = l.Panels[1]; // 総プレイ時間（1,0）
        Assert.NotNull(l.TryPlace(p, 2, 1, 1));  // クリア数と重なる
        Assert.NotNull(l.TryPlace(p, 2, 1, 2));  // はみ出す
        Assert.NotNull(l.TryPlace(p, 2, 0, 3));  // 2カラムなのに横幅3
        Assert.NotNull(l.TryPlace(p, 2, 0, 2));  // 隣と重なる
        Assert.NotNull(l.TryPlace(p, 8, 1, 1));  // 作品（縦に長い）と重なる
        Assert.NotNull(l.TryPlace(p, 0, 0, 1, rowSpan: 41)); // 縦幅が大きすぎる
        Assert.Equal((2, 0, 1, 2), (p.Row, p.Col, p.Span, p.RowSpan));

        Assert.Null(l.TryPlace(p, 40, 0, 2, rowSpan: 2));
        Assert.Equal((40, 0, 2, 2), (p.Row, p.Col, p.Span, p.RowSpan));
    }

    [Fact]
    public void TryPlace_AllowsStayingInPlace_AndGrowingIntoFreeRows()
    {
        var l = PostLayout.CreateDefault();
        var brand = l.Panels.Single(p => p.Kind == PostPanelKind.BestBrand); // (3,0) の下は空いている
        Assert.Null(l.TryPlace(brand, brand.Row, brand.Col, brand.Span));
        Assert.Null(l.TryPlace(brand, brand.Row, brand.Col, brand.Span, rowSpan: 4));
        Assert.Equal(4, brand.RowSpan);
        Assert.NotNull(l.TryPlace(brand, 4, 0, 1, rowSpan: 2)); // 上へ伸ばすと、点数と重なる
    }

    [Fact]
    public void TitlePanel_IsAlwaysFullWidth_AtColumnZero()
    {
        var l = PostLayout.CreateDefault();
        var t = l.Panels[0];
        l.SetColumns(4);
        Assert.Equal((0, 0, 4), (t.Row, t.Col, t.Span));
        l.SetColumns(3);
        Assert.Equal(3, t.Span);
        Assert.Null(l.TryPlace(t, 0, 2, 1)); // 指定しても、全カラム幅・左端になる
        Assert.Equal((0, 3), (t.Col, t.Span));
        AssertNoOverlap(l);
    }

    [Fact]
    public void TitlePanel_CannotBeMoved_OrSwappedWithOthers_ButCanChangeItsHeight()
    {
        var l = PostLayout.CreateDefault();
        var t = l.Panels[0];
        Assert.NotNull(l.TryMove(t, 5, 0));
        Assert.Equal(0, t.Row);
        l.PlanMove(t, 5, 0, out var ok);
        Assert.False(ok);

        Assert.Null(l.TryPlace(t, 7, 1, 1, rowSpan: 1)); // 段・列を指定しても、場所は変わらない
        Assert.Equal((0, 0), (t.Row, t.Col));

        // 同じ大きさ（全カラム幅・1段）の別のパネルが、タイトルと入れ替わることはない
        var wide = l.Add(PostPanelKind.Memo, rowSpan: 1);
        l.TryPlace(wide, 30, 0, l.Columns);
        Assert.NotNull(l.TryMove(wide, 0, 0));
        Assert.Equal(30, wide.Row);
        Assert.Equal(0, t.Row);

        Assert.Null(l.TryPlace(t, 0, 0, l.Columns, rowSpan: 1)); // 縦幅は、そのまま変えられる
    }

    [Fact]
    public void TryMove_GoesToFreeCellNextToATallPanel_FromFarBelow()
    {
        var l = PostLayout.CreateDefault();
        var p = l.Add(PostPanelKind.DaysPlayed);   // 左の列、作品の隣の空き
        Assert.Null(l.TryMove(p, 12, 0));          // いったん下へ
        Assert.Equal((12, 0), (p.Row, p.Col));
        Assert.Null(l.TryMove(p, 6, 0));           // 作品の隣（左の列）の空いた段へ、戻せる
        Assert.Equal((6, 0), (p.Row, p.Col));
    }

    [Fact]
    public void TryMove_SwapsSameSize_AndRejectsOtherOverlaps()
    {
        var l = PostLayout.CreateDefault();
        var a = l.Panels[1]; // 総プレイ時間（1,0）
        var b = l.Panels[2]; // クリア数（1,1）
        Assert.Null(l.TryMove(a, 2, 1));                // 同じ大きさのパネルの場所へ → 入れ替え
        Assert.Equal((2, 1), (a.Row, a.Col));
        Assert.Equal((2, 0), (b.Row, b.Col));

        AssertNoOverlap(l);
    }

    [Fact]
    public void TryMove_OntoDifferentSizedPanel_SwapsOrShifts_WithoutOverlap()
    {
        var l = PostLayout.CreateDefault();
        var a = l.Panels[1]; // 総プレイ時間（1,0）1x1
        var works = l.Panels.Single(p => p.Kind == PostPanelKind.Works); // 縦長
        var ra = (a.Row, a.Col);
        Assert.Null(l.TryMove(a, works.Row + 1, works.Col)); // 縦長の作品の上へ → 置ける（入れ替えかずらし）
        Assert.Equal(works.Col, a.Col);
        AssertNoOverlap(l);
        Assert.NotEqual(ra, (a.Row, a.Col));
    }

    [Fact]
    public void TryResize_PushesPanelsBelowDown_WithoutOverlap()
    {
        var l = PostLayout.CreateDefault();
        var a = l.Panels[1]; // 総プレイ時間（1,0）
        var below = l.Panels.Single(p => p.Kind == PostPanelKind.Score); // その下（2,0）
        Assert.Null(l.TryResize(a, 6));
        Assert.Equal(6, a.RowSpan);
        Assert.True(below.Row >= a.Row + 6);
        AssertNoOverlap(l);
    }

    [Fact]
    public void TryMove_OntoPanel_ShiftsDownwardsInCascade_AndNeverTouchesTitle()
    {
        var l = new PostLayout { Columns = 2 };
        var a = P(PostPanelKind.TotalTime, 0, 0); var b = P(PostPanelKind.ClearCount, 1, 0); var c = P(PostPanelKind.Score, 2, 0);
        var d = P(PostPanelKind.DaysPlayed, 3, 1);
        b.RowSpan = 2; // b は縦2段（行1〜2）で c と接しないよう調整
        c.Row = 3; c.Col = 0;
        var e = P(PostPanelKind.AvgPerDay, 4, 1); // 入れ替え先（d の場所）に b が収まらないようにする
        l.Panels.AddRange([a, b, c, d, e]);
        Assert.Null(l.TryMove(d, 1, 0)); // b の位置へ。大きさが違うので、b は下へずれ、c もずれる
        AssertNoOverlap(l);
        Assert.Equal((1, 0), (d.Row, d.Col));
        Assert.True(b.Row >= 2 && c.Row >= b.Row + b.RowSpan);

        var l2 = PostLayout.CreateDefault();
        var t = l2.Panels[0];
        var other = l2.Panels[1];
        Assert.NotNull(l2.TryMove(other, 0, 0)); // タイトルの上には置けない
        Assert.Equal(0, t.Row);
    }

    [Fact]
    public void TryMove_ClampsColumnSoWidePanelsStayInside()
    {
        var l = new PostLayout { Columns = 3 };
        var p = P(PostPanelKind.Works, 0, 0, span: 2);
        l.Panels.Add(p);
        Assert.Null(l.TryMove(p, 2, 2)); // 列2から横2はみ出す → 列1に寄せる
        Assert.Equal((2, 1), (p.Row, p.Col));
    }

    [Fact]
    public void SetColumns_ShrinksSpans_AndRePlacesOverflowingPanels_WithoutOverlap()
    {
        var l = new PostLayout { Columns = 4 };
        l.Panels.Add(P(PostPanelKind.TotalTime, 0, 0));
        l.Panels.Add(P(PostPanelKind.ClearCount, 0, 3));             // 2カラムにするとはみ出す
        l.Panels.Add(P(PostPanelKind.Works, 1, 0, span: 4, rowSpan: 3)); // 幅を縮める
        l.SetColumns(2);

        Assert.Equal(2, l.Columns);
        Assert.All(l.Panels, p => Assert.InRange(p.Col + p.Span, 1, 2));
        Assert.Equal(2, l.Panels[2].Span);
        AssertNoOverlap(l);
        Assert.Equal((0, 1), (l.Panels[1].Row, l.Panels[1].Col)); // 空いている最も上・左へ
    }

    [Fact]
    public void SetColumns_ClampsToTwoToFour()
    {
        var l = PostLayout.CreateDefault();
        l.SetColumns(9);
        Assert.Equal(4, l.Columns);
        l.SetColumns(0);
        Assert.Equal(2, l.Columns);
    }

    [Fact]
    public void DisplayTitle_FallsBackToDefaultName_WhenBlank()
    {
        var p = PostLayout.NewPanel(PostPanelKind.BestBrand);
        Assert.Equal("マイベストブランド", p.DisplayTitle);
        p.Title = "  ";
        Assert.Equal("マイベストブランド", p.DisplayTitle);
        p.Title = " 推しの会社 ";
        Assert.Equal("推しの会社", p.DisplayTitle);
        Assert.Equal("ノベルゲーム　プレイレポート", PostLayout.NewPanel(PostPanelKind.Title).DisplayTitle);
    }

    [Fact]
    public void Normalize_FixesBrokenValues()
    {
        var l = new PostLayout { Columns = 3, Version = PostLayout.CurrentVersion };
        var a = new PostPanel { Kind = PostPanelKind.Priority, Row = 0, Col = 0, Span = 9, Height = -5, RowSpan = 99, ItemCount = 99, ItemColumns = 0, FontSize = 3 };
        a.GameIds.AddRange(Enumerable.Range(1, 20).Select(i => (long)i));
        l.Panels.Add(a);
        l.Panels.Add(new PostPanel { Kind = PostPanelKind.Works, Row = 0, Col = 0, Span = 1, ItemCount = 99 });
        l.Normalize();

        Assert.Equal(3, a.Span);
        Assert.Equal(0, a.Height);
        Assert.Equal(PostLayout.MaxRowSpan, a.RowSpan);
        Assert.Equal(8, a.FontSize);
        Assert.Equal(PostLayout.MaxPriorityGames, a.ItemCount);
        Assert.Equal(0, a.ItemColumns); // 0 は自動
        Assert.Equal(PostLayout.MaxPriorityGames, a.GameIds.Count);
        Assert.Equal(PostLayout.MaxWorks, l.Panels[1].ItemCount);
        AssertNoOverlap(l);
    }

    [Fact]
    public void Migrate_FromOldLayout_AddsTitlePanel_ShiftsRows_AndSizesPanels()
    {
        var old = new PostLayout { Columns = 4, Version = 0 };
        old.Panels.Add(new PostPanel { Kind = PostPanelKind.TotalTime, Row = 0, Col = 0 });
        old.Panels.Add(new PostPanel { Kind = PostPanelKind.Works, Row = 1, Col = 0, Span = 4, ItemCount = 12, ItemColumns = 4 });
        old.Panels.Add(new PostPanel { Kind = PostPanelKind.DailyChart, Row = 2, Col = 0 });

        old.MigrateToCurrent("私のレポート");
        old.Normalize();

        Assert.Equal(PostLayout.CurrentVersion, old.Version);
        var t = old.Panels[0];
        Assert.Equal((PostPanelKind.Title, "私のレポート", 0, 0, 4), (t.Kind, t.Title, t.Row, t.Col, t.Span));
        Assert.Equal(2, old.Panels[1].Row); // タイトルの分だけ下がる
        Assert.True(old.Panels[2].RowSpan > 2);   // 作品は、中身に合わせた段数になる
        Assert.Equal(4, old.Panels[3].RowSpan);   // グラフ
        AssertNoOverlap(old);

        old.MigrateToCurrent("もう一度"); // 2回目は何もしない
        Assert.Equal(4, old.Panels.Count);
    }

    [Fact]
    public void Migrate_FromVersion2_DoublesRowsAndSpans_Once()
    {
        var l = new PostLayout { Columns = 2, Version = 2 };
        l.Panels.Add(new PostPanel { Kind = PostPanelKind.Title, Row = 0, Col = 0, Span = 2, RowSpan = 1 });
        l.Panels.Add(new PostPanel { Kind = PostPanelKind.Works, Row = 3, Col = 1, RowSpan = 4 });
        l.MigrateToCurrent(null);
        Assert.Equal(PostLayout.CurrentVersion, l.Version);
        Assert.Equal((0, 2), (l.Panels[0].Row, l.Panels[0].RowSpan));
        Assert.Equal((6, 8), (l.Panels[1].Row, l.Panels[1].RowSpan));
        l.MigrateToCurrent(null); // 2回目は何もしない
        Assert.Equal((6, 8), (l.Panels[1].Row, l.Panels[1].RowSpan));
    }

    [Fact]
    public void EveryKind_HasATitle_IsInExactlyOneMenuGroup_AndNewPanelsAreValid()
    {
        foreach (var kind in PostLayout.AllKinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(PostLayout.DefaultTitle(kind)), kind.ToString());
            Assert.Equal(1, PostLayout.Groups.Count(g => g.Kinds.Contains(kind)));
            var p = PostLayout.NewPanel(kind);
            Assert.InRange(p.ItemCount, 1, PostLayout.MaxItems(kind));
            Assert.InRange(p.RowSpan, 1, PostLayout.MaxRowSpan);
        }
        Assert.Equal(PostLayout.AllKinds.Length, PostLayout.Groups.Sum(g => g.Kinds.Length));
    }

    [Fact]
    public void Layout_SurvivesJsonRoundTrip()
    {
        var l = PostLayout.CreateDefault();
        var p = l.Add(PostPanelKind.Priority);
        p.Title = "次に遊ぶ";
        p.GameIds.AddRange(new long[] { 5, 3 });
        p.RowSpan = 2;
        p.FontFamily = "Meiryo"; p.FontColor = "#FF8800"; p.FontSize = 30; p.ShowPeriod = false;

        var back = JsonSerializer.Deserialize<PostLayout>(JsonSerializer.Serialize(l))!;
        Assert.Equal(PostLayout.CurrentVersion, back.Version);
        Assert.Equal(8, back.Panels.Count);
        var q = back.Panels[7];
        Assert.Equal((PostPanelKind.Priority, "次に遊ぶ", 2), (q.Kind, q.Title, q.RowSpan));
        Assert.Equal(new long[] { 5, 3 }, q.GameIds);
        Assert.Equal(("Meiryo", "#FF8800", 30, false), (q.FontFamily, q.FontColor, q.FontSize, q.ShowPeriod));
        Assert.Equal((p.Row, p.Col), (q.Row, q.Col));
    }
}
