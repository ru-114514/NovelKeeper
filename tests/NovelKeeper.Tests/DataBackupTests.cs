using NovelKeeper.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

public class DataBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    public DataBackupTests()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string MakeDb(string name, int games)
    {
        var path = Path.Combine(_dir, name);
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE games (id INTEGER PRIMARY KEY, title TEXT);" +
                              string.Concat(Enumerable.Range(1, games).Select(i => $"INSERT INTO games (title) VALUES ('g{i}');"));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static int Count(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM games";
        var n = Convert.ToInt32(cmd.ExecuteScalar());
        SqliteConnection.ClearAllPools();
        return n;
    }

    [Fact]
    public void Export_Inspect_Stage_Apply_RoundTrip_KeepsABackupOfTheOldData()
    {
        var db = MakeDb("games.db", 3);
        var cfg = Path.Combine(_dir, "egs_config.json");
        File.WriteAllText(cfg, "{\"Theme\":\"Light\"}");
        var zip = Path.Combine(_dir, "b.zip");
        DataBackup.Export(zip, db, cfg);

        var info = DataBackup.Inspect(zip);
        Assert.True(info.Ok);
        Assert.Equal(3, info.Games);
        Assert.True(info.HasConfig);

        // 取り込み先は、別のデータ（作品1件・別の設定）
        var target = MakeDb("target.db", 1);
        var targetCfg = Path.Combine(_dir, "target_cfg.json");
        File.WriteAllText(targetCfg, "{\"Theme\":\"Dark\"}");
        var pending = Path.Combine(_dir, "pending");
        var backups = Path.Combine(_dir, "backups");

        DataBackup.Stage(zip, pending, withConfig: false);
        Assert.NotNull(DataBackup.ApplyPending(pending, target, targetCfg, backups));

        Assert.Equal(3, Count(target));
        Assert.Equal("{\"Theme\":\"Dark\"}", File.ReadAllText(targetCfg)); // 設定は戻していない
        Assert.False(Directory.Exists(pending));
        var old = Assert.Single(Directory.GetFiles(backups, "before_import_*.db"));
        Assert.Equal(1, Count(old)); // 置き換え前の内容が控えに残る
    }

    [Fact]
    public void Stage_WithConfig_RestoresSettingsToo()
    {
        var db = MakeDb("games.db", 1);
        var cfg = Path.Combine(_dir, "egs_config.json");
        File.WriteAllText(cfg, "{\"Theme\":\"Light\"}");
        var zip = Path.Combine(_dir, "b.zip");
        DataBackup.Export(zip, db, cfg);

        var target = MakeDb("target.db", 2);
        var targetCfg = Path.Combine(_dir, "target_cfg.json");
        File.WriteAllText(targetCfg, "{\"Theme\":\"Dark\"}");
        var pending = Path.Combine(_dir, "pending");
        DataBackup.Stage(zip, pending, withConfig: true);
        DataBackup.ApplyPending(pending, target, targetCfg, Path.Combine(_dir, "backups"));
        Assert.Equal("Light", EgsConfig.Load(targetCfg).Theme);
    }

    [Fact]
    public void Inspect_RejectsFilesThatAreNotBackups()
    {
        var notZip = Path.Combine(_dir, "x.zip");
        File.WriteAllText(notZip, "not a zip");
        Assert.False(DataBackup.Inspect(notZip).Ok);

        var other = Path.Combine(_dir, "other.zip");
        using (var z = System.IO.Compression.ZipFile.Open(other, System.IO.Compression.ZipArchiveMode.Create))
            z.CreateEntry("readme.txt");
        var r = DataBackup.Inspect(other);
        Assert.False(r.Ok);
        Assert.Contains("games.db", r.Error);
    }

    [Fact]
    public void ApplyPending_NothingPending_DoesNothing()
        => Assert.Null(DataBackup.ApplyPending(Path.Combine(_dir, "none"), Path.Combine(_dir, "g.db"), Path.Combine(_dir, "c.json"), _dir));

    /// <summary>控えの保存先に書き込めない（同じ名前のファイルがあって、フォルダを作れない）状態で、取り込みを保留にする</summary>
    private (string Target, string Pending, string Backups) StageWithUnwritableBackupDir()
    {
        var zip = Path.Combine(_dir, "b.zip");
        DataBackup.Export(zip, MakeDb("games.db", 3), Path.Combine(_dir, "none.json"));
        var target = MakeDb("target.db", 1);
        var pending = Path.Combine(_dir, "pending");
        DataBackup.Stage(zip, pending, withConfig: false);
        var backups = Path.Combine(_dir, "backups");
        File.WriteAllText(backups, "");
        return (target, pending, backups);
    }

    [Fact]
    public void ApplyPending_BackupFails_KeepsCurrentDataAndLeavesTheImportPending()
    {
        var (target, pending, backups) = StageWithUnwritableBackupDir();

        var message = DataBackup.ApplyPending(pending, target, Path.Combine(_dir, "target_cfg.json"), backups);

        Assert.Contains("中止", message);
        Assert.Equal(1, Count(target)); // 控えなしでは置き換えない
        Assert.True(File.Exists(Path.Combine(pending, DataBackup.DbEntry))); // 次の起動でもう一度反映できる
    }

    [Fact]
    public void ApplyPending_BackupNotRequired_ReplacesEvenIfTheBackupFails()
    {
        // 壊れた DB を自動バックアップから戻すとき（呼ぶ側で、壊れたファイルを控えてある）
        var (target, pending, backups) = StageWithUnwritableBackupDir();

        DataBackup.ApplyPending(pending, target, Path.Combine(_dir, "target_cfg.json"), backups, requireBackup: false);

        Assert.Equal(3, Count(target));
        Assert.False(Directory.Exists(pending));
    }

    [Fact]
    public void ApplyPending_SettingsMoveFails_KeepsCurrentDataAndRetriesNextTime()
    {
        var cfg = Path.Combine(_dir, "egs_config.json");
        File.WriteAllText(cfg, "{\"Theme\":\"Light\"}");
        var zip = Path.Combine(_dir, "b.zip");
        DataBackup.Export(zip, MakeDb("games.db", 3), cfg);
        var target = MakeDb("target.db", 1);
        var pending = Path.Combine(_dir, "pending");
        var backups = Path.Combine(_dir, "backups");
        DataBackup.Stage(zip, pending, withConfig: true);
        var targetCfg = Path.Combine(_dir, "target_cfg.json");
        Directory.CreateDirectory(targetCfg); // 設定の置き場所に同じ名前のフォルダがあって、移せない

        var message = DataBackup.ApplyPending(pending, target, targetCfg, backups);

        Assert.Contains("次の起動", message);
        Assert.Equal(1, Count(target)); // DB だけが新しくなることはない
        Assert.True(File.Exists(Path.Combine(pending, DataBackup.DbEntry)));

        // 原因がなくなれば、次の起動で設定も DB もそろって反映される
        Directory.Delete(targetCfg);
        DataBackup.ApplyPending(pending, target, targetCfg, backups);
        Assert.Equal(3, Count(target));
        Assert.Equal("Light", EgsConfig.Load(targetCfg).Theme);
        Assert.False(Directory.Exists(pending));
    }

    [Fact]
    public void ApplyPending_RemovesDangerousLaunchPaths_BeforeReplacing_AndKeepsConnectionSettings()
    {
        // 他人から受け取ったバックアップ：ネットワーク上の exe・http の URL、送り先とバックアップ先を書き換えた設定
        var srcPath = Path.Combine(_dir, "src.db");
        var src = new NovelKeeper.Data.Database(srcPath);
        var a = new NovelKeeper.Models.Game { Title = "A", ExePath = @"\\host\share\a.exe" };
        src.Insert(a); src.Update(a);
        src.AddTarget(a.Id, "", "https://example.com/a.exe", null);
        var srcCfg = Path.Combine(_dir, "src_cfg.json");
        File.WriteAllText(srcCfg, "{\"Theme\":\"Light\",\"BaseUrl\":\"https://evil.example/\",\"AutoBackupDir\":\"\\\\\\\\host\\\\share\"," +
                                  "\"AlwaysRunAsAdmin\":true,\"PostFontFamily\":\"file:///host/share/#Font\"," +
                                  "\"PostBackColor\":\"ContextColor file://host/share/p.icc 1,0,0,0\",\"PostCardColor\":\"#112233\"}");
        SqliteConnection.ClearAllPools();
        var zip = Path.Combine(_dir, "b.zip");
        DataBackup.Export(zip, srcPath, srcCfg);

        var target = MakeDb("target.db", 1);
        var targetCfg = Path.Combine(_dir, "target_cfg.json");
        File.WriteAllText(targetCfg, "{\"AutoBackupDir\":\"D:\\\\mybackup\"}");
        var pending = Path.Combine(_dir, "pending");
        DataBackup.Stage(zip, pending, withConfig: true);

        var message = DataBackup.ApplyPending(pending, target, targetCfg, Path.Combine(_dir, "backups"));

        Assert.Contains("起動設定を外した作品が 1 件", message);
        var game = new NovelKeeper.Data.Database(target).GetAll().Single();
        Assert.Null(game.ExePath);
        Assert.Empty(game.Targets);

        var cfg = EgsConfig.Load(targetCfg);
        Assert.Equal("Light", cfg.Theme);                          // ふつうの設定は取り込む
        Assert.Equal(new EgsConfig().BaseUrl, cfg.BaseUrl);        // 送り先は変えさせない
        Assert.Equal(@"D:\mybackup", cfg.AutoBackupDir);           // いまの値のまま
        Assert.False(cfg.AlwaysRunAsAdmin);
        Assert.Null(cfg.PostFontFamily);                           // 場所の書かれたフォントは外す
        Assert.Null(cfg.PostBackColor);                            // #RRGGBB でない色も外す
        Assert.Equal("#112233", cfg.PostCardColor);
    }

    [Fact]
    public void ImportConfig_Broken_KeepsCurrentSettings()
    {
        var imported = Path.Combine(_dir, "imported.json");
        File.WriteAllText(imported, "{ broken");
        var current = Path.Combine(_dir, "current.json");
        File.WriteAllText(current, "{\"Theme\":\"Light\"}");

        EgsConfig.Import(imported, current);

        Assert.Equal("{\"Theme\":\"Light\"}", File.ReadAllText(current));
    }

    /// <summary>
    /// 設定に文字や一覧の項目を増やしたら、取り込んでよいか（KeptOnImport に入れるか）を決めて、ここか KeptOnImport に書く。
    /// URL・パス・欄の名前が、知らないうちにバックアップから取り込めるようにならないように
    /// </summary>
    [Fact]
    public void EveryTextOrListSetting_IsClassifiedForImport()
    {
        var safe = new[]
        {
            "SortKeys", "SortPresets", "BacklogExcludedIds", "BacklogSnapshots", "NextPickFilter", "NextPickPresets", "DetailPosition",
            "SummaryPanels", "ColumnWidths", "Columns", "PostLayout", "PostBackColor", "PostCardColor", "PostFontFamily", "PostTextColor",
            "ScoreItems", "ScoreItems2", "ScoreItems3", "ScoreTemplateNames", "PostTitle", "PostReplayGameIds", "PostHiddenGameIds",
            "PostBrandName", "ExpandedSections", "Theme",
        };
        var unclassified = typeof(EgsConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod!.IsPublic && (p.PropertyType == typeof(string) || !p.PropertyType.IsValueType))
            .Select(p => p.Name)
            .Except(EgsConfig.KeptOnImport).Except(safe)
            .ToList();
        Assert.Empty(unclassified);
    }

    [Fact]
    public void CopyDirectory_CopiesSubfolders_AndKeepsFilesAlreadyThere()
    {
        var from = Path.Combine(_dir, "old");
        Directory.CreateDirectory(Path.Combine(from, "backups"));
        File.WriteAllText(Path.Combine(from, "games.db"), "old");
        File.WriteAllText(Path.Combine(from, "backups", "auto_1.db"), "b");
        var to = Path.Combine(_dir, "new");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "games.db"), "new");

        AppPaths.CopyDirectory(from, to);

        Assert.Equal("b", File.ReadAllText(Path.Combine(to, "backups", "auto_1.db")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(to, "games.db")));
    }
}

