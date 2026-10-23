using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>投稿用レポートの集計のテスト</summary>
public class PostReportTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 10, 31);

    private static Game G(long id, string brand, int? score = null, DateTime? clear = null, GameStatus st = GameStatus.Playing) =>
        new() { Id = id, Title = "G" + id, Brand = brand, MyScore = score, ClearDate = clear, Status = clear != null ? GameStatus.Played : st };

    private static SessionRow S(long game, string day, long sec) =>
        new(game, "G" + game, DateTime.Parse(day + " 20:00"), DateTime.Parse(day + " 20:00").AddSeconds(sec), sec);

    [Theory]
    [InlineData("2026-10-01", false, "2026-10-31")]
    [InlineData("2026-01-31", false, "2026-02-27")]
    [InlineData("2026-10-15", true, "2027-10-14")]
    [InlineData("2024-02-29", true, "2025-02-27")]
    public void PeriodEnd_IncludesStartDay(string start, bool year, string end) =>
        Assert.Equal(DateTime.Parse(end), PostReportCalculator.PeriodEnd(DateTime.Parse(start), year));

    [Fact]
    public void ClearCountAndAverageScore_UseOnlyClearDatesInPeriod_AndIgnoreMissingScores()
    {
        var games = new List<Game>
        {
            G(1, "A", 80, new DateTime(2026, 10, 1)),   // 期間の最初の日
            G(2, "A", 60, new DateTime(2026, 10, 31)),  // 期間の最後の日
            G(3, "A", null, new DateTime(2026, 10, 15)), // 得点なし
            G(4, "A", 100, new DateTime(2026, 9, 30)),  // 期間の前日
            G(5, "A", 100, new DateTime(2026, 11, 1)),  // 期間の翌日
        };
        var r = PostReportCalculator.Build(games, [], From, To);
        Assert.Equal(3, r.ClearCount);
        Assert.Equal(70, r.AverageScore);
    }

    [Fact]
    public void ExcludeClearedBefore_DropsGamesClearedBeforeStart_OnlyFromWorks()
    {
        var games = new List<Game>
        {
            G(1, "A", 90, new DateTime(2026, 9, 20)),  // 起点より前にクリア（その後も遊んでいる）
            G(2, "B", 70, new DateTime(2026, 10, 10)), // 期間内にクリア
            G(3, "C"),                                  // 未クリア
        };
        var rows = new[] { S(1, "2026-10-05", 5000), S(2, "2026-10-06", 300), S(3, "2026-10-07", 200) };

        var all = PostReportCalculator.Build(games, rows, From, To);
        Assert.Equal(5500, all.TotalSeconds);
        Assert.Equal("A", all.Brand!.Brand);

        var r = PostReportCalculator.Build(games, rows, From, To, excludeClearedBefore: true);
        Assert.Equal(5500, r.TotalSeconds); // 時間・ブランドは変わらない
        Assert.Equal(new long[] { 2, 3 }, r.Works.Select(w => w.GameId));
        Assert.Equal("A", r.Brand!.Brand);
        Assert.Equal(1, r.ClearCount); // 期間内のクリアは、どちらでも同じ
    }

    [Fact]
    public void NoClears_GivesNoAverageScore()
    {
        var r = PostReportCalculator.Build([G(1, "A")], [], From, To);
        Assert.Equal(0, r.ClearCount);
        Assert.Null(r.AverageScore);
    }

    [Fact]
    public void Works_AreSortedByPlayTimeInPeriod_AndTotalMatches()
    {
        var games = new List<Game> { G(1, "A"), G(2, "B"), G(3, "C") };
        var rows = new[] { S(1, "2026-10-05", 600), S(2, "2026-10-06", 3600), S(3, "2026-09-30", 9999), S(1, "2026-10-07", 600) };
        var r = PostReportCalculator.Build(games, rows, From, To);
        Assert.Equal(new long[] { 2, 1 }, r.Works.Select(w => w.GameId));
        Assert.Equal(4800, r.TotalSeconds);
    }

    [Fact]
    public void Brand_MostPlayed_SumsPerBrand_AndTieGoesToHigherAverageScore()
    {
        var games = new List<Game> { G(1, "A", 70), G(2, "B", 90), G(3, "C", 50) };
        var rows = new[] { S(1, "2026-10-05", 1000), S(2, "2026-10-06", 1000), S(3, "2026-10-07", 500) };
        var b = PostReportCalculator.Build(games, rows, From, To).Brand;
        Assert.Equal("B", b!.Brand);
    }

    [Fact]
    public void Brand_MostPlayed_CombinesGamesOfTheSameBrand()
    {
        var games = new List<Game> { G(1, "A"), G(2, "A"), G(3, "B") };
        var rows = new[] { S(1, "2026-10-05", 400), S(2, "2026-10-06", 400), S(3, "2026-10-07", 700) };
        var b = PostReportCalculator.Build(games, rows, From, To).Brand!;
        Assert.Equal("A", b.Brand);
        Assert.Equal(800, b.Seconds);
        Assert.Equal(2, b.GameCount);
    }

    [Fact]
    public void Brand_HighestScore_NeedsMinimumGames_AndTieGoesToLongerPlayTime()
    {
        var games = new List<Game> { G(1, "A", 100), G(2, "B", 80), G(3, "B", 80), G(4, "C", 80), G(5, "C", 80) };
        var rows = new[] { S(1, "2026-10-05", 5000), S(2, "2026-10-06", 100), S(3, "2026-10-06", 100), S(4, "2026-10-07", 300), S(5, "2026-10-07", 300) };
        var b = PostReportCalculator.Build(games, rows, From, To, PostBrandMode.HighestScore).Brand!;
        Assert.Equal("C", b.Brand); // A は1本だけなので対象外。B と C は同点なので、時間が長い C
        Assert.Equal(80, b.AverageScore);
        Assert.Null(PostReportCalculator.Build(games, rows, From, To, PostBrandMode.HighestScore, minBrandGames: 3).Brand);
    }

    [Fact]
    public void Brand_Specific_ReturnsThatBrand_EvenWhenNotPlayedInPeriod()
    {
        var games = new List<Game> { G(1, "A"), G(2, "B") };
        var rows = new[] { S(1, "2026-10-05", 1000) };
        var a = PostReportCalculator.Build(games, rows, From, To, PostBrandMode.Specific, "a").Brand!;
        Assert.Equal(1000, a.Seconds);
        var b = PostReportCalculator.Build(games, rows, From, To, PostBrandMode.Specific, "B").Brand!;
        Assert.Equal(0, b.Seconds);
        Assert.Null(PostReportCalculator.Build(games, rows, From, To, PostBrandMode.Specific, "").Brand);
    }

    [Fact]
    public void Brand_IsNull_WhenNoGamesWerePlayed_OrBrandIsBlank()
    {
        Assert.Null(PostReportCalculator.Build([G(1, "A")], [], From, To).Brand);
        Assert.Null(PostReportCalculator.Build([G(1, "")], [S(1, "2026-10-05", 100)], From, To).Brand);
    }

    [Fact]
    public void DaysWorksLongestPeakAndBuckets_FollowTheSummary()
    {
        var games = new List<Game> { G(1, "A"), G(2, "B") };
        var rows = new[] { S(1, "2026-10-05", 600), S(2, "2026-10-05", 1200), S(1, "2026-10-09", 3000) };
        var r = PostReportCalculator.Build(games, rows, From, To);
        Assert.Equal(31, r.DayCount);
        Assert.Equal(2, r.PlayedDays);
        Assert.Equal(2, r.PlayedGameCount);
        Assert.Equal(3000, r.LongestSession!.Seconds);
        Assert.Equal(new DateTime(2026, 10, 9), r.BestDay!.Value.Day);
        Assert.Equal(20, r.PeakHour);
        Assert.Equal(31, r.Buckets.Count);
        Assert.Equal(2, r.DailySeconds.Count);
    }

    [Fact]
    public void PlayedGameCount_IgnoresTheExcludeClearedOption()
    {
        var games = new List<Game> { G(1, "A", 80, new DateTime(2026, 9, 1)), G(2, "B") };
        var rows = new[] { S(1, "2026-10-05", 600), S(2, "2026-10-06", 600) };
        var r = PostReportCalculator.Build(games, rows, From, To, excludeClearedBefore: true);
        Assert.Equal(2, r.PlayedGameCount);
        Assert.Single(r.Works);
    }

    [Fact]
    public void PreviousSeconds_ComesFromTheSameLengthPeriodBefore()
    {
        var games = new List<Game> { G(1, "A") };
        var rows = new[] { S(1, "2026-10-05", 600) };
        var prev = new[] { S(1, "2026-09-20", 1800) }; // 10/1〜10/31 の31日前 = 8/31〜9/30
        Assert.Equal(1800, PostReportCalculator.Build(games, rows, From, To, previousSessions: prev).PreviousSeconds);
        Assert.Null(PostReportCalculator.Build(games, rows, From, To).PreviousSeconds);
    }

    [Fact]
    public void ClearedList_ScoreBins_AndMedianCompare()
    {
        Game C(long id, int? score, int? median, string day) { var g = G(id, "A", score, DateTime.Parse(day)); g.Median = median; return g; }
        var games = new List<Game>
        {
            C(1, 55, 60, "2026-10-20"), C(2, 60, 70, "2026-10-02"), C(3, 79, 80, "2026-10-10"),
            C(4, 85, null, "2026-10-11"), C(5, 90, 70, "2026-10-12"), C(6, 100, 90, "2026-10-13"), C(7, null, 50, "2026-10-14"),
        };
        var r = PostReportCalculator.Build(games, [], From, To);
        Assert.Equal(new[] { 2L, 3, 4, 5, 6, 7, 1 }, r.Cleared.Select(c => c.GameId)); // 終了日の古い順
        Assert.Equal(new[] { 1, 1, 1, 1, 2 }, r.ScoreBins);
        var (mine, median) = r.MedianCompare!.Value; // 点数・中央値の両方がある 1,2,3,5,6
        Assert.Equal((55 + 60 + 79 + 90 + 100) / 5.0, mine);
        Assert.Equal((60 + 70 + 80 + 70 + 90) / 5.0, median);
    }

    [Fact]
    public void BrandRanking_IsSortedByPlayTime_AndSkipsBlankBrands()
    {
        var games = new List<Game> { G(1, "A"), G(2, "B"), G(3, ""), G(4, "B") };
        var rows = new[] { S(1, "2026-10-05", 100), S(2, "2026-10-05", 200), S(3, "2026-10-05", 9999), S(4, "2026-10-06", 300) };
        var r = PostReportCalculator.Build(games, rows, From, To);
        Assert.Equal(new[] { "B", "A" }, r.Brands.Select(b => b.Brand));
        Assert.Equal(500, r.Brands[0].Seconds);
    }

    [Fact]
    public void Backlog_CountsUnplayedGamesYouOwn_NotBeforePurchaseOrUnreleased()
    {
        var owned = new Game { Id = 1, Title = "owned", Status = GameStatus.Unplayed, Review = { Possession = true } };
        var notOwned = new Game { Id = 2, Title = "wish", Status = GameStatus.Unplayed }; // 購入前
        var playing = new Game { Id = 3, Title = "playing", Status = GameStatus.Playing, Review = { Possession = true } };
        var unreleased = new Game { Id = 4, Title = "future", Status = GameStatus.Unplayed, SellDay = "2999-01-01", Review = { Possession = true } };
        var r = PostReportCalculator.Build([owned, notOwned, playing, unreleased], [], From, To);
        Assert.Equal(1, r.BacklogCount);
    }
}
