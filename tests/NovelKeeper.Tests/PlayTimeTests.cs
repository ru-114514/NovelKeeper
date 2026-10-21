using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>プレイ中かどうかの判定（どのプロセスがどの作品か）のテスト</summary>
public class ProcessMatcherTests
{
    private static readonly string[] NoBroad = [];
    private static readonly string[] GameRoot = [@"D:\novelgame"];

    private static Game G(long id, string exe, string? monitor = null, string? installDir = null, params string[] more)
    {
        var g = new Game { Id = id, Title = $"作品{id}", ExePath = exe, MonitorProcess = monitor, InstallDir = installDir };
        foreach (var p in more) g.Targets.Add(new LaunchTarget { Path = p });
        return g;
    }

    [Fact]
    public void ProcessInsideTheGameFolder_IsThatGame_EvenWhenTheLauncherIsAnotherExe()
    {
        var a = G(1, @"D:\novelgame\A\launcher.exe");
        var matcher = new ProcessMatcher([a], GameRoot);

        Assert.Same(a, matcher.Match("game", @"D:\novelgame\A\bin\game.exe"));
        Assert.Same(a, matcher.Match("game", @"d:\NOVELGAME\a\GAME.EXE")); // 大文字・小文字は区別しない
        Assert.Null(matcher.Match("notepad", @"C:\Windows\notepad.exe"));
        Assert.Null(matcher.Match("game", @"D:\novelgame\AB\game.exe")); // フォルダ名が前方一致しただけの別の場所
        Assert.Null(matcher.Match("game", null));
    }

    [Fact]
    public void DeepestMatchingFolderWins()
    {
        var outer = G(1, @"D:\novelgame\Box\box.exe");
        var inner = G(2, @"D:\novelgame\Box\Part2\p2.exe");
        var matcher = new ProcessMatcher([outer, inner], GameRoot);

        Assert.Same(inner, matcher.Match("p2", @"D:\novelgame\Box\Part2\p2.exe"));
        Assert.Same(outer, matcher.Match("box", @"D:\novelgame\Box\box.exe"));
    }

    [Fact]
    public void EveryRegisteredLaunchTarget_CountsForTheGame()
    {
        var box = G(1, @"D:\games\Vol1\v1.exe", null, null, @"D:\games\Vol1\v1.exe", @"D:\games\Vol2\v2.exe");
        var matcher = new ProcessMatcher([box], NoBroad);

        Assert.Same(box, matcher.Match("v1", @"D:\games\Vol1\v1.exe"));
        Assert.Same(box, matcher.Match("v2", @"D:\games\Vol2\v2.exe")); // 今使っていない収録作でも数える
    }

    [Fact]
    public void ExeDirectlyInABroadFolder_IsMatchedByProcessNameOnly()
    {
        // ゲームを置く親フォルダ自体に exe があるとき、フォルダ判定にすると、同じ場所の別のゲームまで巻き込む
        var x = G(1, @"D:\novelgame\x.exe");
        var matcher = new ProcessMatcher([x], GameRoot);

        Assert.Same(x, matcher.Match("x", @"D:\novelgame\x.exe"));
        Assert.Null(matcher.Match("y", @"D:\novelgame\y.exe"));
    }

    [Theory]
    [InlineData(@"D:\x.exe")]                                              // ドライブ直下
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\x.exe")]   // steamapps\common そのもの
    public void ObviouslyBroadFolders_AreNeverUsedForFolderMatching(string exe)
    {
        var x = G(1, exe);
        var matcher = new ProcessMatcher([x], NoBroad);

        Assert.Same(x, matcher.Match("x", exe));          // exe 名では一致する
        Assert.Null(matcher.Match("other", Path.Combine(Path.GetDirectoryName(exe)!, "other.exe"))); // 同じ場所の別の exe は一致しない
    }

