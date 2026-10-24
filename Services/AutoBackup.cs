using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ErogeLauncher.Services;

/// <summary>自動バックアップの設定（設定ファイルの値を、使える範囲に整えたもの）</summary>
public class AutoBackupSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>何日ごとに作るか（1 または 7）</summary>
    public int IntervalDays { get; set; } = 1;
    /// <summary>残す世代数（3〜14）</summary>
    public int Keep { get; set; } = AutoBackup.DefaultKeep;

    public AutoBackupSettings Normalized() => new()
    {
        Enabled = Enabled,
        IntervalDays = IntervalDays >= 7 ? 7 : 1,
        Keep = Math.Clamp(Keep, AutoBackup.MinKeep, AutoBackup.MaxKeep),
    };
}

/// <summary>自動バックアップ1世代分</summary>
public record BackupGeneration(DateTime Date, string DbPath, string? ConfigPath, long Size);

public enum AutoBackupResult
{
    Created,         // 作った
    Disabled,        // 自動バックアップがオフ
    NoDatabase,      // 元の DB がない・空
    NotDue,          // まだ間隔が来ていない
    Unchanged,       // 前回と内容が同じなので作らない
    NoFreeSpace,     // ドライブの空き容量が少ない
    Failed,          // 作れなかった・作ったものが正しくなかった（古いものは消さない）
}

/// <summary>
/// 作品の一覧・プレイ時間（games.db）と設定（egs_config.json）の自動バックアップ。
/// 利用者の環境に負荷をかけないよう、すべてに上限を付ける：
///  ・世代数（初期は7、3〜14）／日付つきの控え（before_…）は最大5件／backups フォルダ全体で最大50MB
///  ・前回と内容が同じなら作らない／ドライブの空き容量が500MB未満なら作らない
///  ・新しいバックアップを作って、正しく読めることを確かめてから、古いものを消す
/// 消すのは、このアプリが作った名前（games_日付.db・config_日付.json・before_…_日時.db）のものだけ。完全削除（ごみ箱には入れない）。
/// ログイン情報（credential.bin）は含めない。
/// </summary>
public static class AutoBackup
{
    public const int DefaultKeep = 7;
    public const int MinKeep = 3;
    public const int MaxKeep = 14;
    public const int MaxBefore = 5;
    public const long MaxTotalBytes = 50L * 1024 * 1024;
    public const long MinFreeBytes = 500L * 1024 * 1024;

    private static readonly Regex GamesName = new(@"^games_(\d{8})\.db$", RegexOptions.Compiled);
    private static readonly Regex ConfigName = new(@"^config_(\d{8})\.json$", RegexOptions.Compiled);
    private static readonly Regex BeforeName = new(@"^before_[a-z]+_(\d{8}_\d{6})\.db$", RegexOptions.Compiled);
    private static readonly Regex StageName = new(@"^games_\d{8}\.new(\.tmp)?$", RegexOptions.Compiled);

    private static readonly object Gate = new();

    /// <summary>既定の保存先（アプリのフォルダの backups）</summary>
    public static string DefaultDir => Path.Combine(AppPaths.Dir, "backups");

    private static string? _customDir;

    /// <summary>利用者が指定した保存先（なければ null = 既定）</summary>
    public static string? CustomDir => _customDir;

    /// <summary>いまの保存先。指定があればそこ、なければ既定</summary>
    public static string BackupDir => _customDir ?? DefaultDir;

