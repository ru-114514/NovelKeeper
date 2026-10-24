using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Xunit;

namespace NovelKeeper.Tests;

public class GameTraitsTests
{
    private static Game G(long id, int? egs, int? median, GameStatus status = GameStatus.Unplayed, int? count = null) =>
        new() { Id = id, EgsId = egs, Title = $"G{id}", Median = median, Status = status, EgsCount = count };

    private static GameTraits T(int egs, int? hours = null, string genre = "", params string[] pov) =>
        new() { EgsId = egs, PlayHours = hours, Genre = genre, Pov = pov.ToList(), FetchedAt = new DateTime(2026, 10, 1) };

    private static Dictionary<int, GameTraits> Map(params GameTraits[] t) => t.ToDictionary(x => x.EgsId);

    [Fact]
    public void Default_MinMedianIs80()
    {
        Assert.Equal(80, new TraitFilter().MinMedian);
    }

    [Fact]
    public void Base_NeedsEgsId_StatusMedianAndDataCount_AndSkipsUnreleased()
    {
        var f = new TraitFilter { MinMedian = 70 };
        Assert.True(TraitMatcher.IsBase(G(1, 1, 75), f));
        Assert.False(TraitMatcher.IsBase(G(2, 2, 69), f));
        Assert.False(TraitMatcher.IsBase(G(3, null, 90), f));
        Assert.False(TraitMatcher.IsBase(G(4, 4, 90, GameStatus.Played), f));
        Assert.False(TraitMatcher.IsBase(G(5, 5, null), f));
        Assert.True(TraitMatcher.IsBase(G(6, 6, 80, GameStatus.Playing), f));
        f.IncludePlaying = false;
        Assert.False(TraitMatcher.IsBase(G(6, 6, 80, GameStatus.Playing), f));
        var unreleased = G(7, 7, 80);
        unreleased.SellDay = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
        Assert.False(TraitMatcher.IsBase(unreleased, new TraitFilter()));

        var c = new TraitFilter { MinMedian = null, MinDataCount = 10 };
        Assert.True(TraitMatcher.IsBase(G(8, 8, 90, count: 10), c));
        Assert.False(TraitMatcher.IsBase(G(9, 9, 90, count: 9), c));
        Assert.False(TraitMatcher.IsBase(G(10, 10, 90), c)); // データ数が不明なら合わない
    }

    [Fact]
    public void Traits_BothPovsMustMatch_AndGenreAndHours()
    {
        var t = T(1, 20, "ADV/ノベル", "泣き", "燃え");
        Assert.True(TraitMatcher.MatchesTraits(t, new TraitFilter { Pov1 = "泣き" }));
        Assert.True(TraitMatcher.MatchesTraits(t, new TraitFilter { Pov1 = "泣き", Pov2 = "燃え" }));
        Assert.False(TraitMatcher.MatchesTraits(t, new TraitFilter { Pov1 = "泣き", Pov2 = "萌え" })); // 第一と第二は「かつ」
        Assert.True(TraitMatcher.MatchesTraits(t, new TraitFilter { Genre = "adv" }));
        Assert.False(TraitMatcher.MatchesTraits(t, new TraitFilter { Genre = "RPG" }));
        Assert.True(TraitMatcher.MatchesTraits(t, new TraitFilter { MaxPlayHours = 20 }));
        Assert.False(TraitMatcher.MatchesTraits(t, new TraitFilter { MaxPlayHours = 10 }));
        Assert.False(TraitMatcher.MatchesTraits(T(2), new TraitFilter { MaxPlayHours = 10 })); // 時間が不明なら合わない
        Assert.True(TraitMatcher.MatchesTraits(null, new TraitFilter()));                      // 条件がなければ、特徴が未取得でも合う
        Assert.False(TraitMatcher.MatchesTraits(null, new TraitFilter { Pov1 = "泣き" }));
    }