    [Fact]
    public void SteamGame_IsMatchedByInstallFolder_AndByMonitorProcessName()
    {
        var steam = G(1, "steam://rungameid/12345", monitor: "game.exe", installDir: @"D:\SteamLibrary\steamapps\common\Game");
        var matcher = new ProcessMatcher([steam], NoBroad);

        Assert.Same(steam, matcher.Match("anything", @"D:\SteamLibrary\steamapps\common\Game\bin\anything.exe"));
        Assert.Same(steam, matcher.Match("game", null)); // パスが取れなくても、プロセス名で一致する（管理者権限のゲームなど）
        Assert.Null(matcher.Match("other", @"D:\SteamLibrary\steamapps\common\Other\other.exe"));
    }

    [Fact]
    public void Count_IsZero_WhenNothingCanBeUsedForMatching()
    {
        Assert.Equal(0, new ProcessMatcher([], NoBroad).Count);
        Assert.Equal(0, new ProcessMatcher([new Game { Id = 1, Title = "未設定" }], NoBroad).Count);
        Assert.True(new ProcessMatcher([G(1, @"D:\g\a.exe")], NoBroad).Count > 0);
    }

    [Fact]
    public void InvalidBroadFolderEntry_DoesNotDisableMatching()
    {
        var a = G(1, @"D:\novelgame\A\a.exe");
        var matcher = new ProcessMatcher([a], ["", "   ", "D:\\bad|path<>"]);
        Assert.Same(a, matcher.Match("a", @"D:\novelgame\A\a.exe"));
    }
}

/// <summary>プレイ時間の記録の状態管理（開始・加算・保存・終了）のテスト</summary>
public class SessionBookTests
{
    private sealed class FakeStore : ISessionStore
    {
        public List<string> Calls { get; } = new();
        public Dictionary<long, long> Seconds { get; } = new();
        private long _next = 100;

        public long AddSession(long gameId, DateTime start, DateTime end, long seconds)
        {
            Calls.Add($"add:{gameId}");
            Seconds[_next] = seconds;
            return _next++;
        }
        public void UpdateSession(long sessionId, DateTime end, long seconds)
        {
            Calls.Add($"update:{sessionId}:{seconds}");
            Seconds[sessionId] = seconds;
        }
        public void DeleteSession(long sessionId)
        {
            Calls.Add($"delete:{sessionId}");
            Seconds.Remove(sessionId);
        }
        public void SetPlayingIfUnplayed(long gameId) => Calls.Add($"playing:{gameId}");
    }

    private static readonly Game GameA = new() { Id = 1, Title = "A" };
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static IReadOnlyDictionary<long, (Game Game, HashSet<int> Pids)> Running(params int[] pids) =>
        new Dictionary<long, (Game, HashSet<int>)> { [1] = (GameA, new HashSet<int>(pids)) };

    private static readonly IReadOnlyDictionary<long, (Game Game, HashSet<int> Pids)> None =
        new Dictionary<long, (Game, HashSet<int>)>();

    private static SessionUpdate Tick(SessionBook book, int secondsFromStart, double add, IReadOnlyDictionary<long, (Game Game, HashSet<int> Pids)> running,
        bool idle = false, bool foregroundOnly = false, int foregroundPid = 0) =>
        book.Update(T0.AddSeconds(secondsFromStart), add, running, idle, foregroundOnly, foregroundPid);

    [Fact]
    public void FirstSighting_StartsASession_AndMarksTheGamePlaying_WithoutCountingYet()
    {
        var store = new FakeStore();
        var book = new SessionBook(store);

        var u = Tick(book, 0, 10, Running(1));

        Assert.Equal([(1L, "A")], u.Started);
        Assert.Equal(["add:1", "playing:1"], store.Calls);
        Assert.Equal(0, u.Active.Single().Seconds);
        Assert.True(book.IsRunning(1));
    }

    [Fact]
    public void LaterTicks_AccumulateSeconds()
    {
        var book = new SessionBook(new FakeStore());
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 10, Running(1));
        var u = Tick(book, 20, 10, Running(1));

