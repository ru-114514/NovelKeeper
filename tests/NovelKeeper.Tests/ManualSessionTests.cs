using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>プレイ時間の手動補正：入力の検証と、DB への追加・編集・削除</summary>
public sealed class ManualSessionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0);
    private static readonly DateTime Day = new(2026, 10, 20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    static ManualSessionTests() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public ManualSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Database NewDb(string name = "games.db") => new(Path.Combine(_dir, name));

    // ---- 入力の検証 ----

    [Fact]
    public void ValidInput_BuildsStartAndSeconds()
    {
        var ok = ManualSessionInput.TryBuild(Day, "20:30", "2", "15", Now, out var start, out var seconds, out var error);

        Assert.True(ok, error);
        Assert.Equal(new DateTime(2026, 10, 20, 20, 30, 0), start);
        Assert.Equal(2 * 3600 + 15 * 60, seconds);
    }

    [Fact]
    public void FullWidthDigitsAndColon_AreAccepted_AndBlankHoursOrMinutesMeanZero()
    {
        Assert.True(ManualSessionInput.TryBuild(Day, "２０：３０", "", "４５", Now, out var start, out var seconds, out _));
        Assert.Equal(new DateTime(2026, 10, 20, 20, 30, 0), start);
        Assert.Equal(45 * 60, seconds);

        Assert.True(ManualSessionInput.TryBuild(Day, "8:05", "１", "", Now, out start, out seconds, out _));
        Assert.Equal(new DateTime(2026, 10, 20, 8, 5, 0), start);
        Assert.Equal(3600, seconds);
    }

    [Theory]
    [InlineData("", "1", "0")]        // 開始時刻なし
    [InlineData("25:00", "1", "0")]   // 時刻が不正
    [InlineData("20:30", "", "")]     // 時間が 0
    [InlineData("20:30", "0", "0")]
    [InlineData("20:30", "-1", "0")]  // 負の数
    [InlineData("20:30", "1.5", "0")] // 小数
    [InlineData("20:30", "abc", "0")]
    [InlineData("20:30", "1", "60")]  // 分は 59 まで
    [InlineData("20:30", "25", "0")]  // 24 時間を超える
    [InlineData("20:30", "24", "1")]  // ちょうど 24 時間までなので、1 分でも超えると不可
    public void InvalidInput_IsRejected_WithAMessage(string time, string hours, string minutes)
    {
        var ok = ManualSessionInput.TryBuild(Day, time, hours, minutes, Now, out _, out var seconds, out var error);

        Assert.False(ok);
        Assert.NotEmpty(error);
        Assert.Equal(0, seconds);
    }

    [Fact]
    public void Exactly24Hours_IsAccepted()
    {
        Assert.True(ManualSessionInput.TryBuild(Day, "00:00", "24", "0", Now, out _, out var seconds, out _));
        Assert.Equal(24 * 3600, seconds);
    }

    [Fact]
    public void EndInTheFuture_IsRejected()
    {
        // 現在 12:00 に対して、11:00 開始の 2 時間は、終わりが 13:00 で未来
        var ok = ManualSessionInput.TryBuild(Now.Date, "11:00", "2", "0", Now, out _, out _, out var error);
        Assert.False(ok);
        Assert.Contains("現在より先", error);

        // 終わりがちょうど現在なら良い
        Assert.True(ManualSessionInput.TryBuild(Now.Date, "10:00", "2", "0", Now, out _, out _, out _));
    }

    // ---- DB ----

    [Fact]
    public void AddManualSession_IsCountedInTotal_AndMarkedManual()
    {
        var db = NewDb();
        var g = new Game { Title = "A" };
        db.Insert(g);
        db.AddSession(g.Id, Day.AddHours(10), Day.AddHours(11), 3600); // 自動の記録

        db.AddManualSession(g.Id, Day.AddHours(20), 5400);

        Assert.Equal(3600 + 5400, db.Get(g.Id)!.TotalSeconds);
        var entries = db.GetGameSessions(g.Id);
        Assert.Equal(2, entries.Count);
        Assert.Equal(Day.AddHours(20), entries[0].Start);                    // 新しいものが先
        Assert.Equal(Day.AddHours(20).AddSeconds(5400), entries[0].End);     // 終わりは開始＋時間
        Assert.True(entries[0].Manual);
        Assert.False(entries[1].Manual);
    }

    [Fact]
    public void UpdateManualSession_ChangesTimeAndSeconds_AndMarksItManual()
    {
        var db = NewDb();
        var g = new Game { Title = "A" };
        db.Insert(g);
        var id = db.AddSession(g.Id, Day.AddHours(10), Day.AddHours(11), 3600); // 自動

        db.UpdateManualSession(id, Day.AddDays(1).AddHours(9), 1800);

        var e = db.GetGameSessions(g.Id).Single();
        Assert.Equal(Day.AddDays(1).AddHours(9), e.Start);
        Assert.Equal(1800, e.Seconds);
        Assert.True(e.Manual);
        Assert.Equal(1800, db.Get(g.Id)!.TotalSeconds);
    }

    [Fact]
    public void DeleteSession_RemovesItFromTotal_AndOnlyForThatGame()
    {
        var db = NewDb();
        var a = new Game { Title = "A" };
        var b = new Game { Title = "B" };
        db.Insert(a);
        db.Insert(b);
        var id = db.AddManualSession(a.Id, Day.AddHours(10), 3600);
        db.AddManualSession(b.Id, Day.AddHours(10), 7200);

        db.DeleteSession(id);

        Assert.Equal(0, db.Get(a.Id)!.TotalSeconds);
        Assert.Empty(db.GetGameSessions(a.Id));
        Assert.Equal(7200, db.Get(b.Id)!.TotalSeconds);
        Assert.Single(db.GetGameSessions(b.Id));
    }

    [Fact]
    public void ManualSession_AppearsInReport_AtItsHour_AndSplitsAcrossMidnight()
    {
        var db = NewDb();
        var g = new Game { Title = "A" };
        db.Insert(g);
        db.AddManualSession(g.Id, Day.AddHours(23), 7200); // 23:00〜翌 01:00

        var rows = db.GetSessions(Day, Day.AddDays(2));
        var day1 = SummaryCalculator.Build(rows, Day, Day);
        var day2 = SummaryCalculator.Build(rows, Day.AddDays(1), Day.AddDays(1));

        Assert.Equal(3600, day1.TotalSeconds, 3); // 日またぎは日ごとに按分
        Assert.Equal(3600, day2.TotalSeconds, 3);
    }

    [Fact]
    public void FirstPlayed_FollowsTheEarliestRecord_IncludingManualOnes()
    {
        var db = NewDb();
        var g = new Game { Title = "A" };
        db.Insert(g);
        db.AddSession(g.Id, Day.AddHours(10), Day.AddHours(11), 3600);

        db.AddManualSession(g.Id, Day.AddDays(-5).AddHours(20), 3600);

        Assert.Equal(Day.AddDays(-5).AddHours(20), db.Get(g.Id)!.FirstPlayed);
    }

    [Fact]
    public void OldDatabaseWithoutManualColumn_IsUpgraded_KeepingExistingRecords()
    {
        // 補正機能より前の DB（sessions に manual 列がない）を作る
        var path = Path.Combine(_dir, "old.db");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE games (id INTEGER PRIMARY KEY AUTOINCREMENT, egs_id INTEGER UNIQUE, title TEXT NOT NULL, brand TEXT,
                    sellday TEXT, median INTEGER, exe_path TEXT, monitor_process TEXT, status INTEGER NOT NULL DEFAULT 0,
                    clear_date TEXT, egs_played INTEGER NOT NULL DEFAULT 0, egs_clear_date TEXT, egs_synced_at TEXT);
                CREATE TABLE sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, game_id INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                    started_at TEXT NOT NULL, ended_at TEXT NOT NULL, seconds INTEGER NOT NULL);
                INSERT INTO games (title) VALUES ('旧データ');
                INSERT INTO sessions (game_id, started_at, ended_at, seconds) VALUES (1, '2026-10-20 10:00:00', '2026-10-20 11:00:00', 3600);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var db = new Database(path); // 開くときに列が足される

        var e = db.GetGameSessions(1).Single();
        Assert.Equal(3600, e.Seconds);
        Assert.False(e.Manual); // 既存の記録は「自動」
        db.AddManualSession(1, Day.AddHours(20), 600);
        Assert.Equal(4200, db.Get(1)!.TotalSeconds);
    }
}