    [Fact]
    public void Select_FiltersByTraits_SkipsUntrackedGames_AndSortsByMedian()
    {
        var games = new List<Game> { G(1, 1, 82), G(2, 2, 90), G(3, 3, 85), G(4, 4, 88) };
        var traits = Map(T(1, null, "", "泣き"), T(2, null, "", "泣き"), T(3, null, "", "燃え"));
        var r = TraitMatcher.Select(games, traits, new TraitFilter { Pov1 = "泣き" });
        Assert.Equal(new long[] { 2, 1 }, r.Select(g => g.Id)); // 特徴を取得していない4は、特徴の条件があると出ない
        Assert.Equal(4, TraitMatcher.Select(games, traits, new TraitFilter()).Count);
    }

    [Fact]
    public void PovChoices_CountOnlyGamesThatKeepMatchingTheOtherPov_AndKeepTheSelectedOne()
    {
        var games = new List<Game> { G(1, 1, 85), G(2, 2, 85), G(3, 3, 85), G(4, 4, 85, GameStatus.Played) };
        var traits = Map(T(1, null, "", "泣き", "燃え"), T(2, null, "", "泣き"), T(3, null, "", "燃え", "萌え"), T(4, null, "", "泣き", "燃え"));

        var none = TraitMatcher.PovChoices(games, traits, new TraitFilter(), second: false);
        Assert.Equal(3, none.Count);
        Assert.Contains(("泣き", 2), none);
        Assert.Contains(("燃え", 2), none);
        Assert.Contains(("萌え", 1), none);

        // 第一に「泣き」を選ぶと、第二の選択肢は「泣き」の作品（1・2）の中での本数になる（遊び済みの4は数えない）
        var second = TraitMatcher.PovChoices(games, traits, new TraitFilter { Pov1 = "泣き" }, second: true);
        Assert.Contains(("泣き", 2), second);
        Assert.Contains(("燃え", 1), second);
        Assert.DoesNotContain(second, x => x.Name == "萌え");

        // 選択中の POV は、0本でも選択肢に残す
        var kept = TraitMatcher.PovChoices(games, traits, new TraitFilter { Pov1 = "泣き", Pov2 = "萌え" }, second: true);
        Assert.Contains(("萌え", 0), kept);
    }

    [Fact]
    public void KeptNegativePovs_AreTheThreeScenarioOnes()
    {
        Assert.Equal(new[] { 152, 106, 528 }, TraitMatcher.KeptNegativePovIds);
    }

    [Fact]
    public void NeedFetch_ListsMissingAndStale()
    {
        var now = new DateTime(2026, 10, 25);
        var games = new List<Game> { G(1, 1, 80), G(2, 2, 80), G(3, 3, 80) };
        var traits = Map(T(1), new GameTraits { EgsId = 2, FetchedAt = now.AddDays(-TraitStore.StaleDays - 1) });
        Assert.Equal(new[] { 2, 3 }, TraitStore.NeedFetch(games, traits, now).OrderBy(x => x));
    }

    [Fact]
    public void Store_RoundTrips_DropsOldVersions_AndSurvivesABrokenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"traits_{Guid.NewGuid():N}.json");
        try
        {
            TraitStore.Save([T(5, 12, "ADV", "泣き"), new GameTraits { Version = 1, EgsId = 6 }], path);
            var back = TraitStore.Load(path);
            Assert.Equal(["泣き"], back[5].Pov);
            Assert.Equal(12, back[5].PlayHours);
            Assert.False(back.ContainsKey(6)); // 古い版（タグを含む）は読み込まず、取得し直す
            File.WriteAllText(path, "{壊れている");
            Assert.Empty(TraitStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_PadsNextPickPresetsToThree()
    {
        var cfg = new EgsConfig { NextPickPresets = new() };
        cfg.NormalizeSortPresets();
        Assert.Equal(3, cfg.NextPickPresets.Count);
        Assert.All(cfg.NextPickPresets, p => Assert.Null(p));
    }
}
