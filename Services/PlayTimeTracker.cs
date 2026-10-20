using System.Diagnostics;
using System.IO;
using ErogeLauncher.Data;
using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>計測中の作品1つ分の状態（画面表示用）</summary>
public record ActiveSessionInfo(long GameId, string Title, double Seconds, bool Counting);

/// <summary>
/// プレイ時間の計測。
///  - 10秒ごとに起動中のプロセスを調べ、「ゲームのフォルダの中にある exe」が動いていればプレイ中とみなす
///    （ランチャーから本体が起動するゲームでも、同じフォルダなので自動で計測できる）
///  - ツールから起動したかどうかは問わない（デスクトップや Steam から起動しても計測する）
///  - 1分ごとに途中経過を保存する（ツールが落ちても、失うのは最大1分）
///  - スリープ中・放置中・（設定により）裏に回っている間は数えない
/// </summary>
public sealed class PlayTimeTracker : IDisposable
{
    private const int IntervalSeconds = 10;     // 確認の間隔
    private const int SaveIntervalSeconds = 60; // 途中経過を保存する間隔
    private const int MinSessionSeconds = 10;   // これより短いセッションは記録しない

    private readonly Database _db;
    private readonly EgsConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<long, Session> _active = new();
    private readonly object _lock = new();

    private Dictionary<int, (string Name, string? Path)> _processes = new(); // 前回の確認で調べたプロセス（ID → 名前と exe の場所）
    private List<Target> _targets = new();
    private DateTime _targetsLoadedAt = DateTime.MinValue;
    private volatile bool _targetsDirty = true;
    private DateTime _lastTick = DateTime.Now;
    private bool _disposed; // 終了処理の後は、新しい計測を始めない

    private class Session
    {
        public long SessionId;
        public long GameId;
        public string Title = "";
        public double Seconds;
        public int Misses;
        public bool Counting;
        public DateTime LastSaved;
    }

    /// <summary>判定に使う作品の情報</summary>
    private record Target(Game Game, string? Folder, string? ProcessName);

    public event Action<long, string>? SessionStarted;          // 作品ID, タイトル
    public event Action<long, string, double>? SessionEnded;    // 作品ID, タイトル, 秒数
    public event Action<IReadOnlyList<ActiveSessionInfo>>? Ticked;

    public PlayTimeTracker(Database db, EgsConfig cfg)
    {
        _db = db;
        _cfg = cfg;
    }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>作品の情報が変わったら呼ぶ（次の確認で読み直す）</summary>
    public void InvalidateGames() => _targetsDirty = true;

    public bool IsRunning(long gameId)
    {
        lock (_lock) return _active.ContainsKey(gameId);
    }

    // ================================================================
    // 起動
    // ================================================================

