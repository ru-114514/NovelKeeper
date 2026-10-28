using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>レポート（期間の集計）のテスト。1日は 0:00〜23:59</summary>
public sealed class SummaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    static SummaryTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public SummaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SessionRow Row(long game, string start, string end, long sec) =>
        new(game, "G" + game, DateTime.Parse(start), DateTime.Parse(end), sec);

    private static readonly DateTime D1 = new(2026, 10, 20);

    [Fact]
    public void SingleDay_IsHourly_AndSplitsAcrossHours()
    {
        // 10:30〜11:30 の1時間（3600秒）は、10時台と11時台に半分ずつ
        var r = SummaryCalculator.Build(new[] { Row(1, "2026-10-20 10:30:00", "2026-10-20 11:30:00", 3600) }, D1, D1);

        Assert.True(r.IsHourly);
        Assert.Equal(24, r.Buckets.Count);
        Assert.Equal(1800, r.Buckets[10].Seconds, 3);
        Assert.Equal(1800, r.Buckets[11].Seconds, 3);
        Assert.Equal(3600, r.TotalSeconds, 3);
        Assert.Equal(1, r.PlayedDays);
    }

    [Fact]
    public void SessionAcrossMidnight_IsSplitIntoBothDays()
    {
        // 23:00〜翌1:00（2時間）は、前日 1時間・翌日 1時間
        var rows = new[] { Row(1, "2026-10-20 23:00:00", "2026-10-21 01:00:00", 7200) };

        var both = SummaryCalculator.Build(rows, D1, D1.AddDays(1));
        Assert.Equal(3600, both.Buckets[0].Seconds, 3);
        Assert.Equal(3600, both.Buckets[1].Seconds, 3);
        Assert.Equal(2, both.PlayedDays);

        // 期間に入っている日の分だけ数える
        var first = SummaryCalculator.Build(rows, D1, D1);
        Assert.Equal(3600, first.TotalSeconds, 3);
        var second = SummaryCalculator.Build(rows, D1.AddDays(1), D1.AddDays(1));
        Assert.Equal(3600, second.TotalSeconds, 3);
        Assert.Equal(3600, second.Buckets[0].Seconds, 3); // 0時台
    }

    [Fact]
    public void PausedTime_ScalesTheShare()
    {
        // 2時間いたが、数えたのは1時間（放置を除外）。日をまたぐ割合はそのまま、合計は数えた秒数に一致する
        var r = SummaryCalculator.Build(new[] { Row(1, "2026-10-20 23:00:00", "2026-10-21 01:00:00", 3600) }, D1, D1.AddDays(1));
        Assert.Equal(3600, r.TotalSeconds, 3);
        Assert.Equal(1800, r.Buckets[0].Seconds, 3);
    }

    [Fact]
    public void ZeroLengthSession_GoesToStartHour()
    {
        var r = SummaryCalculator.Build(new[] { Row(1, "2026-10-20 08:15:00", "2026-10-20 08:15:00", 600) }, D1, D1);
        Assert.Equal(600, r.Buckets[8].Seconds, 3);
    }

    [Fact]
    public void Ranking_IsSortedByTimeDescending_AndPeriodEdgesAreInclusive()
    {
        var rows = new[]
        {
            Row(1, "2026-10-20 00:00:00", "2026-10-20 00:10:00", 600),   // 開始日の 0:00 は含む
            Row(2, "2026-10-22 23:50:00", "2026-10-22 23:59:00", 540),   // 終了日の 23:59 も含む
            Row(2, "2026-10-21 12:00:00", "2026-10-21 13:00:00", 3600),
            Row(3, "2026-10-23 00:00:00", "2026-10-23 01:00:00", 3600),  // 期間の外
        };
        var r = SummaryCalculator.Build(rows, D1, D1.AddDays(2));

        Assert.Equal(new long[] { 2, 1 }, r.Games.Select(g => g.GameId).ToArray());
        Assert.Equal(4140, r.Games[0].Seconds, 3);
        Assert.Equal(3, r.DayCount);
        Assert.Equal(3, r.PlayedDays);
        Assert.Equal(3, r.Buckets.Count);
    }

    [Fact]
    public void ReversedRange_IsSwapped()
    {
        var r = SummaryCalculator.Build(Array.Empty<SessionRow>(), D1.AddDays(2), D1);
        Assert.Equal(D1, r.From);
        Assert.Equal(3, r.DayCount);
        Assert.Equal(0, r.TotalSeconds);
        Assert.Empty(r.Games);
    }

    [Fact]
    public void LongRange_FallsBackToWeeklyThenMonthlyBars()
    {
        var weekly = SummaryCalculator.Build(Array.Empty<SessionRow>(), D1, D1.AddDays(99));
        Assert.InRange(weekly.Buckets.Count, 14, 16);

        var monthly = SummaryCalculator.Build(Array.Empty<SessionRow>(), D1, D1.AddDays(799));
        Assert.InRange(monthly.Buckets.Count, 26, 28);
    }

    [Fact]
    public void WeeklyBars_SumToTotal_AndStartOnMonday()
    {
        // 2026-10-19 は月曜
        Assert.Equal(new DateTime(2026, 10, 19), SummaryCalculator.WeekStart(new DateTime(2026, 10, 25)));
        var rows = new[]
        {
            Row(1, "2026-10-20 10:00:00", "2026-10-20 11:00:00", 3600),
            Row(1, "2026-12-01 10:00:00", "2026-12-01 11:00:00", 1800),
        };
        var r = SummaryCalculator.Build(rows, D1, D1.AddDays(99));
        Assert.Equal(5400, r.Buckets.Sum(b => b.Seconds), 3);
    }

    [Fact]
    public void PreviousRange_IsTheSameLengthJustBefore()
    {
        var (f, t) = SummaryCalculator.PreviousRange(D1, D1.AddDays(2)); // 10/20〜10/22
        Assert.Equal(new DateTime(2026, 10, 17), f);
        Assert.Equal(new DateTime(2026, 10, 19), t);

        var (f1, t1) = SummaryCalculator.PreviousRange(D1, D1); // 1日なら前日
        Assert.Equal(D1.AddDays(-1), f1);
        Assert.Equal(D1.AddDays(-1), t1);
    }

    [Fact]
    public void FormatDelta_Examples()
    {
        Assert.Equal("+1時間00分（+100%）", SummaryCalculator.FormatDelta(7200, 3600));
        Assert.Equal("-30分（-50%）", SummaryCalculator.FormatDelta(1800, 3600));
        Assert.Equal("+10分", SummaryCalculator.FormatDelta(600, 0));   // 前が0なら割合は出さない
        Assert.Equal("変化なし", SummaryCalculator.FormatDelta(3600, 3600));
    }

    [Fact]
    public void Highlights_BestDay_LongestSession_PeakHour()
    {
        var rows = new[]
        {
            Row(1, "2026-10-20 21:00:00", "2026-10-20 21:30:00", 1800),
            Row(2, "2026-10-21 21:00:00", "2026-10-21 23:00:00", 7200),
            Row(1, "2026-10-21 09:00:00", "2026-10-21 09:10:00", 600),
        };
        var r = SummaryCalculator.Build(rows, D1, D1.AddDays(2));

        Assert.Equal(D1.AddDays(1), r.BestDay!.Value.Day);
        Assert.Equal(7800, r.BestDay!.Value.Seconds, 3);
        Assert.Equal(2, r.LongestSession!.GameId);
        Assert.Equal(21, r.PeakHour);
        Assert.Equal(1800, r.DailySeconds[D1], 3);
    }

    [Fact]
    public void Highlights_AreEmpty_WhenNothingPlayed()
    {
        var r = SummaryCalculator.Build(Array.Empty<SessionRow>(), D1, D1.AddDays(2));
        Assert.Null(r.BestDay);
        Assert.Null(r.LongestSession);
        Assert.Null(r.PeakHour);
    }

    [Fact]
    public void AxisStep_KeepsAtMostFiveGridLines_EvenForHugeTotals()
    {
        Assert.Equal(900, SummaryCalculator.AxisStep(3600));
        Assert.Equal(86400, SummaryCalculator.AxisStep(5 * 86400));
        foreach (var hours in new[] { 1, 30, 200, 2000, 20000 })
        {
            var max = hours * 3600d;
            var step = SummaryCalculator.AxisStep(max);
            Assert.True(Math.Ceiling(max / step) <= 6, $"{hours}時間: 目盛りが多すぎる");
        }
    }

    [Fact]
    public void FormatDuration_Examples()
    {
        Assert.Equal("2時間05分", SummaryCalculator.FormatDuration(7500));
        Assert.Equal("7分", SummaryCalculator.FormatDuration(420));
        Assert.Equal("30秒", SummaryCalculator.FormatDuration(30));
        Assert.Equal("0分", SummaryCalculator.FormatDuration(0));
    }

    [Fact]
    public void Database_GetSessions_ReturnsOnlyOverlappingSessions_WithTitle()
    {
        var db = new Database(Path.Combine(_dir, "games.db"));
        var g = new Game { Title = "作品A" };
        db.Insert(g);
        db.AddSession(g.Id, new DateTime(2026, 10, 19, 23, 0, 0), new DateTime(2026, 10, 20, 1, 0, 0), 7200); // 前日からまたぐ
        db.AddSession(g.Id, new DateTime(2026, 10, 20, 12, 0, 0), new DateTime(2026, 10, 20, 13, 0, 0), 3600);
        db.AddSession(g.Id, new DateTime(2026, 10, 21, 0, 0, 0), new DateTime(2026, 10, 21, 1, 0, 0), 3600);   // 翌日

        var rows = db.GetSessions(D1, D1.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.Equal("作品A", x.Title));
        // 1日分だけ集計すると、またいだ分の後半(1時間)と、昼の1時間
        Assert.Equal(7200 / 2 + 3600, SummaryCalculator.Build(rows, D1, D1).TotalSeconds, 3);
    }
}
