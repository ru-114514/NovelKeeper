using System.IO;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Services;

/// <summary>
/// 作品の一覧・プレイ時間（games.db）のコピーに使う部品。
/// 自動バックアップ本体は AutoBackup（世代・件数・大きさ・空き容量の上限つき）。
/// SQLite のバックアップ機能を使うので、プレイ時間を計測中でも壊れたコピーにならない。
/// </summary>
public static class DbBackup
{
    /// <summary>
    /// 大きな変更（まとめての削除など）の直前に、日次とは別に控えを残す。
    /// 日次の世代には数えない。新しい方から5件だけ残し、古いものは自動バックアップのときに消える。
    /// 失敗したら null（呼び出し側は、続けるか判断する）
    /// </summary>
    public static string? Snapshot(string dbPath, string backupDir, string tag)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;
            Directory.CreateDirectory(backupDir);
            var stamp = $"{tag}_{DateTime.Now:yyyyMMdd_HHmmss}";
            var path = Path.Combine(backupDir, stamp + ".db");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(backupDir, $"{stamp}_{n}.db"); // 同じ秒に2回作っても失敗しない
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
        try
        {
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
        catch
        {
            SqliteConnection.ClearAllPools();
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>設定の既定値（毎日・7世代、設定ファイルは含めない）で自動バックアップを行う</summary>
    public static void Run(string dbPath, string backupDir) =>
        AutoBackup.Run(dbPath, "", backupDir, new AutoBackupSettings());
}