    /// <summary>ゲームを起動する（計測は常駐の監視が自動で行う）</summary>
    public void Launch(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.ExePath))
            throw new InvalidOperationException("実行ファイルが設定されていません。");
        if (IsRunning(game.Id))
            throw new InvalidOperationException("この作品はすでに起動中です。");

        var isUrl = ShortcutResolver.IsUrl(game.ExePath);
        if (!isUrl && !File.Exists(game.ExePath))
            throw new InvalidOperationException("実行ファイルが見つかりません。\n" + game.ExePath);

        var psi = new ProcessStartInfo(game.ExePath) { UseShellExecute = true };
        if (!isUrl) psi.WorkingDirectory = Path.GetDirectoryName(game.ExePath)!; // セーブ等の相対パス対策
        Process.Start(psi)?.Dispose();
    }

    // ================================================================
    // 監視
    // ================================================================

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));
        _lastTick = DateTime.Now;
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                try { Tick(); }
                catch (Exception ex) { Log.Error("プレイ時間の確認に失敗", ex); } // 1回の失敗で監視を止めない
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        // 間隔が大きく空いた＝スリープしていた。その間は数えない
        var add = elapsed > IntervalSeconds * 3 ? 0 : Math.Min(elapsed, IntervalSeconds * 2);

        if (_targetsDirty || (now - _targetsLoadedAt).TotalSeconds > 60)
        {
            _targets = BuildTargets(_db.GetAll());
            _targetsLoadedAt = now;
            _targetsDirty = false;
        }

        // ---- 起動中のプロセスと作品を突き合わせる ----
        var running = new Dictionary<long, (Game Game, HashSet<int> Pids)>();
        var self = Environment.ProcessId;
        var seen = new Dictionary<int, (string Name, string? Path)>();
        if (_targets.Count > 0)
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string name;
                    try { name = p.ProcessName; } catch (Exception) { continue; } // 調べている間に終了した
                    // 動いているプロセスの exe の場所は変わらないので、一度調べたら使い回す（名前も同じときだけ。ID の使い回し対策）
                    if (!_processes.TryGetValue(p.Id, out var known) || known.Name != name)
                        known = (name, NativeMethods.GetProcessPath(p.Id));
                    seen[p.Id] = known;

                    var target = Match(known.Name, known.Path);
                    if (target == null) continue;
                    if (!running.TryGetValue(target.Game.Id, out var entry))
                        running[target.Game.Id] = entry = (target.Game, new HashSet<int>());
                    entry.Pids.Add(p.Id);
                }
            }
        }
        _processes = seen; // 終了したプロセスの分は、ここで捨てる

        // ---- 「数える」条件（利用者の設定による） ----
        var idle = _cfg.ExcludeIdle && NativeMethods.IdleSeconds() >= Math.Max(1, _cfg.IdleMinutes) * 60;
        var foregroundPid = _cfg.ForegroundOnly ? NativeMethods.ForegroundProcessId() : 0;

        var started = new List<(long, string)>();
        var ended = new List<(long, string, double)>();
        List<ActiveSessionInfo> snapshot;

        lock (_lock)
        {
            if (_disposed) return;
            foreach (var (gameId, (game, pids)) in running)
            {
                if (!_active.TryGetValue(gameId, out var s))
                {
                    // 新しく起動した
                    s = new Session
                    {
                        GameId = gameId,
                        Title = game.Title,
                        LastSaved = now,
                        Counting = true,
                        SessionId = _db.AddSession(gameId, now, now, 0),
                    };
                    _active[gameId] = s;
                    _db.SetPlayingIfUnplayed(gameId);
                    started.Add((gameId, game.Title));
                    continue;
                }

                s.Misses = 0;
                s.Counting = !idle && (!_cfg.ForegroundOnly || pids.Contains(foregroundPid));
                if (s.Counting) s.Seconds += add;

                if ((now - s.LastSaved).TotalSeconds >= SaveIntervalSeconds)
                {
                    _db.UpdateSession(s.SessionId, now, (long)s.Seconds);
                    s.LastSaved = now;
                }
            }

            // 見つからなくなった作品：2回続けて見つからなければ終了とみなす
            foreach (var s in _active.Values.Where(x => !running.ContainsKey(x.GameId)).ToList())
            {
                if (++s.Misses < 2) continue;
                Finish(s, now);
                ended.Add((s.GameId, s.Title, s.Seconds));
            }

            snapshot = _active.Values
                .Select(x => new ActiveSessionInfo(x.GameId, x.Title, x.Seconds, x.Counting))
                .ToList();
        }

        foreach (var (id, title) in started) SessionStarted?.Invoke(id, title);
        foreach (var (id, title, sec) in ended) SessionEnded?.Invoke(id, title, sec);
        Ticked?.Invoke(snapshot);
    }

    private void Finish(Session s, DateTime now)
    {
        if (s.Seconds < MinSessionSeconds) _db.DeleteSession(s.SessionId);
        else _db.UpdateSession(s.SessionId, now, (long)s.Seconds);
        _active.Remove(s.GameId);
    }

    // ================================================================
    // 作品とプロセスの判定
    // ================================================================

    /// <summary>
    /// 作品ごとに「どのフォルダ・どのプロセス名なら、その作品のプレイ中とみなすか」を作る。
    /// 起動するものが複数ある作品（コンプリートボックスなど）は、そのすべてを判定に使う。
    /// </summary>
    private List<Target> BuildTargets(List<Game> games)
    {
        var list = new List<Target>();
        foreach (var g in games)
        {
            // 判定に使う元：今使うもの（＋Steam のインストール先）と、登録してあるすべての起動するもの
            var sources = new List<(string? Path, string? Monitor)> { (g.ExePath, g.MonitorProcess) };
            sources.AddRange(g.Targets.Select(t => ((string?)t.Path, t.MonitorProcess)));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? folder, string? proc)
            {
                if (folder != null && IsTooBroad(folder)) folder = null; // ドライブ直下や D:\Games 自体などは使わない
                if (folder == null && proc == null) return;
                var normalized = folder == null ? null : Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                if (!seen.Add((normalized ?? "") + "|" + (proc ?? ""))) return;
                list.Add(new Target(g, normalized, proc));
            }

            if (!string.IsNullOrWhiteSpace(g.InstallDir)) Add(g.InstallDir, null);

            foreach (var (path, monitor) in sources)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var isUrl = ShortcutResolver.IsUrl(path);
                var folder = isUrl ? null : Path.GetDirectoryName(path);
                var proc = string.IsNullOrWhiteSpace(monitor) ? null : Path.GetFileNameWithoutExtension(monitor.Trim());
                if (folder != null && IsTooBroad(folder) && proc == null && !isUrl)
                    proc = Path.GetFileNameWithoutExtension(path); // フォルダで判定できないときは exe 名で
                Add(folder, proc);
            }
        }
        return list;
    }

    private Target? Match(string name, string? path)
    {
        // ① フォルダで判定（いちばん深く一致したフォルダの作品を選ぶ）
        if (path != null)
        {
            Target? best = null;
            foreach (var t in _targets)
            {
                if (t.Folder == null || !path.StartsWith(t.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || t.Folder.Length > best.Folder!.Length) best = t;
            }
            if (best != null) return best;
        }

        // ② プロセス名で判定（「監視するプロセス名」を設定した作品など）
        return _targets.FirstOrDefault(t => t.ProcessName != null &&
                                            string.Equals(t.ProcessName, name, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsTooBroad(string folder)
    {
        try
        {
            var f = Path.GetFullPath(folder).TrimEnd('\\');
            if (string.Equals(f, Path.GetPathRoot(f)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            if (f.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase)) return true;

            var broad = new List<string>(_cfg.GameFolders)
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            };
            return broad.Where(b => !string.IsNullOrEmpty(b))
                        .Any(b => string.Equals(Path.GetFullPath(b).TrimEnd('\\'), f, StringComparison.OrdinalIgnoreCase));
        }
        catch { return true; }
    }

    // ================================================================

    /// <summary>終了時：計測中のセッションを保存して止める</summary>
    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _disposed = true;
            var now = DateTime.Now;
            foreach (var s in _active.Values.ToList()) Finish(s, now);
        }
    }
}
