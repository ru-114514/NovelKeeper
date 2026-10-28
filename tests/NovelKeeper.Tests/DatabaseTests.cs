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
    public void SaveImport_UpdatesExistingAndAddsNewGames_WithSyncStateAndLaunchTarget()
    {
        var db = NewDb(out _);
        var old = new Game { Title = "既存", EgsId = 1 };
        db.Insert(old);
        old.MyScore = 80;
        old.EgsPlayed = true;
        var added = new Game { Title = "追加", EgsId = 2, EgsPlayed = true, ExePath = @"C:Games.exe" };

        db.SaveImport([old], [added]);

        var all = db.GetAll().ToDictionary(g => g.EgsId!.Value);
        Assert.Equal(80, all[1].MyScore);
        Assert.True(all[1].EgsPlayed);
        Assert.True(all[2].EgsPlayed); // 批評空間側の状態（egs_*）も書き込む
        Assert.Equal(@"C:Games.exe", Assert.Single(all[2].Targets).Path); // 起動するものも同じトランザクションで追加する
    }

    [Fact]
    public void UpdateFromSync_Batch_WritesNothingIfOneGameFails()
    {
        var db = NewDb(out _);
        var a = new Game { Title = "A", EgsId = 1 };
        var b = new Game { Title = "B", EgsId = 2 };
        db.Insert(a);
        db.Insert(b);
        a.MyScore = 70;
        b.MyScore = 60;
        b.PendingReview = null!; // 書き込みの途中（B の得点を書いたあと）で失敗させる

        Assert.ThrowsAny<Exception>(() => db.UpdateFromSync([a, b]));

        Assert.All(db.GetAll(), g => Assert.Null(g.MyScore)); // 途中まで書き込まれた状態は残らない
    }

    [Fact]
    public void Delete_RemovesTheGameWithItsSessionsAndTargets()
    {
        var db = NewDb(out var path);
        var g = new Game { Title = "A", ExePath = @"C:.exe" };
        db.Insert(g);
        db.Update(g);
        db.AddSession(g.Id, DateTime.Now, DateTime.Now, 60);

        db.Delete(g.Id);

        Assert.Empty(db.GetAll());
        using var c = new SqliteConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM sessions) + (SELECT COUNT(*) FROM launch_targets)";
        Assert.Equal(0L, cmd.ExecuteScalar());
    }

    [Fact]
    public void RegisteredDate_IsSavedOnInsert_AndKeptWhenEgsHasNoRecord()
    {
        var db = NewDb(out _);
        var g = new Game { Title = "A", EgsId = 1, EgsRegistered = new DateTime(2026, 10, 25) };
        db.Insert(g);
        var read = db.Get(g.Id)!;
        Assert.Equal(new DateTime(2026, 10, 25), read.EgsRegistered);

        SyncEngine.ApplyEgsState(read, null); // 批評空間に記録がない → 追加した日を残す
        Assert.Equal(new DateTime(2026, 10, 25), read.EgsRegistered);
        SyncEngine.ApplyEgsState(read, new EgsUserRecord(1, false, null, false, null, true, new DateTime(2024, 3, 1))); // あれば、そちらに合わせる
        Assert.Equal(new DateTime(2024, 3, 1), read.EgsRegistered);
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
