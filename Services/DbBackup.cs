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

    public static void Run(string dbPath, string backupDir)
    {
        try
        {
            if (!File.Exists(dbPath) || new FileInfo(dbPath).Length == 0) return;
            Directory.CreateDirectory(backupDir);

            var today = Path.Combine(backupDir, $"games_{DateTime.Now:yyyyMMdd}.db");
            if (!File.Exists(today))
            {
                var tmp = today + ".tmp"; // 途中で止まっても、不完全なファイルを今日の分として残さない
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
                using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp }.ToString()))
                {
                    source.Open();
                    dest.Open();
                    source.BackupDatabase(dest);
                }
                SqliteConnection.ClearAllPools(); // コピー先を閉じて、ファイルを移せるようにする
                File.Move(tmp, today);
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
