using System.IO;
using Microsoft.Data.Sqlite;

namespace ErogeLauncher.Services;

/// <summary>
/// 作品の一覧・プレイ時間（games.db）の自動バックアップ。
/// 1日に1回、%AppData%\NovelKeeper\backups\games_日付.db に残し、新しい方から7日分だけ持つ。
/// SQLite のバックアップ機能を使うので、プレイ時間を計測中でも壊れたコピーにならない。
/// </summary>
public static class DbBackup
{
    private const int Keep = 7;

    public static void RunDaily() => Run(AppPaths.Database, Path.Combine(AppPaths.Dir, "backups"));

    /// <summary>
    /// 大きな変更（まとめての削除など）の直前に、日次とは別に控えを残す。
    /// 日次の「新しい方から7日分」には数えず、自動では消さない。失敗したら null（呼び出し側は、続けるか判断する）
    /// </summary>
    public static string? Snapshot(string dbPath, string backupDir, string tag)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;
            Directory.CreateDirectory(backupDir);
            var path = Path.Combine(backupDir, $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
            CopyDatabase(dbPath, path);
            Log.Info("データベースの控えを残しました: " + path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("データベースの控えに失敗", ex);
            return null;
        }
    }

    /// <summary>SQLite のバックアップ機能でコピーする。途中で止まっても、不完全なファイルを残さない</summary>
    internal static void CopyDatabase(string dbPath, string destPath)
    {
        var tmp = destPath + ".tmp";
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp }.ToString()))
        {
            source.Open();
            dest.Open();
            source.BackupDatabase(dest);
        }
        SqliteConnection.ClearAllPools(); // コピー先を閉じて、ファイルを移せるようにする
        File.Move(tmp, destPath);
    }

    public static void Run(string dbPath, string backupDir)
    {
        try
        {
            if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return;
            Directory.CreateDirectory(backupDir);

            var today = Path.Combine(backupDir, $"games_{DateTime.Now:yyyyMMdd}.db");
            if (!File.Exists(today))
            {
                CopyDatabase(dbPath, today);
                Log.Info("データベースをバックアップしました: " + today);
            }

            foreach (var old in Directory.GetFiles(backupDir, "games_*.db").OrderByDescending(f => f).Skip(Keep))
                File.Delete(old);
        }
        catch (Exception ex)
        {
            Log.Warn("データベースのバックアップに失敗", ex);
        }
    }
}
