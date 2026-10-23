using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>購入額の保存と、手動のプレイ時間の重なり検出</summary>
public sealed class PriceAndOverlapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    static PriceAndOverlapTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public PriceAndOverlapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Price_IsSavedAndRead_AndEmptyStaysNull()
    {
        var db = new Database(Path.Combine(_dir, "games.db"));
        var g = new Game { Title = "A" };
        db.Insert(g);
        Assert.Null(db.Get(g.Id)!.Price);

        g.Price = 3300;
        db.Update(g);
        Assert.Equal(3300, db.Get(g.Id)!.Price);
        Assert.Equal("3,300", db.Get(g.Id)!.PriceText);

        g.Price = null;
        db.Update(g);
        Assert.Null(db.Get(g.Id)!.Price);
        Assert.Equal("", db.Get(g.Id)!.PriceText);
    }

    private static readonly DateTime T = new(2026, 10, 20, 20, 0, 0);

    [Fact]
    public void Overlaps_FindsOnlyRecordsThatShareTime()
    {
        var rows = new[]
        {
            new SessionRow(1, "A", T, T.AddHours(1), 3600),                       // 20:00-21:00
            new SessionRow(2, "B", T.AddHours(3), T.AddHours(4), 3600),           // 23:00-24:00
        };
        Assert.Single(ManualSessionInput.Overlaps(rows, 1, T.AddMinutes(30), 3600));        // 20:30-21:30 は A と重なる
        Assert.Empty(ManualSessionInput.Overlaps(rows, 1, T.AddHours(1), 3600));            // 21:00-22:00 は、ちょうど接するだけ
        Assert.Empty(ManualSessionInput.Overlaps(rows, 1, T.AddHours(1), 3600 * 2 - 1));    // 22:00 までは B にも届かない
    }

    [Fact]
    public void Overlaps_WhenEditing_IgnoresTheRecordBeingEdited()
    {
        var rows = new[] { new SessionRow(1, "A", T, T.AddHours(1), 3600) };
        var self = new SessionEntry(10, T, T.AddHours(1), 3600, true);
        Assert.Empty(ManualSessionInput.Overlaps(rows, 1, T.AddMinutes(10), 1800, ignore: self));
        Assert.Single(ManualSessionInput.Overlaps(rows, 2, T.AddMinutes(10), 1800, ignore: self)); // 別の作品の記録なら、数える
    }
}
