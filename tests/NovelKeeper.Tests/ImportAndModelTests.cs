using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>一括取り込み（批評空間 → 一覧）の規則のテスト</summary>
public class ImportTests
{
    private static EgsMyGame Item(bool played = false, bool giveUp = false, int? score = null, DateTime? clear = null, bool possession = true) =>
        new(new EgsGame(100, "作品", "ブランド", "2022-05-27", 70, 68, 300), played, clear, possession, giveUp, score);

    [Fact]
    public void NewGame_CopiesSiteInfo_AndRecordsTheSiteState()
    {
        var g = SyncEngine.NewGameFromImport(Item(played: true, score: 85, clear: new DateTime(2026, 3, 1)), null);

        Assert.Equal(100, g.EgsId);
        Assert.Equal("作品", g.Title);
        Assert.Equal("ブランド", g.Brand);
        Assert.Equal(70, g.Median);
        Assert.Equal(68, g.EgsAverage);
        Assert.Equal(300, g.EgsCount);
        Assert.Equal(GameStatus.Played, g.Status);
        Assert.Equal(new DateTime(2026, 3, 1), g.ClearDate);
        Assert.Equal(85, g.MyScore);
        Assert.True(g.EgsPlayed);
        Assert.Equal(85, g.EgsScore);
        Assert.NotNull(g.EgsSyncedAt);
        Assert.Empty(g.PendingReview); // 取り込んだ内容は、反映待ちではない
    }

    [Theory]
    [InlineData(false, false, GameStatus.Unplayed)]
    [InlineData(true, false, GameStatus.Played)]
    [InlineData(false, true, GameStatus.Dropped)]
    [InlineData(true, true, GameStatus.Dropped)] // 放棄は、プレイ済の☑より優先
    public void NewGame_StatusFollowsPlayedAndGiveUp(bool played, bool giveUp, GameStatus expected) =>
        Assert.Equal(expected, SyncEngine.NewGameFromImport(Item(played, giveUp), null).Status);

    [Fact]
    public void NewGame_ClearDate_IsKeptOnlyForPlayedAndNotGivenUp()
    {
        var day = new DateTime(2026, 3, 1);
        Assert.Equal(day, SyncEngine.NewGameFromImport(Item(played: true, clear: day), null).ClearDate);
        Assert.Null(SyncEngine.NewGameFromImport(Item(played: true, giveUp: true, clear: day), null).ClearDate);
        Assert.Null(SyncEngine.NewGameFromImport(Item(played: false, clear: day), null).ClearDate);
    }

    [Fact]
    public void NewGame_UsesRemoteReviewWhenAvailable_OtherwiseOnlyPossession()
    {
        var remote = new Review { Hitokoto = "面白い", Possession = false };
        var withReview = SyncEngine.NewGameFromImport(Item(possession: true), remote);
        Assert.Equal("面白い", withReview.Review.Hitokoto);
        Assert.NotSame(remote, withReview.Review); // 写しを持つ

        var without = SyncEngine.NewGameFromImport(Item(possession: true), null);
        Assert.True(without.Review.Possession);
        Assert.Equal("", without.Review.Hitokoto);
    }

    [Fact]
    public void Existing_FillsScoreOnlyWhenToolHasNone()
    {
        var empty = new Game { EgsId = 100, Title = "A" };
        Assert.True(SyncEngine.ApplyImportToExisting(empty, Item(score: 90), null));
        Assert.Equal(90, empty.MyScore);

        var scored = new Game { EgsId = 100, Title = "B", MyScore = 70 };
        Assert.False(SyncEngine.ApplyImportToExisting(scored, Item(score: 90), null));
        Assert.Equal(70, scored.MyScore);   // ツールの得点は上書きしない
        Assert.Equal(90, scored.EgsScore);  // 批評空間側の得点は記録する

        var noSite = new Game { EgsId = 100, Title = "C" };
        Assert.False(SyncEngine.ApplyImportToExisting(noSite, Item(score: null), null));
        Assert.Null(noSite.MyScore);
    }

    [Fact]
    public void Existing_KeepsUnreflectedToolChanges_WhenApplyingRemoteReview()
    {
        var g = new Game { EgsId = 100, Title = "A" };
        g.Review.Hitokoto = "ツールで書いた";
        g.PendingReview.Add("hitokoto");

        SyncEngine.ApplyImportToExisting(g, Item(), new Review { Hitokoto = "サイトの内容", TotalPlayTime = 12 });

        Assert.Equal("ツールで書いた", g.Review.Hitokoto);
        Assert.Equal(12, g.Review.TotalPlayTime); // 未反映でない項目は取り込む
    }
}

