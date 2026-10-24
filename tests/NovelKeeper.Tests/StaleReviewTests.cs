using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Xunit;

namespace NovelKeeper.Tests;

public class StaleReviewTests
{
    private static readonly DateTime Today = new(2026, 10, 25);

    private static Game G(long id, GameStatus status, string? sell = null, DateTime? first = null) =>
        new() { Id = id, Title = $"G{id}", Status = status, SellDay = sell, FirstPlayed = first, Review = new() { Possession = true } };

    [Fact]
    public void Playing_NeedsTheThresholdOfDays_FromTheLastSessionOrFirstPlay()
    {
        var games = new[]
        {
            G(1, GameStatus.Playing), G(2, GameStatus.Playing), G(3, GameStatus.Playing, first: Today.AddDays(-200)), G(4, GameStatus.Playing),
        };
        var last = new Dictionary<long, DateTime> { [1] = Today.AddDays(-100), [2] = Today.AddDays(-10) };
        var r = StaleReview.Candidates(games, last, Today);
        Assert.Equal(new long[] { 4, 3, 1 }, r.Select(i => i.Game.Id)); // 記録のない4 → 古い順。最近遊んだ2は出ない
        Assert.Null(r[0].Days);
        Assert.Equal(100, r[2].Days);
    }

    [Fact]
    public void Unplayed_NeedsReleaseOlderThanYears_AndCountsAsBacklog()
    {
        var games = new[]
        {
            G(1, GameStatus.Unplayed, "2024-05-01"), G(2, GameStatus.Unplayed, "2026-03-01"), G(3, GameStatus.Unplayed, "2020-01-01"),
            G(4, GameStatus.Unplayed, "2030-01-01"), G(5, GameStatus.Unplayed, null), G(6, GameStatus.Played, "2020-01-01"),
        };
        var r = StaleReview.Candidates(games, new Dictionary<long, DateTime>(), Today);
        Assert.Equal(new long[] { 3, 1 }, r.Select(i => i.Game.Id)); // 発売の古い順。未発売・発売日不明・遊び済みは出ない
    }

    [Fact]
    public void Excluded_AreNotListedAgain_AndNotCountedInBacklog()
    {
        var games = new[] { G(1, GameStatus.Unplayed, "2020-01-01"), G(2, GameStatus.Unplayed, "2020-01-01") };
        var ex = new HashSet<long> { 1 };
        Assert.Equal(new long[] { 2 }, StaleReview.Candidates(games, new Dictionary<long, DateTime>(), Today, backlogExcluded: ex).Select(i => i.Game.Id));
        Assert.Equal(2, SummaryExtras.BacklogCount(games));
        Assert.Equal(1, SummaryExtras.BacklogCount(games, ex));
    }
}
