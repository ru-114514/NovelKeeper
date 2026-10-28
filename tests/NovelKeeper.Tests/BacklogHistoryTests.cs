using NovelKeeper.Services;
using Xunit;

namespace NovelKeeper.Tests;

public class BacklogHistoryTests
{
    private static DateTime D(int m, int d) => new(2026, m, d);
    private static BacklogSnapshot S(int m, int d, int n) => new() { Date = D(m, d), Count = n };

    [Fact]
    public void Record_AddsOnePerDay_AndOverwritesTheSameDay()
    {
        var h = new List<BacklogSnapshot>();
        Assert.True(BacklogHistory.Record(h, 50, D(10, 1)));
        Assert.False(BacklogHistory.Record(h, 50, D(10, 1)));   // 変わらなければ保存しない
        Assert.True(BacklogHistory.Record(h, 49, D(10, 1)));    // 同じ日は上書き
        Assert.True(BacklogHistory.Record(h, 48, D(10, 2)));
        Assert.Equal([49, 48], h.Select(x => x.Count));
    }

    [Fact]
    public void Record_KeepsOnlyTheNewestSnapshots()
    {
        var h = Enumerable.Range(0, BacklogHistory.MaxSnapshots).Select(i => new BacklogSnapshot { Date = D(1, 1).AddDays(i), Count = i }).ToList();
        BacklogHistory.Record(h, 1, D(1, 1).AddDays(BacklogHistory.MaxSnapshots));
        Assert.Equal(BacklogHistory.MaxSnapshots, h.Count);
        Assert.Equal(1, h[0].Count);
    }

    [Fact]
    public void Change_ComparesWithTheLastSnapshotBeforeThePeriod()
    {
        var h = new List<BacklogSnapshot> { S(9, 20, 60), S(9, 30, 55), S(10, 10, 52) };
        var c = BacklogHistory.Change(h, D(10, 1), D(10, 31), 50, D(10, 25))!;
        Assert.Equal(D(9, 30), c.BaselineDate);
        Assert.Equal(-5, c.Delta);
        Assert.True(c.IsExact);
        Assert.Equal("−5 本（9/30 の記録より）", BacklogHistory.Describe(c));
    }

    [Fact]
    public void Change_WithoutEarlierSnapshot_UsesTheOldestOne()
    {
        var h = new List<BacklogSnapshot> { S(10, 10, 52) };
        var c = BacklogHistory.Change(h, D(10, 1), D(10, 31), 54, D(10, 25))!;
        Assert.False(c.IsExact);
        Assert.Equal(2, c.Delta);
        Assert.Equal("+2 本（記録を始めた 10/10 より）", BacklogHistory.Describe(c));
    }

    [Fact]
    public void Change_ForAPastPeriod_UsesTheLastSnapshotInIt()
    {
        var h = new List<BacklogSnapshot> { S(8, 31, 70), S(9, 15, 66), S(9, 30, 64), S(10, 10, 60) };
        var c = BacklogHistory.Change(h, D(9, 1), D(9, 30), 50, D(10, 25))!;
        Assert.Equal(64, c.Now);
        Assert.Equal(-6, c.Delta);
    }

    [Fact]
    public void Change_IsNullWhenUnknown()
    {
        Assert.Null(BacklogHistory.Change([], D(10, 1), D(10, 31), 50, D(10, 25)));
        Assert.Null(BacklogHistory.Change([S(10, 10, 52)], D(8, 1), D(8, 31), 50, D(10, 25))); // 期間より新しい記録だけ
        Assert.Contains("まだありません", BacklogHistory.Describe(null));
    }

    [Fact]
    public void Config_StartsWithEmptyHistory()
    {
        Assert.Empty(new EgsConfig().BacklogSnapshots);
    }
}
