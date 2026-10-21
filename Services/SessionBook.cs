using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>計測中の作品1つ分の状態（画面表示用）</summary>
public record ActiveSessionInfo(long GameId, string Title, double Seconds, bool Counting);

/// <summary>プレイ時間の記録の保存先（本番は Database、テストでは記録するだけの偽物）</summary>
public interface ISessionStore
{
    long AddSession(long gameId, DateTime start, DateTime end, long seconds);
    void UpdateSession(long sessionId, DateTime end, long seconds);
    void DeleteSession(long sessionId);
    /// <summary>未プレイの作品だけ「プレイ中」にする</summary>
    void SetPlayingIfUnplayed(long gameId);
}

/// <summary>確認1回分の結果</summary>
public record SessionUpdate(
    IReadOnlyList<(long GameId, string Title)> Started,
    IReadOnlyList<(long GameId, string Title, double Seconds)> Ended,
    IReadOnlyList<ActiveSessionInfo> Active);

/// <summary>
/// プレイ時間の計測の状態管理。一定間隔で「いま起動中の作品」を受け取り、
/// 開始・加算・途中保存・終了を判断する（プロセスにも画面にも触れないので、そのままテストできる）。
///  - 1分ごとに途中経過を保存する（ツールが落ちても、失うのは最大1分）
///  - 見つからなくなった作品は、2回続けて見つからなければ終了とみなす
///  - 10秒より短いセッションは記録しない
///  - スリープ中・放置中・（設定により）裏に回っている間は数えない
/// スレッドセーフではない（呼ぶ側で排他する）。
/// </summary>
public sealed class SessionBook(ISessionStore store)
{
    public const int SaveIntervalSeconds = 60; // 途中経過を保存する間隔
    public const int MinSessionSeconds = 10;   // これより短いセッションは記録しない
    public const int MissesToEnd = 2;          // 何回続けて見つからなければ終了とみなすか

    private sealed class Session
    {
        public long SessionId;
        public long GameId;
        public string Title = "";
        public double Seconds;
        public int Misses;
        public bool Counting;
        public DateTime LastSaved;
    }

    private readonly Dictionary<long, Session> _active = new();

    public bool IsRunning(long gameId) => _active.ContainsKey(gameId);

    /// <summary>
    /// 前回の確認から今回までに、数える秒数。間隔が大きく空いた（スリープしていた）ときは 0。
    /// </summary>
    public static double SecondsToAdd(double elapsedSeconds, int intervalSeconds) =>
        elapsedSeconds > intervalSeconds * 3 ? 0 : Math.Min(elapsedSeconds, intervalSeconds * 2);

    /// <param name="running">いま起動中の作品と、そのプロセス ID</param>
    /// <param name="addSeconds">今回数える秒数（SecondsToAdd の結果）</param>
    /// <param name="idle">放置中（数えない）</param>
    /// <param name="foregroundOnly">前面にあるときだけ数える設定</param>
    /// <param name="foregroundPid">前面のウィンドウのプロセス ID</param>
    public SessionUpdate Update(DateTime now, double addSeconds,
        IReadOnlyDictionary<long, (Game Game, HashSet<int> Pids)> running,
        bool idle, bool foregroundOnly, int foregroundPid)
    {
        var started = new List<(long, string)>();
        var ended = new List<(long, string, double)>();

        foreach (var (gameId, (game, pids)) in running)
        {
            if (!_active.TryGetValue(gameId, out var s))
            {
                // 新しく起動した
                _active[gameId] = new Session
                {
                    GameId = gameId,
                    Title = game.Title,
                    LastSaved = now,
                    Counting = true,
                    SessionId = store.AddSession(gameId, now, now, 0),
                };
                store.SetPlayingIfUnplayed(gameId);
                started.Add((gameId, game.Title));
                continue;
            }

            s.Misses = 0;
            s.Counting = !idle && (!foregroundOnly || pids.Contains(foregroundPid));
            if (s.Counting) s.Seconds += addSeconds;

            if ((now - s.LastSaved).TotalSeconds >= SaveIntervalSeconds)
            {
                store.UpdateSession(s.SessionId, now, (long)s.Seconds);
                s.LastSaved = now;
            }
        }

        // 見つからなくなった作品：続けて見つからなければ終了とみなす
        foreach (var s in _active.Values.Where(x => !running.ContainsKey(x.GameId)).ToList())
        {
            if (++s.Misses < MissesToEnd) continue;
            Finish(s, now);
            ended.Add((s.GameId, s.Title, s.Seconds));
        }

        var active = _active.Values.Select(x => new ActiveSessionInfo(x.GameId, x.Title, x.Seconds, x.Counting)).ToList();
        return new SessionUpdate(started, ended, active);
    }

    /// <summary>終了時：計測中のセッションを、すべて保存して終える</summary>
    public void FinishAll(DateTime now)
    {
        foreach (var s in _active.Values.ToList()) Finish(s, now);
    }

    private void Finish(Session s, DateTime now)
    {
        if (s.Seconds < MinSessionSeconds) store.DeleteSession(s.SessionId);
        else store.UpdateSession(s.SessionId, now, (long)s.Seconds);
        _active.Remove(s.GameId);
    }
}
