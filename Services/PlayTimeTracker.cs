using System.Diagnostics;
using System.IO;
using ErogeLauncher.Data;
using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>
/// プレイ時間の計測（Windows のプロセスを見張る係）。
///  - 10秒ごとに起動中のプロセスを調べ、「ゲームのフォルダの中にある exe」が動いていればプレイ中とみなす
///    （ランチャーから本体が起動するゲームでも、同じフォルダなので自動で計測できる）
///  - ツールから起動したかどうかは問わない（デスクトップや Steam から起動しても計測する）
/// どの作品かの判定は <see cref="ProcessMatcher"/>、開始・加算・保存・終了の判断は <see cref="SessionBook"/> が行う
/// （どちらも単体テストがある）。ここでは、プロセスの一覧と放置・前面の状態を集めて、それらに渡す。
/// </summary>
public sealed class PlayTimeTracker : IDisposable
{
    private const int IntervalSeconds = 10;     // 確認の間隔

    private readonly Database _db;
    private readonly EgsConfig _cfg;
    private readonly CancellationTokenSource _cts = new();
    private readonly SessionBook _book;
    private readonly object _lock = new();

    private Dictionary<int, (string Name, string? Path)> _processes = new(); // 前回の確認で調べたプロセス（ID → 名前と exe の場所）
    private ProcessMatcher _matcher = new(Array.Empty<Game>(), Array.Empty<string>());
    private DateTime _matcherLoadedAt = DateTime.MinValue;
    private volatile bool _gamesDirty = true;
    private DateTime _lastTick = DateTime.Now;
    private bool _disposed; // 終了処理の後は、新しい計測を始めない

    public event Action<long, string>? SessionStarted;          // 作品ID, タイトル
    public event Action<long, string, double>? SessionEnded;    // 作品ID, タイトル, 秒数
    public event Action<IReadOnlyList<ActiveSessionInfo>>? Ticked;

    public PlayTimeTracker(Database db, EgsConfig cfg)
    {
        _db = db;
        _cfg = cfg;
        _book = new SessionBook(db);
    }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>作品の情報が変わったら呼ぶ（次の確認で読み直す）</summary>
    public void InvalidateGames() => _gamesDirty = true;

    public bool IsRunning(long gameId)
    {
        lock (_lock) return _book.IsRunning(gameId);
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
        var add = SessionBook.SecondsToAdd((now - _lastTick).TotalSeconds, IntervalSeconds);
        _lastTick = now;

        if (_gamesDirty || (now - _matcherLoadedAt).TotalSeconds > 60)
        {
            _matcher = new ProcessMatcher(_db.GetAll(), ProcessMatcher.BroadFolders(_cfg.GameFolders));
            _matcherLoadedAt = now;
            _gamesDirty = false;
        }

        // ---- 起動中のプロセスと作品を突き合わせる ----
        var running = new Dictionary<long, (Game Game, HashSet<int> Pids)>();
        var self = Environment.ProcessId;
        var seen = new Dictionary<int, (string Name, string? Path)>();
        if (_matcher.Count > 0)
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

                    var game = _matcher.Match(known.Name, known.Path);
                    if (game == null) continue;
                    if (!running.TryGetValue(game.Id, out var entry))
                        running[game.Id] = entry = (game, new HashSet<int>());
                    entry.Pids.Add(p.Id);
                }
            }
        }
        _processes = seen; // 終了したプロセスの分は、ここで捨てる

        // ---- 「数える」条件（利用者の設定による） ----
        var idleSeconds = _cfg.ExcludeIdle ? NativeMethods.IdleSeconds() : 0;
        var idle = _cfg.ExcludeIdle && idleSeconds >= Math.Max(1, _cfg.IdleMinutes) * 60;
        var foregroundPid = _cfg.ForegroundOnly ? NativeMethods.ForegroundProcessId() : 0;

        SessionUpdate update;
        lock (_lock)
        {
            if (_disposed) return;
            update = _book.Update(now, add, running, idle, _cfg.ForegroundOnly, foregroundPid,
                idleSeconds, _cfg.ExcludeIdle && _cfg.SubtractIdleTime);
        }

        foreach (var (id, title) in update.Started) SessionStarted?.Invoke(id, title);
        foreach (var (id, title, sec) in update.Ended) SessionEnded?.Invoke(id, title, sec);
        Ticked?.Invoke(update.Active);
    }

    // ================================================================

    /// <summary>終了時：計測中のセッションを保存して止める</summary>
    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _disposed = true;
            _book.FinishAll(DateTime.Now);
        }
    }
}