public class ClearMissingLaunchPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));

    public ClearMissingLaunchPathsTests()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void MissingFiles_AreCleared_ExistingAndUrlsAreKept_AndCurrentSwitchesToWhatRemains()
    {
        var dbPath = Path.Combine(_dir, "games.db");
        var db = new NovelKeeper.Data.Database(dbPath);
        const string here = @"D:\games\here.exe";
        const string gone = @"Z:\old-pc\gone.exe";

        var a = new NovelKeeper.Models.Game { Title = "A", ExePath = gone, InstallDir = @"Z:\old-pc" };
        db.Insert(a); db.Update(a);                                   // 見つからない1つだけ → 未設定になる
        var b = new NovelKeeper.Models.Game { Title = "B", ExePath = "steam://rungameid/1" };
        db.Insert(b); db.Update(b);                                   // URL → そのまま
        var c = new NovelKeeper.Models.Game { Title = "C", ExePath = here };
        db.Insert(c); db.Update(c);                                   // ある → そのまま
        var d = new NovelKeeper.Models.Game { Title = "D", ExePath = gone };
        db.Insert(d); db.Update(d);
        db.AddTarget(d.Id, "", here, null);                           // 今使うものはないが、別の1つはある

        var n = DataBackup.ClearMissingLaunchPaths(dbPath, p => p == here || p.StartsWith("steam://"));
        var all = db.GetAll().ToDictionary(g => g.Title);

        Assert.Equal(2, n); // A と D
        Assert.Null(all["A"].ExePath);
        Assert.Null(all["A"].InstallDir);
        Assert.Empty(all["A"].Targets);
        Assert.Equal("steam://rungameid/1", all["B"].ExePath);
        Assert.Equal(here, all["C"].ExePath);
        Assert.Equal(here, all["D"].ExePath);                          // 残った1つに切り替わる
        Assert.Single(all["D"].Targets);
    }

    [Theory]
    [InlineData(@"D:\a.exe", true, false, false, true)]                         // ファイルがある
    [InlineData(@"D:\a.exe", false, true, true, false)]                         // ファイルがない（URL の判断は関係しない）
    [InlineData("steam://rungameid/123", false, true, false, true)]             // Steam にそのゲームがある
    [InlineData("steam://rungameid/123", true, false, true, false)]             // Steam にそのゲームがない
    [InlineData("dmmgameplayer://play/GMAIN/x/main/win", false, false, true, true)]   // DMM Game Player がある
    [InlineData("dmmgameplayer://play/GMAIN/x/main/win", true, true, false, false)]   // DMM Game Player がない
    [InlineData("steam://rungameid/123/", false, true, false, true)]
    [InlineData("steam://run/123", true, true, true, false)]                    // rungameid 以外の Steam の URL は使わない
    [InlineData("unknown-app://x", true, true, true, false)]                    // 知らない URL は使わない
    [InlineData("https://example.com/a.exe", true, true, true, false)]
    [InlineData("file://host/share/a.exe", true, true, true, false)]
    public void IsLaunchAvailable_JudgesFilesSteamAndDmm(string path, bool fileExists, bool steam, bool dmm, bool expected) =>
        Assert.Equal(expected, DataBackup.IsLaunchAvailable(path, _ => fileExists, _ => steam, _ => dmm));

    [Theory]
    [InlineData(@"\\host\share\a.exe")]
    [InlineData("//host/share/a.exe")]
    [InlineData(@"\\?\UNC\host\share\a.exe")]
    [InlineData(@" \/host\share\a.exe")]
    [InlineData(@"\??\UNC\host\share\a.exe")]
    public void IsLaunchAvailable_NetworkPath_IsRefusedWithoutTouchingIt(string path) =>
        // 存在を確かめるだけで認証情報が送られるので、ファイルには触らない
        Assert.False(DataBackup.IsLaunchAvailable(path, _ => throw new InvalidOperationException("触った"), _ => true, _ => true));

    [Fact]
    public void Triggers_AreDropped_SoTheyCannotPutRemovedPathsBack()
    {
        var dbPath = Path.Combine(_dir, "games.db");
        var db = new NovelKeeper.Data.Database(dbPath);
        var g = new NovelKeeper.Models.Game { Title = "A" };
        db.Insert(g); db.Update(g);
        db.AddTarget(g.Id, "", @"\\host\share\a.exe", null);
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER put_back AFTER DELETE ON launch_targets BEGIN " +
                              "INSERT INTO launch_targets (game_id, label, path, sort_order) VALUES (OLD.game_id, '', OLD.path, 0); END;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        DataBackup.ClearMissingLaunchPaths(dbPath);

        Assert.Empty(db.GetAll().Single().Targets);
        using var check = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger'";
        Assert.Equal(0L, (long)q.ExecuteScalar()!);
    }

    [Fact]
    public void NetworkInstallDir_IsCleared()
    {
        var dbPath = Path.Combine(_dir, "games.db");
        var db = new NovelKeeper.Data.Database(dbPath);
        var g = new NovelKeeper.Models.Game { Title = "A", ExePath = "steam://rungameid/1", InstallDir = @"\\host\share\game" };
        db.Insert(g); db.Update(g);

        DataBackup.ClearMissingLaunchPaths(dbPath, _ => true);

        Assert.Null(db.GetAll().Single().InstallDir);
    }

    [Fact]
    public void OldFormatDatabase_WithoutLaunchColumns_IsLeftAlone()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE games (id INTEGER PRIMARY KEY, title TEXT);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, DataBackup.ClearMissingLaunchPaths(path, _ => false));
    }
}
