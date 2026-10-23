using System.Text;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

public class CsvExporterTests
{
    private const string Header =
        "作品名,ブランド,発売日,批評空間ID,所持,状態,自分の得点,開始日,クリア日,総プレイ時間,中央値,平均値,データ数,批評空間側の状態,最終同期日,購入額(円)";

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void SelectAll_ExcludesBeforePurchaseWorks_UnlessTheyAreShownInTheList()
    {
        var owned = new Game { Title = "所持", EgsPossession = true };
        var before = new Game { Title = "購入前", EgsPossession = false }; // 未プレイ・所持なし・起動設定なし・計測なし
        var playedNotOwned = new Game { Title = "プレイ済", Status = ErogeLauncher.Models.GameStatus.Played, EgsPlayed = true, EgsPossession = false };
        Assert.True(before.IsBeforePurchase);
        Assert.False(playedNotOwned.IsBeforePurchase);

        // 一覧に出していないときは、CSV にも出さない
        Assert.Equal(["所持", "プレイ済"], CsvExporter.SelectAll([owned, before, playedNotOwned], showBeforePurchase: false).Select(g => g.Title));
        // 出しているときは、含める
        Assert.Equal(["所持", "購入前", "プレイ済"], CsvExporter.SelectAll([owned, before, playedNotOwned], showBeforePurchase: true).Select(g => g.Title));
    }

    [Fact]
    public void ColumnNames_MatchTheHeaderRow_ForBothModes()
    {
        // 画面の「書き出す列」の表示と、実際の見出し行が、ずれないこと
        Assert.Equal(Lines(CsvExporter.Build([]))[0], string.Join(",", CsvExporter.ColumnNames()));
        Assert.Equal(Lines(CsvExporter.Build([], includeReview: true))[0], string.Join(",", CsvExporter.ColumnNames(true)));
        Assert.Equal(16, CsvExporter.ColumnNames().Count);
        Assert.Equal(18, CsvExporter.ColumnNames(true).Count);
    }

    [Fact]
    public void Header_FollowsAgreedColumnOrder_AndOmitsReviewColumnsByDefault()
    {
        var csv = CsvExporter.Build([]);
        Assert.Equal(Header + "\r\n", csv);
    }

    [Fact]
    public void Header_WithReview_PutsReviewColumnsBetweenDataCountAndEgsState()
    {
        var header = Lines(CsvExporter.Build([], includeReview: true))[0];
        Assert.Equal(Header.Replace("データ数,", "データ数,一言感想,長文感想,"), header);
    }

    [Fact]
    public void Row_FormatsAllFields()
    {
        var g = new Game
        {
            Title = "作品A", Brand = "ブランドX", SellDay = "2020-01-31", EgsId = 101,
            Status = GameStatus.Played, MyScore = 85, ClearDate = new DateTime(2021, 5, 6),
            Review = new Review { StartDate = new DateTime(2021, 3, 1) },
            FirstPlayed = new DateTime(2021, 2, 20, 22, 15, 0), // 開始日は「初回起動日」ではなく、ゲーム開始日の欄
            Price = 3300,
            TotalSeconds = 117000, // 32.5 時間 = 32:30:00（記録が 0 秒の作品は、総プレイ時間が空欄）
            Median = 80, EgsAverage = 79, EgsCount = 250, EgsPlayed = true, EgsClearDate = new DateTime(2021, 5, 6),
            EgsSyncedAt = new DateTime(2026, 10, 20, 9, 0, 0), EgsPossession = true,
        };

        var row = Lines(CsvExporter.Build([g]))[1];

        Assert.Equal("作品A,ブランドX,2020/01/31,101,所持,プレイ済,85,2021/03/01,2021/05/06,32:30:00,80,79,250,プレイ済 2021/05/06,2026/10/20,3300", row);
    }

    [Fact]
    public void EmptyValues_AreEmptyFields_AndZeroSecondsIsBlank()
    {
        var row = Lines(CsvExporter.Build([new Game { Title = "T" }]))[1];
        Assert.Equal("T,,,,,未プレイ,,,,,,,,未連携,,", row);
    }

    [Theory]
    [InlineData(GameStatus.Unplayed, "未プレイ")]
    [InlineData(GameStatus.Playing, "プレイ中")]
    [InlineData(GameStatus.Played, "プレイ済")]   // 一覧の「☑ プレイ済」ではなく、記号なしで出す
    [InlineData(GameStatus.Dropped, "中断")]
    public void StatusNames(GameStatus status, string expected)
    {
        var cols = Lines(CsvExporter.Build([new Game { Title = "T", Status = status }]))[1].Split(',');
        Assert.Equal(expected, cols[5]);
    }

    [Fact]
    public void CommaQuoteAndNewline_AreQuoted_PlainTextIsNot()
    {
        Assert.Equal("abc", CsvExporter.Quote("abc"));
        Assert.Equal("\"a,b\"", CsvExporter.Quote("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Quote("say \"hi\""));
        Assert.Equal("\"1行目\r\n2行目\"", CsvExporter.Quote("1行目\r\n2行目"));
        Assert.Equal("\" 先頭の空白\"", CsvExporter.Quote(" 先頭の空白"));
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    public void FormulaLikeValues_GetLeadingApostrophe(string input, string expected)
        => Assert.Equal(expected, CsvExporter.Quote(input));

    [Fact]
    public void Review_IsWrittenOnlyWhenRequested_WithMultilineMemoKeptInOneField()
    {
        var g = new Game { Title = "T", Review = new Review { Hitokoto = "良作", Memo = "1行目\r\n2行目, \"引用\"" } };

        Assert.DoesNotContain("良作", CsvExporter.Build([g]));

        var csv = CsvExporter.Build([g], includeReview: true);
        Assert.Contains(",良作,\"1行目\r\n2行目, \"\"引用\"\"\",", csv);
    }

    [Fact]
    public void Write_UsesUtf8Bom_AndLeavesNoTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            CsvExporter.Write(path, [new Game { Title = "日本語" }]);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // Excel が文字化けしない
            Assert.Contains("日本語", Encoding.UTF8.GetString(bytes));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UniquePath_NeverOverwrites_AndNumbersFromOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var p = Path.Combine(dir, "a.csv");
            Assert.Equal(p, CsvExporter.UniquePath(p));

            File.WriteAllText(p, "x");
            Assert.Equal(Path.Combine(dir, "a (1).csv"), CsvExporter.UniquePath(p));

            File.WriteAllText(Path.Combine(dir, "a (1).csv"), "x");
            Assert.Equal(Path.Combine(dir, "a (2).csv"), CsvExporter.UniquePath(p));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SellDay_UnparseableValue_IsKeptAsIs()
    {
        var cols = Lines(CsvExporter.Build([new Game { Title = "T", SellDay = "不明" }]))[1].Split(',');
        Assert.Equal("不明", cols[2]);
    }
}