    /// <summary>設定ファイルの保存先を使い始める（空なら既定）。起動時と、設定を変えたときに呼ぶ</summary>
    public static void UseFolder(string? path) => _customDir = string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    /// <summary>保存先として使えるかを調べる。使えなければ理由、使えれば null（フォルダがなければ作り、書き込めるかを試す）</summary>
    public static string? CheckFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "フォルダが指定されていません";
        try
        {
            path = path.Trim();
            if (!Path.IsPathFullyQualified(path)) return "ドライブ名から始まるフォルダ（例：D:/Backup）か、ネットワークの共有フォルダを指定してください";
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".nk_write_test_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            return "このフォルダには書き込めません：" + ex.Message;
        }
    }

    /// <summary>このアプリが作った名前のファイルか（指定したフォルダに他のファイルがあっても、数えたり消したりしないため）</summary>
    public static bool IsOwnFile(string path)
    {
        var n = Path.GetFileName(path);
        return GamesName.IsMatch(n) || ConfigName.IsMatch(n) || BeforeName.IsMatch(n) || StageName.IsMatch(n);
    }

    public static AutoBackupSettings SettingsOf(EgsConfig cfg) => new AutoBackupSettings
    {
        Enabled = cfg.AutoBackupEnabled,
        IntervalDays = cfg.AutoBackupIntervalDays,
        Keep = cfg.AutoBackupKeep,
    }.Normalized();

    /// <summary>起動時と、常駐中の定期チェックで呼ぶ（間隔が来ていて、内容が変わっていれば作る）</summary>
    public static AutoBackupResult RunScheduled(EgsConfig cfg) =>
        Run(AppPaths.Database, AppPaths.Config, BackupDir, SettingsOf(cfg));

    public static AutoBackupResult Run(string dbPath, string configPath, string backupDir, AutoBackupSettings settings,
        bool force = false, DateTime? now = null, Func<string, long>? freeBytes = null, long maxTotalBytes = MaxTotalBytes)
    {
        lock (Gate)
        {
            string? stage = null;
            try
            {
                var s = settings.Normalized();
                if (!s.Enabled && !force) return AutoBackupResult.Disabled;
                if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return AutoBackupResult.NoDatabase;

                var t = now ?? DateTime.Now;
                Directory.CreateDirectory(backupDir);
                var gens = List(backupDir);
                if (!force && gens.Count > 0 && (t.Date - gens[0].Date.Date).Days < s.IntervalDays)
                    return AutoBackupResult.NotDue;

                if ((freeBytes ?? FreeBytes)(backupDir) < MinFreeBytes)
                {
                    Log.Warn("ドライブの空き容量が少ないため、自動バックアップを作りませんでした");
                    return AutoBackupResult.NoFreeSpace;
                }

                // いったん別の名前に作って、正しいことを確かめてから、本来の名前にする
                stage = Path.Combine(backupDir, $"games_{t:yyyyMMdd}.new");
                if (File.Exists(stage)) File.Delete(stage);
                DbBackup.CopyDatabase(dbPath, stage);

                var check = CheckIntegrity(stage);
                if (!check.Ok)
                {
                    Log.Warn("作ったバックアップが正しくなかったため、破棄しました：" + check.Message);
                    File.Delete(stage);
                    return AutoBackupResult.Failed;
                }
                if (!force && gens.Count > 0 && SameContent(stage, gens[0].DbPath))
                {
                    File.Delete(stage);
                    return AutoBackupResult.Unchanged;
                }

                var dest = Path.Combine(backupDir, $"games_{t:yyyyMMdd}.db");
                File.Move(stage, dest, overwrite: true);
                stage = null;

                if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
                {
                    var cfgDest = Path.Combine(backupDir, $"config_{t:yyyyMMdd}.json");
                    var tmp = cfgDest + ".tmp";
                    File.Copy(configPath, tmp, overwrite: true);
                    File.Move(tmp, cfgDest, overwrite: true);
                }
                Log.Info("データベースをバックアップしました: " + dest);

                Prune(backupDir, s.Keep, MaxBefore, maxTotalBytes);
                return AutoBackupResult.Created;
            }
            catch (Exception ex)
            {
                Log.Warn("データベースのバックアップに失敗", ex);
                try { if (stage != null && File.Exists(stage)) File.Delete(stage); } catch { }
                return AutoBackupResult.Failed;
            }
        }
    }

    /// <summary>自動バックアップの世代（新しい順）</summary>
    public static IReadOnlyList<BackupGeneration> List(string backupDir)
    {
        if (!Directory.Exists(backupDir)) return Array.Empty<BackupGeneration>();
        var list = new List<BackupGeneration>();
        foreach (var file in Directory.GetFiles(backupDir))
        {
            var m = GamesName.Match(Path.GetFileName(file));
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date)) continue;
            var cfg = Path.Combine(backupDir, $"config_{m.Groups[1].Value}.json");
            list.Add(new BackupGeneration(date, file, File.Exists(cfg) ? cfg : null, new FileInfo(file).Length));
        }
        return list.OrderByDescending(g => g.Date).ToList();
    }

    /// <summary>バックアップの保存先にある、このアプリのファイルの合計の大きさ（バイト）。ほかのファイルは数えない</summary>
    public static long TotalSize(string backupDir) =>
        Directory.Exists(backupDir) ? Directory.GetFiles(backupDir).Where(IsOwnFile).Sum(f => new FileInfo(f).Length) : 0;

    /// <summary>
    /// 古いものを消す（完全削除）。このアプリが作った名前のものだけが対象。最新の1世代は、どの上限でも消さない。
    /// 順序：世代数の上限 → 控え（before_…）の件数の上限 → フォルダ全体の大きさの上限（控えの古い順、次に世代の古い順）
    /// </summary>
    public static void Prune(string backupDir, int keep, int maxBefore, long maxTotalBytes)
    {
        if (!Directory.Exists(backupDir)) return;
        keep = Math.Max(1, keep);

        void Delete(string path)
        {
            try { File.Delete(path); Log.Info("古いバックアップを削除しました: " + Path.GetFileName(path)); }
            catch (Exception ex) { Log.Warn("古いバックアップを削除できませんでした: " + path, ex); }
        }

        // 取りこぼした作りかけのファイル
        foreach (var f in Directory.GetFiles(backupDir))
            if (StageName.IsMatch(Path.GetFileName(f)) && File.GetLastWriteTime(f) < DateTime.Now.AddHours(-1)) Delete(f);

        var gens = List(backupDir).ToList();
        foreach (var g in gens.Skip(keep)) { Delete(g.DbPath); if (g.ConfigPath != null) Delete(g.ConfigPath); }
        gens = gens.Take(keep).ToList();

        // 対応する世代のない設定の控え
        var dates = gens.Select(g => g.Date.ToString("yyyyMMdd")).ToHashSet();
        foreach (var f in Directory.GetFiles(backupDir))
        {
            var m = ConfigName.Match(Path.GetFileName(f));
            if (m.Success && !dates.Contains(m.Groups[1].Value)) Delete(f);
        }

        var befores = Befores(backupDir);
        foreach (var f in befores.Skip(Math.Max(0, maxBefore))) Delete(f);
        befores = befores.Take(Math.Max(0, maxBefore)).ToList();

        // フォルダ全体の大きさの上限：控えの古い順 → 世代の古い順（最新の1世代は残す）
        long Total() => TotalSize(backupDir);
        for (var i = befores.Count - 1; i >= 0 && Total() > maxTotalBytes; i--) Delete(befores[i]);
        for (var i = gens.Count - 1; i >= 1 && Total() > maxTotalBytes; i--)
        {
            Delete(gens[i].DbPath);
            if (gens[i].ConfigPath != null) Delete(gens[i].ConfigPath!);
        }
    }

    /// <summary>before_…（取り込み・整理・復元の直前の控え）を、新しい順に</summary>
    private static List<string> Befores(string backupDir) =>
        Directory.GetFiles(backupDir)
            .Select(f => (File: f, M: BeforeName.Match(Path.GetFileName(f))))
            .Where(x => x.M.Success)
            .OrderByDescending(x => x.M.Groups[1].Value)
            .Select(x => x.File)
            .ToList();

    /// <summary>DB が壊れていないかの簡易検査（SQLite の quick_check）。開けない・壊れていれば、理由つきで false</summary>
    public static (bool Ok, string Message) CheckIntegrity(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            var result = cmd.ExecuteScalar()?.ToString() ?? "";
            if (result != "ok") return (false, result == "" ? "検査の結果を読めませんでした" : result);
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            cmd.ExecuteScalar(); // 作品の表があること
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>復元の準備：選んだ世代を保留フォルダに置く（反映は、DataBackup.ApplyPending）。壊れた世代は断る</summary>
    public static void StageRestore(BackupGeneration g, bool withConfig, string pendingDir)
    {
        var check = CheckIntegrity(g.DbPath);
        if (!check.Ok) throw new InvalidDataException("このバックアップは壊れているため、戻せません：" + check.Message);
        if (Directory.Exists(pendingDir)) Directory.Delete(pendingDir, true);
        Directory.CreateDirectory(pendingDir);
        try
        {
            File.Copy(g.DbPath, Path.Combine(pendingDir, DataBackup.DbEntry));
            if (withConfig && g.ConfigPath != null) File.Copy(g.ConfigPath, Path.Combine(pendingDir, DataBackup.ConfigEntry));
        }
        catch
        {
            try { Directory.Delete(pendingDir, true); } catch { }
            throw;
        }
    }

    /// <summary>作品数（バックアップの一覧に表示する）。読めなければ null</summary>
    public static int? CountGames(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch { return null; }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
        }
        catch { return false; }
    }

    private static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return long.MaxValue; } // 調べられないときは、止めない
    }
}
