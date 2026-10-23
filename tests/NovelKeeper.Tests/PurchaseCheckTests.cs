using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

public class PurchaseCheckTests
{
    private static readonly DateTime Today = new(2026, 10, 23, 15, 0, 0);

    private static Game Planned(string sellDay, string will = "0_必ず購入", string title = "作品") => new()
    {
        Title = title, SellDay = sellDay, Status = GameStatus.Unplayed,
        Review = new Review { Possession = false, PurchaseWill = will },
    };

    [Theory]
    [InlineData("2026-10-22", "0_必ず購入", true)]   // 発売済み・購入予定あり・所持なし
    [InlineData("2026-10-23", "多分購入", true)]     // 発売日当日も発売済み
    [InlineData("2020-01-01", "様子見", true)]       // 「様子見」も含める
    [InlineData("2026-10-24", "0_必ず購入", false)]  // まだ発売前
    [InlineData("2020-01-01", "", false)]            // 購入予定が「ー」
    [InlineData("未定", "0_必ず購入", false)]         // 発売日が読めない
    [InlineData("", "0_必ず購入", false)]
    public void NeedsPurchaseCheck_ReleasedAndPlannedButNotOwned(string sellDay, string will, bool expected)
        => Assert.Equal(expected, Planned(sellDay, will).NeedsPurchaseCheckOn(Today));

    [Fact]
    public void PurchaseCheckText_ShowsYoukakuninOnlyForTargets()
    {
        Assert.Equal("要確認", Planned("2020-01-01").PurchaseCheckText);
        Assert.Equal("", Planned("2030-01-01").PurchaseCheckText); // 発売前は対象外
        Assert.Equal("", Planned("2020-01-01", "").PurchaseCheckText);
    }

    [Fact]
    public void NeedsPurchaseCheck_GoesAway_WhenOwnedPlayedOrSetUp()
    {
        Assert.True(Planned("2020-01-01").NeedsPurchaseCheckOn(Today));

        var owned = Planned("2020-01-01"); owned.Review.Possession = true;           // 買って「所持」を付けた
        Assert.False(owned.NeedsPurchaseCheckOn(Today));

        var launchable = Planned("2020-01-01"); launchable.ExePath = @"D:\g\a.exe";  // 起動設定がある
        Assert.False(launchable.NeedsPurchaseCheckOn(Today));

        var played = Planned("2020-01-01"); played.TotalSeconds = 60;                // 遊んだ記録がある
        Assert.False(played.NeedsPurchaseCheckOn(Today));

        var done = Planned("2020-01-01"); done.Status = GameStatus.Played; done.EgsPlayed = true;
        Assert.False(done.NeedsPurchaseCheckOn(Today));
    }

    [Fact]
    public void Csv_KeepsWorksThatNeedACheck_EvenWhenBeforePurchaseWorksAreHidden()
    {
        var needs = Planned("2020-01-01", title: "確認");
        var hidden = Planned("2020-01-01", "", "非表示");
        Assert.Equal(["確認"], CsvExporter.SelectAll([needs, hidden], showBeforePurchase: false).Select(g => g.Title));
    }
}
