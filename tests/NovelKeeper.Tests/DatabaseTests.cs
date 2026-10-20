using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>DB と自動バックアップのテスト（アプリ本体と同じ winsqlite3 を使う）</summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    static DatabaseTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public DatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 一時ファイルなので残っても害はない */ }
    }

    private Database NewDb(out string path)
    {
        path = Path.Combine(_dir, "games.db");
        return new Database(path);
    }

    [Fact]
    public void Get_ReturnsOnlyTheRequestedGame()
    {
        var db = NewDb(out _);
        var a = new Game { Title = "A", EgsId = 1 };
        var b = new Game { Title = "B", EgsId = 2 };
        db.Insert(a);
        db.Insert(b);

        Assert.Equal("B", db.Get(b.Id)?.Title);
        Assert.Null(db.Get(9999));
        Assert.Equal(2, db.GetAll().Count);
    }

    [Fact]
    public void SessionsAreSummedIntoTotalSeconds()
    {
        var db = NewDb(out _);
        var g = new Game { Title = "A" };
        db.Insert(g);
        db.AddSession(g.Id, DateTime.Now, DateTime.Now, 600);
        db.AddSession(g.Id, DateTime.Now, DateTime.Now, 300);

        Assert.Equal(900, db.Get(g.Id)!.TotalSeconds);
    }

    [Fact]
    public void Backup_CreatesReadableCopy_OncePerDay()
    {
        var db = NewDb(out var path);
        db.Insert(new Game { Title = "バックアップ対象" });
        var backups = Path.Combine(_dir, "backups");

        DbBackup.Run(path, backups);
        DbBackup.Run(path, backups); // 同じ日に2回目は何もしない

        var files = Directory.GetFiles(backups);
        var copy = Assert.Single(files);
        Assert.Equal("バックアップ対象", new Database(copy).GetAll().Single().Title);
    }

    [Fact]
    public void Backup_KeepsOnlyTheNewestSevenDays_AndLeavesNoTempFile()
    {
        var db = NewDb(out var path);
        db.Insert(new Game { Title = "A" });
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        for (var i = 1; i <= 9; i++)
            File.WriteAllText(Path.Combine(backups, $"games_2000010{i}.db"), "old");

        DbBackup.Run(path, backups);

        var names = Directory.GetFiles(backups).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(7, names.Count);
        Assert.Contains($"games_{DateTime.Now:yyyyMMdd}.db", names);
        Assert.DoesNotContain(names, n => n!.EndsWith(".tmp"));
        Assert.DoesNotContain("games_20000101.db", names); // 古いものから消える
    }

    [Fact]
    public void Backup_MissingDatabase_DoesNothing()
    {
        DbBackup.Run(Path.Combine(_dir, "none.db"), Path.Combine(_dir, "backups"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "backups")));
    }
}
