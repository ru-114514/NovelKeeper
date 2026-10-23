using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>管理者権限での起動：起動方法の判定と、作品ごとの印の保存・一括解除</summary>
public sealed class RunAsAdminTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    static RunAsAdminTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public RunAsAdminTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false, false, false, false, false)] // 何も選んでいない
    [InlineData(true, false, false, false, true)]   // 設定で常に管理者
    [InlineData(false, true, false, false, true)]   // その作品の印
    [InlineData(false, false, true, false, true)]   // 今回だけ
    [InlineData(true, true, true, true, false)]     // URL 形式（Steam など）は、何があっても付けない
    public void NeedsAdmin_FollowsTheThreeSwitches_ButNeverForUrls(bool always, bool flag, bool once, bool isUrl, bool expected) =>
        Assert.Equal(expected, PlayTimeTracker.NeedsAdmin(always, flag, once, isUrl));

    [Fact]
    public void RunAsAdmin_IsSavedPerGame_AndDefaultsToOff()
    {
        var db = new Database(Path.Combine(_dir, "games.db"));
        var a = new Game { Title = "A" };
        var b = new Game { Title = "B" };
        db.Insert(a);
        db.Insert(b);
        Assert.False(db.Get(a.Id)!.RunAsAdmin);

        a.RunAsAdmin = true;
        db.Update(a);
        Assert.True(db.Get(a.Id)!.RunAsAdmin);
        Assert.False(db.Get(b.Id)!.RunAsAdmin); // 別の作品には影響しない
    }

    [Fact]
    public void ClearAllRunAsAdmin_ClearsEveryGame_AndReturnsTheCount()
    {
        var db = new Database(Path.Combine(_dir, "games.db"));
        var games = new[] { new Game { Title = "A", RunAsAdmin = true }, new Game { Title = "B" }, new Game { Title = "C" } };
        foreach (var g in games) db.Insert(g);
        games[0].RunAsAdmin = true; games[2].RunAsAdmin = true;
        db.Update(games[0]);
        db.Update(games[2]);

        Assert.Equal(2, db.ClearAllRunAsAdmin());
        Assert.All(db.GetAll(), g => Assert.False(g.RunAsAdmin));
        Assert.Equal(0, db.ClearAllRunAsAdmin());
    }
}
