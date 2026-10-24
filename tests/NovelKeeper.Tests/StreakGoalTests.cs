using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>連続プレイ日数（1日10分以上）・年間月間のまとめ・目標のテスト</summary>
public class StreakGoalTests
{
    private static Dictionary<DateTime, double> Daily(params (string Day, double Minutes)[] days) =>
        days.ToDictionary(d => DateTime.Parse(d.Day), d => d.Minutes * 60);

    [Fact]
    public void LongestStreak_CountsOnlyDaysWithTenMinutesOrMore()
    {
        var daily = Daily(("2026-10-01", 30), ("2026-10-02", 9.9), ("2026-10-03", 10), ("2026-10-04", 60), ("2026-10-05", 10));
        var s = SummaryExtras.LongestStreak(daily, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Equal(3, s.Days);
        Assert.Equal(new DateTime(2026, 10, 3), s.Start);
        Assert.Equal(new DateTime(2026, 10, 5), s.End);
    }

    [Fact]
    public void LongestStreak_NoPlay_IsZero_AndTieTakesTheNewer()
    {
        Assert.Equal(0, SummaryExtras.LongestStreak(Daily(), new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)).Days);
        var tie = Daily(("2026-10-01", 20), ("2026-10-02", 20), ("2026-10-04", 20), ("2026-10-05", 20));
        var s = SummaryExtras.LongestStreak(tie, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5));
        Assert.Equal(2, s.Days);
        Assert.Equal(new DateTime(2026, 10, 4), s.Start);
    }

    [Fact]
    public void LongestStreak_IsLimitedToThePeriod()
    {
        var daily = Daily(("2026-09-29", 20), ("2026-09-30", 20), ("2026-10-01", 20));
        Assert.Equal(1, SummaryExtras.LongestStreak(daily, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)).Days);
    }

    [Fact]
    public void CurrentStreak_TodayNotYetTenMinutes_CountsFromYesterday()
    {
        var daily = Daily(("2026-10-22", 30), ("2026-10-23", 30), ("2026-10-24", 3));
        Assert.Equal(2, SummaryExtras.CurrentStreak(daily, new DateTime(2026, 10, 24)));
    }

    [Fact]
    public void CurrentStreak_IncludesToday_WhenReached_AndBreaksOnGap()
    {
        var daily = Daily(("2026-10-21", 30), ("2026-10-23", 30), ("2026-10-24", 10));
        Assert.Equal(2, SummaryExtras.CurrentStreak(daily, new DateTime(2026, 10, 24)));
        Assert.Equal(0, SummaryExtras.CurrentStreak(Daily(("2026-10-20", 60)), new DateTime(2026, 10, 24)));
    }

    [Fact]
    public void GoalPercent_AndParseGoal()
    {
        Assert.Null(SummaryExtras.GoalPercent(100, 0));
        Assert.Equal(50, SummaryExtras.GoalPercent(1800, 3600));
        Assert.Equal(150, SummaryExtras.GoalPercent(5400, 3600));

        Assert.Equal(0, SummaryExtras.ParseGoal("", 99));
        Assert.Equal(0, SummaryExtras.ParseGoal("  ", 99));
        Assert.Equal(30, SummaryExtras.ParseGoal(" 30 ", 99));
        Assert.Equal(99, SummaryExtras.ParseGoal("99", 99));
        Assert.Null(SummaryExtras.ParseGoal("100", 99));
        Assert.Null(SummaryExtras.ParseGoal("-1", 99));
        Assert.Null(SummaryExtras.ParseGoal("1.5", 99));
        Assert.Null(SummaryExtras.ParseGoal("abc", 99));
    }

    [Fact]
    public void BuildRecap_CountsTimeDaysClearsAndStreak()
    {
        var rows = new List<SessionRow>
        {
            new(1, "A", new DateTime(2026, 10, 1, 20, 0, 0), new DateTime(2026, 10, 1, 20, 30, 0), 1800),
            new(1, "A", new DateTime(2026, 10, 2, 20, 0, 0), new DateTime(2026, 10, 2, 20, 30, 0), 1800),
            new(2, "B", new DateTime(2026, 9, 30, 20, 0, 0), new DateTime(2026, 9, 30, 21, 0, 0), 3600),
        };
        var games = new List<Game>
        {
            new() { Id = 1, Title = "A", Status = GameStatus.Played, ClearDate = new DateTime(2026, 10, 2) },
            new() { Id = 2, Title = "B", Status = GameStatus.Played, ClearDate = new DateTime(2026, 9, 30) },
        };
        var r = SummaryExtras.BuildRecap(rows, games, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Equal(3600, r.Seconds, 3);
        Assert.Equal(2, r.PlayedDays);
        Assert.Equal(1, r.Clears);
        Assert.Equal(1, r.Games);
        Assert.Equal(2, r.Longest.Days);
    }

    // ---- クリア数の集計から除外 ----

    private static Game ClearedGame(long id, string date, int? score, bool exclude) => new()
    {
        Id = id, Title = "G" + id, Status = GameStatus.Played, ClearDate = DateTime.Parse(date), MyScore = score,
        Review = new Review { ExcludeFromClearCount = exclude },
    };

    [Fact]
    public void Cleared_SkipsExcludedGames()
    {
        var games = new List<Game> { ClearedGame(1, "2026-10-05", 80, false), ClearedGame(2, "2026-10-06", 90, true) };
        var list = SummaryExtras.Cleared(games, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Single(list);
        Assert.Equal(1, list[0].GameId);
    }

    [Fact]
    public void BuildRecap_ExcludedGameIsNotCounted_ButPlayTimeIs()
    {
        var rows = new List<SessionRow>
        {
            new(2, "G2", new DateTime(2026, 10, 6, 20, 0, 0), new DateTime(2026, 10, 6, 21, 0, 0), 3600),
        };
        var games = new List<Game> { ClearedGame(2, "2026-10-06", 90, true) };
        var r = SummaryExtras.BuildRecap(rows, games, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Equal(0, r.Clears);
        Assert.Equal(3600, r.Seconds, 3);
    }

    [Fact]
    public void PostReport_ExcludedGameIsLeftOutOfClearsAndScores()
    {
        var games = new List<Game> { ClearedGame(1, "2026-10-05", 80, false), ClearedGame(2, "2026-10-06", 100, true) };
        var r = PostReportCalculator.Build(games, [], new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Equal(1, r.ClearCount);
        Assert.Equal(80, r.AverageScore);
        Assert.Single(r.Cleared);
        Assert.Equal(1, r.ScoreBins.Sum());
    }

    [Fact]
    public void ExcludeFlag_IsLocalOnly_SurvivesCloneAndJson()
    {
        var a = new Review { ExcludeFromClearCount = true };
        Assert.Empty(Review.Diff(a, new Review())); // 批評空間に送る変更にはならない
        Assert.True(a.Clone().ExcludeFromClearCount);
        var back = System.Text.Json.JsonSerializer.Deserialize<Review>(System.Text.Json.JsonSerializer.Serialize(a))!;
        Assert.True(back.ExcludeFromClearCount);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<Review>("{}")!.ExcludeFromClearCount);
    }

    [Fact]
    public void MonthlyTotals_ListsOldestFirst_WithEmptyMonths()
    {
        var rows = new List<SessionRow>
        {
            new(1, "A", new DateTime(2026, 8, 5, 20, 0, 0), new DateTime(2026, 8, 5, 21, 0, 0), 3600),
            new(1, "A", new DateTime(2026, 10, 5, 20, 0, 0), new DateTime(2026, 10, 5, 20, 30, 0), 1800),
        };
        var games = new List<Game> { new() { Id = 1, Title = "A", Status = GameStatus.Played, ClearDate = new DateTime(2026, 10, 9) } };
        var m = SummaryExtras.MonthlyTotals(rows, games, new DateTime(2026, 10, 24), 3);
        Assert.Equal(3, m.Count);
        Assert.Equal(new DateTime(2026, 8, 1), m[0].Month);
        Assert.Equal(3600, m[0].Seconds, 3);
        Assert.Equal(0, m[1].Seconds, 3);
        Assert.Equal(1800, m[2].Seconds, 3);
        Assert.Equal(1, m[2].Clears);
    }
}