/// <summary>作品モデル（一覧に出す文字・判定）のテスト</summary>
public class GameModelTests
{
    [Theory]
    [InlineData("2026-11-01", true)]
    [InlineData("2026-10-23", false)] // 発売日当日は、発売前ではない
    [InlineData("2020-01-31", false)]
    [InlineData("", false)]
    [InlineData("未定", false)]
    public void IsUnreleased_OnlyWhenSellDayIsAfterToday(string sellDay, bool expected)
        => Assert.Equal(expected, new Game { SellDay = sellDay }.IsUnreleasedOn(new DateTime(2026, 10, 23, 15, 0, 0)));

    [Fact]
    public void Category_UnreleasedWinsOverBeforePurchase_AndOwnedWorksHaveNone()
    {
        var future = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd");
        var unreleased = new Game { Status = GameStatus.Unplayed, SellDay = future, Review = new Review { Possession = false } };
        Assert.True(unreleased.IsBeforePurchase);
        Assert.Equal("発売前", unreleased.Category);

        var before = new Game { Status = GameStatus.Unplayed, SellDay = "2020-01-01", Review = new Review { Possession = false } };
        Assert.Equal("購入前", before.Category);

        var owned = new Game { Status = GameStatus.Unplayed, SellDay = "2020-01-01", Review = new Review { Possession = true } };
        Assert.Equal("", owned.Category);

        var ownedUnreleased = new Game { SellDay = future, Review = new Review { Possession = true } }; // 予約済みなど
        Assert.Equal("発売前", ownedUnreleased.Category);
    }

    [Fact]
    public void IsBeforePurchase_OnlyForUnownedUntouchedGames()
    {
        Game Make() => new() { Status = GameStatus.Unplayed, Review = new Review { Possession = false } };

        Assert.True(Make().IsBeforePurchase);

        var owned = Make(); owned.Review.Possession = true;
        Assert.False(owned.IsBeforePurchase);

        var unknown = Make(); unknown.Review.Possession = null; // 所持が不明（空）でも、一覧の「所持」は空なので購入前として扱う
        Assert.True(unknown.IsBeforePurchase);
        unknown.EgsPossession = true;
        Assert.False(unknown.IsBeforePurchase);

        var launchable = Make(); launchable.ExePath = @"D:\g\a.exe";
        Assert.False(launchable.IsBeforePurchase);

        var played = Make(); played.TotalSeconds = 60;
        Assert.False(played.IsBeforePurchase);

        var siteSaysPlayed = Make(); siteSaysPlayed.EgsPlayed = true;
        Assert.False(siteSaysPlayed.IsBeforePurchase);
    }

    [Fact]
    public void EgsText_DescribesTheSyncState()
    {
        Assert.Equal("未連携", new Game().EgsText);
        Assert.Equal("未同期", new Game { EgsId = 1 }.EgsText);

        var synced = new Game { EgsId = 1, EgsSyncedAt = DateTime.Now };
        Assert.Equal("未プレイ", synced.EgsText);
        synced.EgsPlayed = true;
        Assert.Equal("プレイ済", synced.EgsText);
        synced.EgsClearDate = new DateTime(2026, 3, 1);
        Assert.Equal("プレイ済 2026/03/01", synced.EgsText);
        synced.EgsGiveUp = true;
        Assert.Equal("放棄", synced.EgsText);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(59, "0:00:59")]
    [InlineData(3725, "1:02:05")]
    [InlineData(36000, "10:00:00")]
    public void PlayTimeText_FormatsAsClock(long seconds, string expected) =>
        Assert.Equal(expected, new Game { TotalSeconds = seconds }.PlayTimeText);

    [Fact]
    public void LaunchText_ShowsWhatWillBeLaunched()
    {
        Assert.Equal("⬇ ここにドロップ", new Game().LaunchText);
        Assert.Equal("Steam", new Game { ExePath = "steam://rungameid/1" }.LaunchText);
        Assert.Equal("a.exe", new Game { ExePath = @"D:\g\a.exe" }.LaunchText);

        var named = new Game { ExePath = @"D:\g\a.exe" };
        named.Targets.Add(new LaunchTarget { Path = @"D:\g\a.exe", Label = "本編" });
        Assert.Equal("本編", named.LaunchText);
    }

    [Fact]
    public void StatusOrder_PlayingThenUnplayedThenDroppedThenPlayed()
    {
        var order = new[] { GameStatus.Played, GameStatus.Dropped, GameStatus.Unplayed, GameStatus.Playing }
            .OrderBy(s => new Game { Status = s }.StatusOrder).ToList();
        Assert.Equal([GameStatus.Playing, GameStatus.Unplayed, GameStatus.Dropped, GameStatus.Played], order);
    }
}
