using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>レポートの「傾向」欄の集計（年別・ブランド別・得点の分布・ジャンル別）のテスト</summary>
public class TrendStatsTests
{
    private static SessionRow S(long id, DateTime start, long sec) => new(id, "t" + id, start, start.AddSeconds(sec), sec);

    private static Game G(long id, string brand, GameStatus st, int? score = null, DateTime? clear = null, long sec = 3600, int? egs = null, bool exclude = false)
    {
        var g = new Game { Id = id, Title = "g" + id, Brand = brand, Status = st, MyScore = score, ClearDate = clear, TotalSeconds = sec, EgsId = egs };
        g.Review.ExcludeFromClearCount = exclude;
        return g;
    }

    [Fact]
    public void ByYear_CountsTimeByStartYear_AndClearsAndAverageByClearYear_NewestFirst()
    {
        var rows = new[]
        {
            S(1, new DateTime(2025, 12, 31, 23, 0, 0), 3600), S(1, new DateTime(2026, 1, 2, 10, 0, 0), 1800),
            S(2, new DateTime(2026, 3, 1, 10, 0, 0), 600),
        };
        var games = new[]
        {
            G(1, "A", GameStatus.Played, 80, new DateTime(2026, 1, 10)),
            G(2, "A", GameStatus.Played, 90, new DateTime(2026, 4, 1)),
            G(3, "B", GameStatus.Played, null, new DateTime(2024, 5, 1)),
            G(4, "B", GameStatus.Played, 10, new DateTime(2026, 5, 1), exclude: true), // クリア数から除外
        };
        var years = TrendStats.ByYear(rows, games);
        Assert.Equal(new[] { 2026, 2025, 2024 }, years.Select(y => y.Year));
        var y26 = years[0];
        Assert.Equal(2400, y26.Seconds);
        Assert.Equal(2, y26.Games);
        Assert.Equal(2, y26.Clears);
        Assert.Equal(85, y26.AvgScore);
        Assert.Equal(3600, years[1].Seconds);
        Assert.Equal(1, years[2].Clears);
        Assert.Null(years[2].AvgScore);
    }

    [Fact]
    public void ByBrand_GroupsPlayedGames_SortsByCountThenTime_AndIgnoresUnplayedAndNoBrand()
    {
        var games = new[]
        {
            G(1, "A", GameStatus.Played, 80, sec: 100), G(2, "A", GameStatus.Playing, null, sec: 100),
            G(3, "B", GameStatus.Played, 70, sec: 9000), G(4, "B", GameStatus.Dropped, 90, sec: 10),
            G(5, "C", GameStatus.Unplayed, 99, sec: 0), G(6, " ", GameStatus.Played, 50), G(7, "D", GameStatus.Unplayed, null, sec: 5),
        };
        var rows = TrendStats.ByBrand(games);
        Assert.Equal(new[] { "B", "A", "D" }, rows.Select(r => r.Name)); // 同数なら時間の長い方（B）が先。D は時間の記録があるので数える
        Assert.Equal(80, rows[0].AvgScore);
        Assert.Equal(1, rows[0].Clears);
        Assert.Equal(80, rows[1].AvgScore); // 得点のある1本だけで平均
        Assert.Equal(1, rows[1].Scored);
    }

    [Fact]
    public void ByBrand_TakesOnlyTheTop()
    {
        var games = Enumerable.Range(1, 15).Select(i => G(i, "B" + i, GameStatus.Played)).ToArray();
        Assert.Equal(10, TrendStats.ByBrand(games).Count);
        Assert.Equal(3, TrendStats.ByBrand(games, 3).Count);
    }

    [Fact]
    public void ScoreBands_CountsPlayedScoredGames()
    {
        var games = new[]
        {
            G(1, "A", GameStatus.Played, 100), G(2, "A", GameStatus.Played, 90), G(3, "A", GameStatus.Played, 89),
            G(4, "A", GameStatus.Played, 60), G(5, "A", GameStatus.Played, 49), G(6, "A", GameStatus.Played, null),
            G(7, "A", GameStatus.Unplayed, 95, sec: 0), // 遊んでいない作品は数えない
        };
        var bands = TrendStats.ScoreBands(games);
        Assert.Equal(new[] { 2, 1, 0, 1, 0, 1 }, bands.Select(b => b.Count));
        Assert.Equal("90〜100", bands[0].Label);
    }

    [Fact]
    public void SplitGenres_SplitsOnCommonSeparators_AndDedups()
    {
        Assert.Equal(new[] { "ADV", "RPG" }, TrendStats.SplitGenres("ADV/RPG、ADV"));
        Assert.Empty(TrendStats.SplitGenres(""));
        Assert.Empty(TrendStats.SplitGenres(null));
    }

    [Fact]
    public void ByGenre_UsesFetchedTraitsOnly_AndCountsGameUnderEachGenre()
    {
        var games = new[]
        {
            G(1, "A", GameStatus.Played, 80, egs: 10), G(2, "A", GameStatus.Played, 60, egs: 20),
            G(3, "A", GameStatus.Played, 90, egs: 30), // 未取得
            G(4, "A", GameStatus.Unplayed, null, sec: 0, egs: 10), // 遊んでいない
        };
        var traits = new Dictionary<int, GameTraits>
        {
            [10] = new() { EgsId = 10, Genre = "ADV/ノベル" },
            [20] = new() { EgsId = 20, Genre = "ADV" },
        };
        var rows = TrendStats.ByGenre(games, traits);
        Assert.Equal(new[] { "ADV", "ノベル" }, rows.Select(r => r.Name));
        Assert.Equal(2, rows[0].Games);
        Assert.Equal(70, rows[0].AvgScore);
        Assert.Equal((2, 3), TrendStats.GenreCoverage(games, traits));
        Assert.Equal(new[] { 30 }, TrendStats.GenreNeedFetch(games, traits));
    }
}
