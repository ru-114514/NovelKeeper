using System.IO;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Services;

/// <summary>
/// 手動のバックアップ（書き出し・取り込み）。作品の一覧とプレイ時間（games.db）と、設定（egs_config.json）を1つの zip にまとめる。
/// ログイン情報（credential.bin）は、他へ持ち出さないよう含めない。
/// 取り込みは、動作中の DB を差し替えないよう、いったん保留フォルダに置き、次の起動の最初に反映する。
/// </summary>
public static class DataBackup
{
    public const string DbEntry = "games.db";
    public const string ConfigEntry = "egs_config.json";
    private const string InfoEntry = "backup_info.txt";

    public static string PendingDir => Path.Combine(AppPaths.Dir, "import_pending");

    /// <summary>zip に書き出す。DB は SQLite のバックアップ機能でコピーするので、計測中でも壊れない。途中で止まっても書きかけを残さない</summary>
    public static void Export(string zipPath, string dbPath, string configPath)
    {
        var work = Path.Combine(Path.GetTempPath(), "NovelKeeper_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var tmpZip = zipPath + ".tmp";
        try
        {
            var dbCopy = Path.Combine(work, DbEntry);
            DbBackup.CopyDatabase(dbPath, dbCopy);

            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            using (var zip = ZipFile.Open(tmpZip, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(dbCopy, DbEntry);
                if (File.Exists(configPath)) zip.CreateEntryFromFile(configPath, ConfigEntry);
                var info = zip.CreateEntry(InfoEntry);
                using var w = new StreamWriter(info.Open());
                w.Write($"NovelKeeper backup\r\n{DateTime.Now:yyyy/MM/dd HH:mm:ss}\r\n");
            }
            File.Move(tmpZip, zipPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch { }
        }
    }

    /// <summary>取り込む zip を調べる。使える（games.db に作品の表がある）なら作品数を、だめなら理由を返す</summary>
    public static (bool Ok, int Games, bool HasConfig, string? Error) Inspect(string zipPath)
    {
        var work = Path.Combine(Path.GetTempPath(), "NovelKeeper_inspect_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var db = zip.GetEntry(DbEntry);
            if (db == null) return (false, 0, false, "NovelKeeper のバックアップではありません（games.db が入っていません）。");
            Directory.CreateDirectory(work);
            var dbFile = Path.Combine(work, DbEntry);
            db.ExtractToFile(dbFile);
            var count = CountGames(dbFile);
            return (true, count, zip.GetEntry(ConfigEntry) != null, null);
        }
        catch (Exception ex)
        {
            return (false, 0, false, "バックアップを読めませんでした：" + ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static int CountGames(string dbFile)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM games";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>取り込む内容を保留フォルダに置く（反映は次の起動の最初）。設定は、withConfig のときだけ</summary>
    public static void Stage(string zipPath, string pendingDir, bool withConfig)
    {
        if (Directory.Exists(pendingDir)) Directory.Delete(pendingDir, true);
        Directory.CreateDirectory(pendingDir);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            zip.GetEntry(DbEntry)!.ExtractToFile(Path.Combine(pendingDir, DbEntry));
            if (withConfig && zip.GetEntry(ConfigEntry) is { } cfg)
                cfg.ExtractToFile(Path.Combine(pendingDir, ConfigEntry));
        }
        catch
        {
            try { Directory.Delete(pendingDir, true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 取り込んだデータの起動先として、このパソコンで使えそうか。ファイルは存在で、steam://rungameid/数字 は Steam にそのゲームが入っているか、
    /// dmmgameplayer:// は DMM Game Player が入っているか（URL の種類が登録されているか）で判断する。
    /// ネットワーク上のパス（\\相手のPC\共有）と、それ以外の URL（http・file など）は使わない（他人のバックアップで、相手の PC の exe などを起動させられないように）。
    /// ネットワーク上のパスは、存在を確かめるだけで Windows の認証情報（NTLM）が相手に送られるので、ファイルに触る前に文字だけで断る
    /// </summary>
    internal static bool IsLaunchAvailable(string path, Func<string, bool>? fileExists = null,
        Func<string, bool>? steamAppInstalled = null, Func<string, bool>? schemeRegistered = null)
    {
        if (IsNetworkPath(path)) return false;
        if (!path.Contains("://")) return (fileExists ?? File.Exists)(path);

        var m = SteamRunGame.Match(path.Trim());
        if (m.Success) return (steamAppInstalled ?? (id => GameScanner.ScanSteam(id).Count > 0))(m.Groups[1].Value);
        if (path.StartsWith("dmmgameplayer://", StringComparison.OrdinalIgnoreCase))
            return (schemeRegistered ?? SchemeRegistered)("dmmgameplayer");
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex SteamRunGame =
        new(@"^steam://rungameid/(\d+)/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>ネットワーク上の場所か（\\ や // で始まる。\\?\UNC\ も含む。\??\ で始まるものも、UNC になりうるので含める）</summary>
    internal static bool IsNetworkPath(string? path)
    {
        var p = path?.Trim() ?? "";
        return (p.Length >= 2 && p[0] is '\\' or '/' && p[1] is '\\' or '/') || p.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private static bool SchemeRegistered(string scheme)
    {
        try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme); return k != null; }
        catch { return false; }
    }

    /// <summary>
    /// 作品の起動設定のうち、このパソコンで使えないものを外す（ファイルがない、Steam にそのゲームがない、DMM Game Player がない、
    /// ネットワーク上のパス、Steam・DMM Game Player 以外の URL）。
    /// 起動する一覧から外した結果、「今使うもの」が無効になった作品は、残った先頭のものに切り替え、なければ未設定
    /// （監視するプロセス名・ゲームのフォルダも外す）にする。ネットワーク上のゲームのフォルダも外す。外れたものがあった作品の数を返す
    /// </summary>
    public static int ClearMissingLaunchPaths(string dbPath, Func<string, bool>? exists = null)
    {
        exists ??= p => IsLaunchAvailable(p);
        bool Ok(string? p) => string.IsNullOrWhiteSpace(p) || exists(p);

        // 取り込む前の保留の DB に使い、このあとファイルを移すので、接続を使い回さない（閉じたらファイルも閉じる）
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();
        using var tx = conn.BeginTransaction();

        SqliteCommand Cmd(string sql)
        {
            var c = conn.CreateCommand();
            c.Transaction = tx;
            c.CommandText = sql;
            return c;
        }
        bool HasColumn(string table, string column)
        {
            using var c = Cmd($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'");
            return Convert.ToInt32(c.ExecuteScalar()) > 0;
        }

        // このアプリはトリガーを作らない。取り込んだ DB にあれば、外した起動設定を書き戻したりできるので、すべて消す
        var triggers = new List<string>();
        using (var c = Cmd("SELECT name FROM sqlite_master WHERE type = 'trigger'"))
        using (var r = c.ExecuteReader())
            while (r.Read()) triggers.Add(r.GetString(0));
        foreach (var name in triggers)
        {
            using var drop = Cmd($"DROP TRIGGER \"{name.Replace("\"", "\"\"")}\"");
            drop.ExecuteNonQuery();
        }

        if (!HasColumn("games", "exe_path")) { tx.Commit(); return 0; } // 起動設定のない古い形式
        var affected = new HashSet<long>();
        var hasTargets = HasColumn("launch_targets", "path");

        if (hasTargets)
        {
            var missing = new List<(long Id, long Game)>();
            using (var c = Cmd("SELECT id, game_id, path FROM launch_targets"))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    if (!Ok(r.IsDBNull(2) ? null : r.GetString(2))) missing.Add((r.GetInt64(0), r.GetInt64(1)));
            foreach (var (id, game) in missing)
            {
                using var del = Cmd("DELETE FROM launch_targets WHERE id = $id");
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
                affected.Add(game);
            }
        }

        var hasMonitor = HasColumn("games", "monitor_process");
        var hasInstall = HasColumn("games", "install_dir");
        var broken = new List<long>();
        using (var c = Cmd("SELECT id, exe_path FROM games WHERE exe_path IS NOT NULL AND exe_path <> ''"))
        using (var r = c.ExecuteReader())
            while (r.Read())
                if (!Ok(r.GetString(1))) broken.Add(r.GetInt64(0));

        foreach (var id in broken)
        {
            affected.Add(id);
            string? nextPath = null, nextMonitor = null;
            if (hasTargets)
            {
                using var next = Cmd("SELECT path, monitor_process FROM launch_targets WHERE game_id = $g ORDER BY sort_order, id LIMIT 1");
                next.Parameters.AddWithValue("$g", id);
                using var nr = next.ExecuteReader();
                if (nr.Read())
                {
                    nextPath = nr.GetString(0);
                    nextMonitor = nr.IsDBNull(1) ? null : nr.GetString(1);
                }
            }

            var sets = new List<string> { "exe_path = $p" };
            if (hasMonitor) sets.Add("monitor_process = $m");
            if (nextPath == null && hasInstall) sets.Add("install_dir = NULL");
            using var up = Cmd($"UPDATE games SET {string.Join(", ", sets)} WHERE id = $id");
            up.Parameters.AddWithValue("$p", (object?)nextPath ?? DBNull.Value);
            up.Parameters.AddWithValue("$m", (object?)nextMonitor ?? DBNull.Value);
            up.Parameters.AddWithValue("$id", id);
            up.ExecuteNonQuery();
        }

        if (hasInstall)
        {
            // ゲームのフォルダは、プレイ中かどうかの判定で存在を確かめるので、ネットワーク上の場所は使わない
            var remote = new List<long>();
            using (var c = Cmd("SELECT id, install_dir FROM games WHERE install_dir IS NOT NULL"))
            using (var r = c.ExecuteReader())
                while (r.Read())
                    if (IsNetworkPath(r.GetString(1))) remote.Add(r.GetInt64(0));
            foreach (var id in remote)
            {
                using var up = Cmd("UPDATE games SET install_dir = NULL WHERE id = $id");
                up.Parameters.AddWithValue("$id", id);
                up.ExecuteNonQuery();
            }
        }

        tx.Commit();
        SqliteConnection.ClearAllPools();
        return affected.Count;
    }

    /// <summary>
    /// 保留中の取り込みがあれば反映する（起動の最初、DB も設定も開く前に呼ぶ）。
    /// 反映の前に、いまの DB を backups に控える。反映したか、失敗したら null 以外のメッセージを返す
    /// </summary>
    /// <param name="requireBackup">
    /// いまの DB の控えを作れなければ、置き換えずに保留のまま残す（次の起動でもう一度反映する）。
    /// 壊れた DB を戻すときは、呼ぶ側でファイルのまま控えてあり、SQLite の控えは作れないことがあるので false にする
    /// </param>
    public static string? ApplyPending(string pendingDir, string dbPath, string configPath, string backupDir, bool requireBackup = true)
    {
        try
        {
            var newDb = Path.Combine(pendingDir, DbEntry);
            if (!File.Exists(newDb)) return null;

            if (DbBackup.Snapshot(dbPath, backupDir, "before_import") == null && requireBackup && File.Exists(dbPath))
            {
                Log.Warn("取り込み前の控えを作れなかったため、取り込みを中止しました（保留のまま残します）");
                return "バックアップを取り込めませんでした。\n\n" +
                       "取り込む前に、いまの作品データの控えを作れなかったため、置き換えを中止しました（いまのデータはそのままです）。\n" +
                       $"バックアップの保存先（{backupDir}）に書き込めるか、空き容量があるかを確認してください。\n\n" +
                       "取り込む内容は残してあるので、次の起動でもう一度反映します。\n" +
                       $"取り込みをやめるときは、次のフォルダを削除してください：\n{pendingDir}";
            }
            // 置き換える前に、取り込む DB から、このパソコンで使えない起動設定を外す（別のパソコンのパスが残って起動に失敗しないように。
            // ネットワーク上のパスや知らない URL で、相手の PC の exe を起動させられたり、認証情報を送らされたりしないように）。
            // 外せなければ反映しない（危ない起動設定が残るかもしれないので。保留のまま残し、次の起動でもう一度試す）
            var cleared = ClearMissingLaunchPaths(newDb);

            // 設定を先に、DB を最後に移す。保留の DB が残っていることが「まだ反映していない」の目印なので、
            // 途中で失敗しても、次の起動で残りを反映し直せる（DB だけが新しくなって、設定が取り残されることがない）。
            // 設定は、通信先・フォルダなどの項目を取り込まず、いまの値を残す（EgsConfig.KeptOnImport）
            var newCfg = Path.Combine(pendingDir, ConfigEntry);
            var hasConfig = File.Exists(newCfg);
            if (hasConfig)
            {
                EgsConfig.Import(newCfg, configPath);
                File.Delete(newCfg);
            }

            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
            File.Move(newDb, dbPath, overwrite: true);

            // 反映はすんでいる（保留の DB はもうないので、消せなくても次の起動では何もしない）
            try { Directory.Delete(pendingDir, true); }
            catch (Exception ex) { Log.Warn("取り込みに使ったフォルダを削除できませんでした", ex); }

            Log.Info($"バックアップを取り込みました（起動設定を外した作品: {cleared} 件）");
            return "バックアップを取り込みました。\n\nログイン情報（ID・パスワード）はバックアップに含まれません。\n別のパソコンに戻した場合は、「ログイン設定」タブでログインし直してください。" +
                   (hasConfig ? "\n\n設定のうち、批評空間との連携の設定、フォルダの指定（ゲームのフォルダ・自動バックアップの保存先・背景の画像）、" +
                                "管理者権限・自動起動の設定は、取り込まずにいまのままにしています。" : "") +
                   (cleared == 0 ? "" : $"\n\nこのパソコンで使えず（ファイルがない、Steam や DMM Game Player にゲームがない、ネットワーク上の場所、対応していない URL）、" +
                                        $"起動設定を外した作品が {cleared} 件あります。\n「ゲーム起動設定」タブで、「実行ファイルを自動検出」か、手動の設定をしてください。");
        }
        catch (Exception ex)
        {
            Log.Warn("バックアップの取り込みに失敗", ex);
            return "バックアップを取り込めませんでした：" + ex.Message +
                   (File.Exists(Path.Combine(pendingDir, DbEntry))
                       ? "\n\n取り込む内容は残してあるので、次の起動でもう一度反映します。\n" +
                         $"取り込みをやめるときは、次のフォルダを削除してください：\n{pendingDir}"
                       : "");
        }
    }
}
