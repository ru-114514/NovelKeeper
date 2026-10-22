using System.Globalization;
using System.Text.Json;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace ErogeLauncher.Data;

/// <summary>ローカルDB（SQLite）。%AppData%\ErogeLauncher\games.db に保存される</summary>
public class Database : ISessionStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS games (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                egs_id          INTEGER UNIQUE,
                title           TEXT NOT NULL,
                brand           TEXT,
                sellday         TEXT,
                median          INTEGER,
                exe_path        TEXT,
                monitor_process TEXT,
                status          INTEGER NOT NULL DEFAULT 0,
                clear_date      TEXT,
                egs_played      INTEGER NOT NULL DEFAULT 0,
                egs_clear_date  TEXT,
                egs_synced_at   TEXT
            );
            CREATE TABLE IF NOT EXISTS sessions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id    INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                started_at TEXT NOT NULL,
                ended_at   TEXT NOT NULL,
                seconds    INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // 後から追加した列（古いDBにはないので、なければ追加する）
        AddColumnIfMissing(conn, "games", "egs_giveup", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "games", "my_score", "INTEGER");
        AddColumnIfMissing(conn, "games", "egs_score", "INTEGER");
        AddColumnIfMissing(conn, "games", "install_dir", "TEXT");
        AddColumnIfMissing(conn, "games", "egs_possession", "INTEGER");
        AddColumnIfMissing(conn, "games", "egs_average", "INTEGER");
        AddColumnIfMissing(conn, "games", "egs_count", "INTEGER");
        AddColumnIfMissing(conn, "games", "review_json", "TEXT");    // 得点登録フォームのその他の項目
        AddColumnIfMissing(conn, "games", "review_pending", "TEXT"); // まだ批評空間に反映していない項目（カンマ区切り）

        // 起動するもの（1作品に複数）
        using (var t = conn.CreateCommand())
        {
            t.CommandText = """
                CREATE TABLE IF NOT EXISTS launch_targets (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id         INTEGER NOT NULL,
                    label           TEXT NOT NULL DEFAULT '',
                    path            TEXT NOT NULL,
                    monitor_process TEXT,
                    sort_order      INTEGER NOT NULL DEFAULT 0
                );
                -- 以前の「実行ファイル1つ」の設定を、起動するものの一覧に移す
                INSERT INTO launch_targets (game_id, label, path, monitor_process, sort_order)
                SELECT id, '', exe_path, monitor_process, 0 FROM games
                WHERE exe_path IS NOT NULL AND exe_path <> ''
                  AND id NOT IN (SELECT game_id FROM launch_targets);
                """;
            t.ExecuteNonQuery();
        }
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    /// <param name="id">指定すると、その作品だけを読む</param>
    public List<Game> GetAll(long? id = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.*,
                   COALESCE((SELECT SUM(s.seconds) FROM sessions s WHERE s.game_id = g.id), 0) AS total_seconds,
                   (SELECT MIN(s.started_at) FROM sessions s WHERE s.game_id = g.id) AS first_played
            FROM games g
            WHERE $id IS NULL OR g.id = $id
            ORDER BY g.title
            """;
        cmd.Parameters.AddWithValue("$id", Db(id));
        var targets = GetAllTargets(conn);
        using var r = cmd.ExecuteReader();
        var list = new List<Game>();
        while (r.Read())
        {
            list.Add(new Game
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                EgsId = r["egs_id"] is DBNull ? null : Convert.ToInt32(r["egs_id"]),
                Title = Convert.ToString(r["title"]) ?? "",
                Brand = Str(r["brand"]),
                SellDay = Str(r["sellday"]),
                Median = r["median"] is DBNull ? null : Convert.ToInt32(r["median"]),
                EgsAverage = r["egs_average"] is DBNull ? null : Convert.ToInt32(r["egs_average"]),
                EgsCount = r["egs_count"] is DBNull ? null : Convert.ToInt32(r["egs_count"]),
                ExePath = Str(r["exe_path"]),
                MonitorProcess = Str(r["monitor_process"]),
                InstallDir = Str(r["install_dir"]),
                Status = (GameStatus)Convert.ToInt32(r["status"]),
                ClearDate = ParseDate(Str(r["clear_date"]), DateFormat),
                EgsPlayed = Convert.ToInt32(r["egs_played"]) != 0,
                EgsGiveUp = Convert.ToInt32(r["egs_giveup"]) != 0,
                EgsPossession = r["egs_possession"] is DBNull ? null : Convert.ToInt32(r["egs_possession"]) != 0,
                MyScore = r["my_score"] is DBNull ? null : Convert.ToInt32(r["my_score"]),
                EgsScore = r["egs_score"] is DBNull ? null : Convert.ToInt32(r["egs_score"]),
                EgsClearDate = ParseDate(Str(r["egs_clear_date"]), DateFormat),
                EgsSyncedAt = ParseDate(Str(r["egs_synced_at"]), DateTimeFormat),
                TotalSeconds = Convert.ToInt64(r["total_seconds"]),
                FirstPlayed = ParseDate(Str(r["first_played"]), DateTimeFormat),
                Review = ReadReview(Str(r["review_json"])),
                PendingReview = new HashSet<string>((Str(r["review_pending"]) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            });
        }
        foreach (var g in list)
        {
            g.Targets = targets.TryGetValue(g.Id, out var ts) ? ts : new List<LaunchTarget>();
            foreach (var t in g.Targets)
                t.IsCurrent = string.Equals(t.Path, g.ExePath, StringComparison.OrdinalIgnoreCase);
        }
        return list;
    }

    // ================================================================
    // 起動するもの（1作品に複数）
    // ================================================================

    private static Dictionary<long, List<LaunchTarget>> GetAllTargets(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM launch_targets ORDER BY game_id, sort_order, id";
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<long, List<LaunchTarget>>();
        while (r.Read())
        {
            var t = new LaunchTarget
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                GameId = Convert.ToInt64(r["game_id"]),
                Label = Convert.ToString(r["label"]) ?? "",
                Path = Convert.ToString(r["path"]) ?? "",
                MonitorProcess = Str(r["monitor_process"]),
                SortOrder = Convert.ToInt32(r["sort_order"]),
            };
            if (!dict.TryGetValue(t.GameId, out var list)) dict[t.GameId] = list = new List<LaunchTarget>();
            list.Add(t);
        }
        return dict;
    }

    public List<LaunchTarget> GetTargets(long gameId)
    {
        using var conn = Open();
        return GetAllTargets(conn).TryGetValue(gameId, out var list) ? list : new List<LaunchTarget>();
    }

    /// <summary>起動するものを追加する（同じパスがすでにあれば、それを返す）</summary>
    public LaunchTarget AddTarget(long gameId, string label, string path, string? monitorProcess)
    {
        using var conn = Open();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id, label, monitor_process, sort_order FROM launch_targets WHERE game_id = $g AND path = $p COLLATE NOCASE";
            find.Parameters.AddWithValue("$g", gameId);
            find.Parameters.AddWithValue("$p", path);
            using var r = find.ExecuteReader();
            if (r.Read())
                return new LaunchTarget
                {
                    Id = r.GetInt64(0), GameId = gameId, Label = Convert.ToString(r[1]) ?? "",
                    Path = path, MonitorProcess = Str(r[2]), SortOrder = Convert.ToInt32(r[3]),
                };
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO launch_targets (game_id, label, path, monitor_process, sort_order)
            VALUES ($g, $label, $p, $proc, COALESCE((SELECT MAX(sort_order) + 1 FROM launch_targets WHERE game_id = $g), 0));
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$g", gameId);
        cmd.Parameters.AddWithValue("$label", label);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$proc", Db(monitorProcess));
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return new LaunchTarget { Id = id, GameId = gameId, Label = label, Path = path, MonitorProcess = monitorProcess };
    }

    public void UpdateTarget(LaunchTarget t)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE launch_targets SET label = $label, path = $p, monitor_process = $proc, sort_order = $sort
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$label", t.Label);
        cmd.Parameters.AddWithValue("$p", t.Path);
        cmd.Parameters.AddWithValue("$proc", Db(string.IsNullOrWhiteSpace(t.MonitorProcess) ? null : t.MonitorProcess));
        cmd.Parameters.AddWithValue("$sort", t.SortOrder);
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteTarget(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM launch_targets WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>作品を1件だけ読み直す（通信中に画面で変更されていても、最新の内容を使うため）</summary>
    public Game? Get(long id) => GetAll(id).FirstOrDefault();

    /// <summary>
    /// 批評空間との同期状態（egs_* と未反映の項目）だけを書き込む。
    /// 通信中に画面で保存された内容（得点・感想など）を、古い内容で上書きしないため。
    /// </summary>
    public void UpdateSyncState(Game g)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE games SET
                egs_played = $egsPlayed, egs_giveup = $egsGiveUp, egs_possession = $egsPossession, egs_score = $egsScore,
                egs_clear_date = $egsClear, egs_synced_at = $egsSynced, review_pending = $reviewPending
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$egsPlayed", g.EgsPlayed ? 1 : 0);
        cmd.Parameters.AddWithValue("$egsGiveUp", g.EgsGiveUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$egsPossession", g.EgsPossession is bool pos ? (pos ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$egsScore", Db(g.EgsScore));
        cmd.Parameters.AddWithValue("$egsClear", Db(g.EgsClearDate?.ToString(DateFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$egsSynced", Db(g.EgsSyncedAt?.ToString(DateTimeFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$reviewPending", string.Join(",", g.PendingReview));
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 同期・取り込みで変わる項目だけを書き込む（起動設定など、同期中に画面で変えられる項目は上書きしない）
    /// </summary>
    public void UpdateFromSync(Game g)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE games SET
                sellday = $sellday, median = $median, egs_average = $egsAverage, egs_count = $egsCount,
                status = $status, clear_date = $clear, my_score = $myScore, review_json = $review
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$sellday", Db(g.SellDay));
        cmd.Parameters.AddWithValue("$median", Db(g.Median));
        cmd.Parameters.AddWithValue("$egsAverage", Db(g.EgsAverage));
        cmd.Parameters.AddWithValue("$egsCount", Db(g.EgsCount));
        cmd.Parameters.AddWithValue("$status", (int)g.Status);
        cmd.Parameters.AddWithValue("$clear", Db(g.ClearDate?.ToString(DateFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$myScore", Db(g.MyScore));
        cmd.Parameters.AddWithValue("$review", JsonSerializer.Serialize(g.Review));
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.ExecuteNonQuery();
        UpdateSyncState(g);
    }

    public bool ExistsEgsId(int egsId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM games WHERE egs_id = $egs";
        cmd.Parameters.AddWithValue("$egs", egsId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public void Insert(Game g)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games (egs_id, title, brand, sellday, median, egs_average, egs_count, exe_path, monitor_process, status, clear_date, my_score, install_dir, review_json, review_pending)
            VALUES ($egs, $title, $brand, $sellday, $median, $egsAverage, $egsCount, $exe, $proc, $status, $clear, $myScore, $installDir, $review, $reviewPending);
            SELECT last_insert_rowid();
            """;
        AddCommonParams(cmd, g);
        g.Id = Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Update(Game g)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE games SET
                egs_id = $egs, title = $title, brand = $brand, sellday = $sellday, median = $median,
                egs_average = $egsAverage, egs_count = $egsCount,
                exe_path = $exe, monitor_process = $proc, install_dir = $installDir, status = $status, clear_date = $clear, my_score = $myScore,
                review_json = $review, review_pending = $reviewPending,
                egs_played = $egsPlayed, egs_giveup = $egsGiveUp, egs_possession = $egsPossession, egs_score = $egsScore, egs_clear_date = $egsClear, egs_synced_at = $egsSynced
            WHERE id = $id
            """;
        AddCommonParams(cmd, g);
        cmd.Parameters.AddWithValue("$egsPlayed", g.EgsPlayed ? 1 : 0);
        cmd.Parameters.AddWithValue("$egsGiveUp", g.EgsGiveUp ? 1 : 0);
        cmd.Parameters.AddWithValue("$egsPossession", g.EgsPossession is bool pos ? (pos ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$egsScore", Db(g.EgsScore));
        cmd.Parameters.AddWithValue("$egsClear", Db(g.EgsClearDate?.ToString(DateFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$egsSynced", Db(g.EgsSyncedAt?.ToString(DateTimeFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.ExecuteNonQuery();

        // 「今使うもの」が起動するものの一覧になければ追加する（自動検出・ドロップなど、どこで設定しても一覧にそろう）
        if (!string.IsNullOrWhiteSpace(g.ExePath))
        {
            var t = AddTarget(g.Id, "", g.ExePath, g.MonitorProcess);
            if (!string.IsNullOrWhiteSpace(g.MonitorProcess) && t.MonitorProcess != g.MonitorProcess)
            {
                t.MonitorProcess = g.MonitorProcess;
                UpdateTarget(t);
            }
        }
    }

    public void Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE game_id = $id; DELETE FROM launch_targets WHERE game_id = $id; DELETE FROM games WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>セッションを追加して、その ID を返す（計測中は UpdateSession で途中経過を書き込む）</summary>
    public long AddSession(long gameId, DateTime start, DateTime end, long seconds)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (game_id, started_at, ended_at, seconds)
            VALUES ($id, $start, $end, $sec);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", gameId);
        cmd.Parameters.AddWithValue("$start", start.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$end", end.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$sec", seconds);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void UpdateSession(long sessionId, DateTime end, long seconds)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET ended_at = $end, seconds = $sec WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$end", end.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$sec", seconds);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSession(long sessionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>期間にかかるセッションを読む（レポート用）。from 以上 to 未満</summary>
    public List<SessionRow> GetSessions(DateTime from, DateTime to)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.game_id, g.title, s.started_at, s.ended_at, s.seconds
            FROM sessions s JOIN games g ON g.id = s.game_id
            WHERE s.started_at < $to AND s.ended_at >= $from
            ORDER BY s.started_at
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$to", to.ToString(DateTimeFormat, CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRow>();
        while (r.Read())
        {
            var start = ParseDate(Str(r[2]), DateTimeFormat);
            var end = ParseDate(Str(r[3]), DateTimeFormat);
            if (start == null) continue;
            list.Add(new SessionRow(Convert.ToInt64(r[0]), Convert.ToString(r[1]) ?? "", start.Value, end ?? start.Value, Convert.ToInt64(r[4])));
        }
        return list;
    }

    /// <summary>未プレイの作品だけ「プレイ中」にする（ほかの項目には触れない）</summary>
    public void SetPlayingIfUnplayed(long gameId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET status = 1 WHERE id = $id AND status = 0";
        cmd.Parameters.AddWithValue("$id", gameId);
        cmd.ExecuteNonQuery();
    }

    // ---- 補助 ----
    private static void AddCommonParams(SqliteCommand cmd, Game g)
    {
        cmd.Parameters.AddWithValue("$egs", Db(g.EgsId));
        cmd.Parameters.AddWithValue("$title", g.Title);
        cmd.Parameters.AddWithValue("$brand", Db(g.Brand));
        cmd.Parameters.AddWithValue("$sellday", Db(g.SellDay));
        cmd.Parameters.AddWithValue("$median", Db(g.Median));
        cmd.Parameters.AddWithValue("$egsAverage", Db(g.EgsAverage));
        cmd.Parameters.AddWithValue("$egsCount", Db(g.EgsCount));
        cmd.Parameters.AddWithValue("$exe", Db(g.ExePath));
        cmd.Parameters.AddWithValue("$proc", Db(g.MonitorProcess));
        cmd.Parameters.AddWithValue("$installDir", Db(g.InstallDir));
        cmd.Parameters.AddWithValue("$status", (int)g.Status);
        cmd.Parameters.AddWithValue("$clear", Db(g.ClearDate?.ToString(DateFormat, CultureInfo.InvariantCulture)));
        cmd.Parameters.AddWithValue("$myScore", Db(g.MyScore));
        cmd.Parameters.AddWithValue("$review", JsonSerializer.Serialize(g.Review));
        cmd.Parameters.AddWithValue("$reviewPending", string.Join(",", g.PendingReview));
    }

    private static Review ReadReview(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Review();
        try { return JsonSerializer.Deserialize<Review>(json) ?? new Review(); }
        catch (Exception ex) { Log.Warn("感想などの保存内容を読めませんでした", ex); return new Review(); }
    }

    private static object Db(object? value) => value ?? DBNull.Value;

    private static string? Str(object value) => value is DBNull ? null : Convert.ToString(value);

    private static DateTime? ParseDate(string? s, string format) =>
        DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
