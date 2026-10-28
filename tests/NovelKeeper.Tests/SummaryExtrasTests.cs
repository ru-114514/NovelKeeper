using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>レポートの追加欄（ヒートマップ・散布図・クリアと積みゲー）の集計のテスト</summary>
public class SummaryExtrasTests
{
    private static readonly DateTime From = new(2026, 10, 1);
    private static readonly DateTime To = new(2026, 10, 31);

    private static SessionRow S(long game, string start, int minutes) =>
        new(game, "G" + game, DateTime.Parse(start), DateTime.Parse(start).AddMinutes(minutes), minutes * 60L);

    [Fact]
    public void Heatmap_PutsTimeInWeekdayAndHourCells_MondayFirst()
    {
        // 2026-10-05 は月曜、2026-10-04 は日曜
        var h = SummaryExtras.Heatmap([S(1, "2026-10-05 21:00", 30), S(1, "2026-10-04 08:00", 60)], From, To);
        Assert.Equal(1800, h[0, 21], 3);
        Assert.Equal(3600, h[6, 8], 3);
        Assert.Equal(5400, h.Cast<double>().Sum(), 3);
    }

    [Fact]
    public void Heatmap_SplitsAcrossHours_AndIgnoresOutsidePeriod()
    {
        var h = SummaryExtras.Heatmap([S(1, "2026-10-05 21:30", 60), S(1, "2026-09-30 22:00", 30)], From, To);
        Assert.Equal(1800, h[0, 21], 3);
        Assert.Equal(1800, h[0, 22], 3);
        Assert.Equal(3600, h.Cast<double>().Sum(), 3);
    }

    [Fact]
    public void ScorePoints_KeepsOnlyScoredGames()
    {
        var games = new List<Game>
        {
            new() { Id = 1, Title = "A", MyScore = 85 },
            new() { Id = 2, Title = "B", MyScore = null },
        };
        var played = new[] { new GameSummary(1, "A", 7200), new GameSummary(2, "B", 600), new GameSummary(3, "C", 60) };
        var p = Assert.Single(SummaryExtras.ScorePoints(played, games));
        Assert.Equal(new ScorePoint(1, "A", 7200, 85), p);
    }

    [Fact]
    public void Cleared_UsesPlayedWithClearDateInPeriod_SortedByDate()
    {
        var games = new List<Game>
        {
            new() { Id = 1, Title = "B", Status = GameStatus.Played, ClearDate = new DateTime(2026, 10, 20), MyScore = 90 },
            new() { Id = 2, Title = "A", Status = GameStatus.Played, ClearDate = new DateTime(2026, 10, 1) },
            new() { Id = 3, Title = "C", Status = GameStatus.Played, ClearDate = new DateTime(2026, 11, 1) },
            new() { Id = 4, Title = "D", Status = GameStatus.Playing, ClearDate = new DateTime(2026, 10, 5) },
        };
        var c = SummaryExtras.Cleared(games, From, To);
        Assert.Equal(new long[] { 2, 1 }, c.Select(x => x.GameId));
        Assert.Null(c[0].Score);
        Assert.Equal(90, c[1].Score);
    }

    [Fact]
    public void BacklogCount_CountsUnplayedExcludingUnreleasedAndBeforePurchase()
    {
        var games = new List<Game>
        {
            new() { Id = 1, Status = GameStatus.Unplayed, TotalSeconds = 0, EgsPossession = true },
            new() { Id = 2, Status = GameStatus.Unplayed, EgsPossession = true },
            new() { Id = 3, Status = GameStatus.Playing },
            new() { Id = 4, Status = GameStatus.Unplayed }, // 未所持＝購入前
        };
        Assert.Equal(2, SummaryExtras.BacklogCount(games));
    }
}
