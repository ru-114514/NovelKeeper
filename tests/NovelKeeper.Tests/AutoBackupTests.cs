using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

/// <summary>自動バックアップ：世代・件数・大きさ・空き容量の上限、検査、復元の準備のテスト</summary>
public sealed class AutoBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NK_autobackup_" + Guid.NewGuid().ToString("N"));
    private string BackupDir => Path.Combine(_dir, "backups");
    private string DbPath => Path.Combine(_dir, "games.db");
    private string CfgPath => Path.Combine(_dir, "egs_config.json");

    private static readonly Func<string, long> PlentyOfSpace = _ => long.MaxValue;

    public AutoBackupTests()
    {
        Directory.CreateDirectory(_dir);
        var db = new Database(DbPath);
        db.Insert(new Game { Title = "元の作品" });
        File.WriteAllText(CfgPath, "{\"Theme\":\"Dark\"}");
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AutoBackupResult Run(DateTime day, AutoBackupSettings? s = null, bool force = false, Func<string, long>? free = null, long maxTotal = AutoBackup.MaxTotalBytes) =>
        AutoBackup.Run(DbPath, CfgPath, BackupDir, s ?? new AutoBackupSettings(), force, day, free ?? PlentyOfSpace, maxTotal);

    private static DateTime D(int day) => new(2026, 10, day, 9, 0, 0);

    private void AddGame(string title)
    {
        new Database(DbPath).Insert(new Game { Title = title });
        SqliteConnection.ClearAllPools();
    }

    private string[] Names() => Directory.Exists(BackupDir)
        ? Directory.GetFiles(BackupDir).Select(Path.GetFileName).OfType<string>().OrderBy(x => x).ToArray()
        : Array.Empty<string>();

    // ---- 作る ----

    [Fact]
    public void Run_CopiesDbAndConfig_AndLeavesNoStageFile()
    {
        Assert.Equal(AutoBackupResult.Created, Run(D(1)));
        Assert.Equal(new[] { "config_20261001.json", "games_20261001.db" }, Names());
        Assert.Equal("元の作品", new Database(Path.Combine(BackupDir, "games_20261001.db")).GetAll().Single().Title);
        Assert.Equal("{\"Theme\":\"Dark\"}", File.ReadAllText(Path.Combine(BackupDir, "config_20261001.json")));
    }

    [Fact]
    public void Run_DoesNotCopyCredentials()
    {
        File.WriteAllText(Path.Combine(_dir, "credential.bin"), "secret");
        Run(D(1));
        Assert.DoesNotContain(Names(), n => n.Contains("credential"));
    }

    [Fact]
    public void Run_Disabled_DoesNothing_UnlessForced()
    {
        var off = new AutoBackupSettings { Enabled = false };
        Assert.Equal(AutoBackupResult.Disabled, Run(D(1), off));
        Assert.Empty(Names());
        Assert.Equal(AutoBackupResult.Created, Run(D(1), off, force: true)); // 「今すぐバックアップ」
    }

    [Fact]
    public void Run_MissingOrEmptyDb_DoesNothing()
    {
        File.WriteAllBytes(DbPath, Array.Empty<byte>());
        Assert.Equal(AutoBackupResult.NoDatabase, Run(D(1)));
        File.Delete(DbPath);
        Assert.Equal(AutoBackupResult.NoDatabase, Run(D(1)));
        Assert.False(Directory.Exists(BackupDir));
    }

    // ---- 間隔・変更なし ----

    [Fact]
    public void Run_SameDay_IsNotDue()
    {
        Run(D(1));
        AddGame("追加");
        Assert.Equal(AutoBackupResult.NotDue, Run(D(1)));
    }

    [Fact]
    public void Run_WeeklyInterval_WaitsSevenDays()
    {
        var weekly = new AutoBackupSettings { IntervalDays = 7 };
        Run(D(1), weekly);
        AddGame("追加");
        Assert.Equal(AutoBackupResult.NotDue, Run(D(7), weekly)); // 6日後
        Assert.Equal(AutoBackupResult.Created, Run(D(8), weekly)); // 7日後
    }

    [Fact]
    public void Run_UnchangedDb_DoesNotCreateAnotherGeneration()
    {
        Run(D(1));
        Assert.Equal(AutoBackupResult.Unchanged, Run(D(2)));
        Assert.Equal(2, Names().Length); // games_20261001.db と config_20261001.json のまま
        Assert.DoesNotContain(Names(), n => n.StartsWith("games_20261002"));
    }

    [Fact]
    public void Run_ChangedDb_CreatesNewGeneration()
    {
        Run(D(1));
        AddGame("追加");
        Assert.Equal(AutoBackupResult.Created, Run(D(2)));
        Assert.Contains("games_20261002.db", Names());
    }

    [Fact]
    public void Run_ForcedSameDay_ReplacesTodaysFile_WithoutGrowing()
    {
        Run(D(1));
        AddGame("追加");
        Assert.Equal(AutoBackupResult.Created, Run(D(1), force: true));
        Assert.Equal(2, Names().Length);
        Assert.Equal(2, new Database(Path.Combine(BackupDir, "games_20261001.db")).GetAll().Count);
    }

    // ---- 上限 ----

    [Fact]
    public void Prune_KeepsOnlyTheNewestGenerations_AndTheirConfigs()
    {
        var s = new AutoBackupSettings { Keep = 3 };
        for (var d = 1; d <= 6; d++) { AddGame("g" + d); Run(D(d), s); }

        var names = Names();
        Assert.Equal(new[] { "config_20261004.json", "config_20261005.json", "config_20261006.json",
                             "games_20261004.db", "games_20261005.db", "games_20261006.db" }, names);
    }

    [Fact]
    public void Prune_NeverDeletesFilesItDidNotCreate()
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(Path.Combine(BackupDir, "メモ.txt"), "x");
        File.WriteAllText(Path.Combine(BackupDir, "games_notes.db"), "x");
        File.WriteAllText(Path.Combine(BackupDir, "my_games_20200101.db"), "x");
        File.WriteAllText(Path.Combine(BackupDir, "before_x.db"), "x");
        var s = new AutoBackupSettings { Keep = 3 };
        for (var d = 1; d <= 5; d++) { AddGame("g" + d); Run(D(d), s); }

        var names = Names();
        Assert.Contains("メモ.txt", names);
        Assert.Contains("games_notes.db", names);
        Assert.Contains("my_games_20200101.db", names);
        Assert.Contains("before_x.db", names);
    }

    [Fact]
    public void Prune_LimitsBeforeSnapshots_ToNewestFive()
    {
        Directory.CreateDirectory(BackupDir);
        for (var i = 1; i <= 8; i++)
            File.WriteAllText(Path.Combine(BackupDir, $"before_import_2026100{i}_120000.db"), "old");
        File.WriteAllText(Path.Combine(BackupDir, "before_cleanup_20261009_120000.db"), "old");

        Run(D(10));

        var befores = Names().Where(n => n.StartsWith("before_")).ToArray();
        Assert.Equal(AutoBackup.MaxBefore, befores.Length);
        Assert.Contains("before_cleanup_20261009_120000.db", befores); // 新しいものが残る
        Assert.DoesNotContain("before_import_20261001_120000.db", befores);
    }

    [Fact]
    public void Prune_TotalSizeCap_DeletesOldestFirst_ButKeepsTheNewestGeneration()
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllBytes(Path.Combine(BackupDir, "before_import_20261001_120000.db"), new byte[4000]);
        File.WriteAllBytes(Path.Combine(BackupDir, "games_20260901.db"), new byte[4000]);
        File.WriteAllBytes(Path.Combine(BackupDir, "games_20260902.db"), new byte[4000]);

        Run(D(10), maxTotal: 1000); // 上限をわざと極端に小さくする

        var names = Names();
        Assert.Contains("games_20261010.db", names); // 最新の1世代は、上限を超えても残す
        Assert.DoesNotContain("before_import_20261001_120000.db", names);
        Assert.DoesNotContain("games_20260901.db", names);
        Assert.DoesNotContain("games_20260902.db", names);
    }

    [Fact]
    public void Prune_RemovesOrphanConfigs()
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(Path.Combine(BackupDir, "config_20200101.json"), "{}");
        Run(D(1));
        Assert.DoesNotContain("config_20200101.json", Names());
    }

    [Fact]
    public void Prune_KeepAlwaysAtLeastOne()
    {
        Run(D(1));
        AddGame("x");
        Run(D(2));
        AutoBackup.Prune(BackupDir, 0, 0, 1);
        Assert.Single(AutoBackup.List(BackupDir));
        Assert.Equal(new DateTime(2026, 10, 2), AutoBackup.List(BackupDir)[0].Date);
    }

    // ---- 負荷を避ける ----

    [Fact]
    public void Run_LowFreeSpace_CreatesNothing_AndDeletesNothing()
    {
        Run(D(1));
        AddGame("追加");
        var before = Names();
        Assert.Equal(AutoBackupResult.NoFreeSpace, Run(D(2), free: _ => AutoBackup.MinFreeBytes - 1));
        Assert.Equal(before, Names());
    }

    [Fact]
    public void Run_CorruptDb_FailsAndKeepsOldGenerations()
    {
        var s = new AutoBackupSettings { Keep = 3 };
        for (var d = 1; d <= 3; d++) { AddGame("g" + d); Run(D(d), s); }
        var before = Names();

        File.WriteAllText(DbPath, "これはデータベースではありません");
        Assert.Equal(AutoBackupResult.Failed, Run(D(4), s));
        Assert.Equal(before, Names()); // 作りかけも残らず、古い世代も消えない
    }

    // ---- 検査・復元 ----

    [Fact]
    public void CheckIntegrity_OkForHealthyDb_NotOkForGarbageOrMissing()
    {
        Assert.True(AutoBackup.CheckIntegrity(DbPath).Ok);

        var bad = Path.Combine(_dir, "bad.db");
        File.WriteAllText(bad, "壊れたファイル");
        Assert.False(AutoBackup.CheckIntegrity(bad).Ok);
        Assert.False(AutoBackup.CheckIntegrity(Path.Combine(_dir, "none.db")).Ok);
    }

    [Fact]
    public void StageRestore_PutsDbAndOptionalConfigInPending_AndRejectsBrokenOnes()
    {
        Run(D(1));
        var g = AutoBackup.List(BackupDir).Single();
        var pending = Path.Combine(_dir, "pending");

        AutoBackup.StageRestore(g, withConfig: false, pending);
        Assert.True(File.Exists(Path.Combine(pending, "games.db")));
        Assert.False(File.Exists(Path.Combine(pending, "egs_config.json")));

        AutoBackup.StageRestore(g, withConfig: true, pending);
        Assert.True(File.Exists(Path.Combine(pending, "egs_config.json")));

        File.WriteAllText(g.DbPath, "壊れた");
        var ex = Assert.Throws<InvalidDataException>(() => AutoBackup.StageRestore(g, true, pending));
        Assert.Contains("壊れて", ex.Message);
        Assert.False(Directory.Exists(pending) && File.Exists(Path.Combine(pending, "games.db")) && new FileInfo(Path.Combine(pending, "games.db")).Length == 0);
    }

    [Fact]
    public void List_NewestFirst_WithConfigWhenPresent_AndIgnoresOtherNames()
    {
        Run(D(1));
        AddGame("x");
        Run(D(2));
        File.Delete(Path.Combine(BackupDir, "config_20261001.json"));
        File.WriteAllText(Path.Combine(BackupDir, "games_abc.db"), "x");

        var list = AutoBackup.List(BackupDir);
        Assert.Equal(new[] { 2, 1 }, list.Select(g => g.Date.Day));
        Assert.NotNull(list[0].ConfigPath);
        Assert.Null(list[1].ConfigPath);
    }

    [Fact]
    public void Settings_AreClampedToTheAllowedRange()
    {
        var s = new AutoBackupSettings { IntervalDays = 3, Keep = 99 }.Normalized();
        Assert.Equal(1, s.IntervalDays);
        Assert.Equal(AutoBackup.MaxKeep, s.Keep);
        Assert.Equal(AutoBackup.MinKeep, new AutoBackupSettings { Keep = 0 }.Normalized().Keep);
        Assert.Equal(7, new AutoBackupSettings { IntervalDays = 10 }.Normalized().IntervalDays);
    }

    [Fact]
    public void CountGames_ReadsTheNumberOfWorks_OrNullWhenBroken()
    {
        Assert.Equal(1, AutoBackup.CountGames(DbPath));
        var bad = Path.Combine(_dir, "bad.db");
        File.WriteAllText(bad, "x");
        Assert.Null(AutoBackup.CountGames(bad));
    }

    // ---- 保存先の指定 ----

    [Fact]
    public void UseFolder_SwitchesBackupDir_AndEmptyMeansDefault()
    {
        try
        {
            AutoBackup.UseFolder(@"D:\Somewhere");
            Assert.Equal(@"D:\Somewhere", AutoBackup.BackupDir);
            Assert.Equal(@"D:\Somewhere", AutoBackup.CustomDir);
            AutoBackup.UseFolder("  ");
            Assert.Null(AutoBackup.CustomDir);
            Assert.Equal(AutoBackup.DefaultDir, AutoBackup.BackupDir);
        }
        finally { AutoBackup.UseFolder(null); }
    }

    [Fact]
    public void CheckFolder_AcceptsWritableFolder_AndCreatesIt_AndLeavesNoProbeFile()
    {
        var dir = Path.Combine(_dir, "custom", "sub");
        Assert.Null(AutoBackup.CheckFolder(dir));
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("relative\folder")]
    public void CheckFolder_RejectsEmptyOrRelative(string path) => Assert.NotNull(AutoBackup.CheckFolder(path));

    [Fact]
    public void CheckFolder_RejectsAFileInPlaceOfAFolder()
    {
        var file = Path.Combine(_dir, "afile");
        File.WriteAllText(file, "x");
        Assert.NotNull(AutoBackup.CheckFolder(file));
    }

    [Fact]
    public void CustomFolder_BackupsGoThere_AndOtherFilesAreNeitherCountedNorDeleted()
    {
        var custom = Path.Combine(_dir, "mine");
        Directory.CreateDirectory(custom);
        var foreign = Path.Combine(custom, "my_photo.jpg");
        File.WriteAllBytes(foreign, new byte[5000]);

        Assert.Equal(AutoBackupResult.Created, AutoBackup.Run(DbPath, CfgPath, custom, new AutoBackupSettings(), false, D(1), PlentyOfSpace, 1000000));
        Assert.True(File.Exists(Path.Combine(custom, "games_20261001.db")));
        Assert.False(Directory.Exists(BackupDir)); // 既定の場所には作らない

        var own = new FileInfo(Path.Combine(custom, "games_20261001.db")).Length + new FileInfo(Path.Combine(custom, "config_20261001.json")).Length;
        Assert.Equal(own, AutoBackup.TotalSize(custom)); // 写真は数えない

        AutoBackup.Prune(custom, 3, 5, 1); // 上限を極端に小さくしても、他のファイルは消さない
        Assert.True(File.Exists(foreign));
        Assert.Single(AutoBackup.List(custom)); // 最新の1世代は残る
    }

    [Fact]
    public void ConfigRoundTrip_KeepsAutoBackupDir()
    {
        var cfg = new EgsConfig { AutoBackupDir = @"E:\NK" };
        Assert.Equal(@"E:\NK", System.Text.Json.JsonSerializer.Deserialize<EgsConfig>(System.Text.Json.JsonSerializer.Serialize(cfg))!.AutoBackupDir);
        Assert.Equal("", new EgsConfig().AutoBackupDir);
    }
}