        Assert.Equal(20, u.Active.Single().Seconds);
        Assert.True(u.Active.Single().Counting);
    }

    [Fact]
    public void Idle_StopsCounting_ButTheSessionContinues()
    {
        var book = new SessionBook(new FakeStore());
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 10, Running(1));
        var idle = Tick(book, 20, 10, Running(1), idle: true);

        Assert.Equal(10, idle.Active.Single().Seconds);
        Assert.False(idle.Active.Single().Counting);

        var back = Tick(book, 30, 10, Running(1));
        Assert.Equal(20, back.Active.Single().Seconds);
    }

    [Fact]
    public void ForegroundOnly_CountsOnlyWhileTheGameWindowIsInFront()
    {
        var book = new SessionBook(new FakeStore());
        Tick(book, 0, 10, Running(7), foregroundOnly: true, foregroundPid: 7);
        var inFront = Tick(book, 10, 10, Running(7), foregroundOnly: true, foregroundPid: 7);
        var behind = Tick(book, 20, 10, Running(7), foregroundOnly: true, foregroundPid: 99);

        Assert.Equal(10, inFront.Active.Single().Seconds);
        Assert.Equal(10, behind.Active.Single().Seconds);
        Assert.False(behind.Active.Single().Counting);
    }

    [Fact]
    public void ProgressIsSavedEveryMinute_NotBefore()
    {
        var store = new FakeStore();
        var book = new SessionBook(store);
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 10, Running(1));
        Tick(book, 50, 10, Running(1));
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("update:"));

        Tick(book, 60, 10, Running(1));
        Assert.Single(store.Calls, c => c.StartsWith("update:"));
        Assert.Equal(30, store.Seconds[100]); // 開始後の3回分（10+10+10）
    }

    [Fact]
    public void Ends_OnlyAfterTwoConsecutiveMisses_AndReappearingResetsTheCount()
    {
        var store = new FakeStore();
        var book = new SessionBook(store);
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 10, Running(1));
        Tick(book, 20, 10, Running(1));

        Assert.Empty(Tick(book, 30, 10, None).Ended);     // 1回見つからない
        Assert.Empty(Tick(book, 40, 10, Running(1)).Ended); // 戻ってきた：数え直し
        Assert.Empty(Tick(book, 50, 10, None).Ended);
        var end = Tick(book, 60, 10, None);                // 2回続けて見つからない

        Assert.Single(end.Ended);
        Assert.False(book.IsRunning(1));
        Assert.Equal(30, end.Ended.Single().Seconds);
        Assert.Contains("update:100:30", store.Calls);
    }

    [Fact]
    public void SessionShorterThanTenSeconds_IsDeleted_NotRecorded()
    {
        var store = new FakeStore();
        var book = new SessionBook(store);
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 5, Running(1)); // 5秒だけ数えた
        Tick(book, 20, 10, None);
        Tick(book, 30, 10, None);

        Assert.Contains("delete:100", store.Calls);
        Assert.DoesNotContain(store.Calls, c => c.StartsWith("update:"));
    }

    [Fact]
    public void FinishAll_SavesRunningSessions()
    {
        var store = new FakeStore();
        var book = new SessionBook(store);
        Tick(book, 0, 10, Running(1));
        Tick(book, 10, 10, Running(1));
        Tick(book, 20, 10, Running(1));

        book.FinishAll(T0.AddSeconds(25));

        Assert.Contains("update:100:20", store.Calls);
        Assert.False(book.IsRunning(1));
    }

    [Theory]
    [InlineData(10, 10)]   // ふつう
    [InlineData(12.5, 12.5)]
    [InlineData(25, 20)]   // 遅れても、2回分まで
    [InlineData(30, 20)]
    [InlineData(31, 0)]    // 大きく空いた＝スリープしていた：数えない
    [InlineData(3600, 0)]
    public void SecondsToAdd_CountsNormalTicks_AndIgnoresSleep(double elapsed, double expected) =>
        Assert.Equal(expected, SessionBook.SecondsToAdd(elapsed, 10));
}
