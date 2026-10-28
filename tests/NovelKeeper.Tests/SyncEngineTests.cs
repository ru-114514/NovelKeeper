using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>同期の規則（批評空間 → ツール）のテスト。リモートのアカウントを書き換える部分の土台なので、特に重要</summary>
public class SyncEngineTests
{
    /// <summary>決まった答えを返す（呼ばれた記録も残す）</summary>
    private sealed class FakeResolver(bool useTool) : IConflictResolver
    {
        public List<Conflict> Asked { get; } = new();
        public bool UseTool(Game game, Conflict kind, string toolText, string siteText)
        {
            Asked.Add(kind);
            return useTool;
        }
    }

    private static Game NewGame(GameStatus status = GameStatus.Unplayed) => new() { Id = 1, EgsId = 100, Title = "テスト", Status = status };

    private static EgsUserRecord Rec(bool played = false, DateTime? clear = null, bool giveUp = false, int? score = null, bool possession = true) =>
        new(100, played, clear, giveUp, score, possession);

    private static SyncStats Apply(Game g, EgsUserRecord? rec, IConflictResolver? resolver = null, Review? remote = null, EgsGameInfo? info = null)
    {
        var stats = new SyncStats();
        SyncEngine.ApplyRecord(g, rec, info, remote, resolver ?? new FakeResolver(true), stats);
        return stats;
    }

    // ---------------- プレイ済・クリア日 ----------------

    [Fact]
    public void RemotePlayed_MarksToolPlayedAndTakesClearDate()
    {
        var g = NewGame();
        var stats = Apply(g, Rec(played: true, clear: new DateTime(2026, 3, 1)));

        Assert.Equal(GameStatus.Played, g.Status);
        Assert.Equal(new DateTime(2026, 3, 1), g.ClearDate);
        Assert.Equal(1, stats.Imported);
        Assert.Empty(g.PendingReview);
    }

    [Fact]
    public void RemoteGiveUp_MarksToolDropped_EvenIfPlayed()
    {
        var g = NewGame();
        var stats = Apply(g, Rec(played: true, giveUp: true));

        Assert.Equal(GameStatus.Dropped, g.Status);
        Assert.Equal(1, stats.Dropped);
    }

    [Fact]
    public void ToolPlayed_ButNotRegisteredOnSite_QueuesPush()
    {
        var g = NewGame(GameStatus.Played);
        g.ClearDate = new DateTime(2026, 1, 2);
        Apply(g, null);

        Assert.Contains("played", g.PendingReview);
        Assert.Contains("cleardate", g.PendingReview);
        Assert.False(g.EgsPlayed);
        Assert.Equal(GameStatus.Played, g.Status); // 批評空間の状態でツールを消さない
    }

    [Fact]
    public void RemoteMissingClearDate_QueuesToolDate()
    {
        var g = NewGame(GameStatus.Played);
        g.ClearDate = new DateTime(2026, 1, 2);
        Apply(g, Rec(played: true, clear: null));

        Assert.Equal(new DateTime(2026, 1, 2), g.ClearDate);
        Assert.Contains("cleardate", g.PendingReview);
    }

    [Fact]
    public void PendingPlayed_IsKeptWhenRemoteDiffers_AndClearedWhenSame()
    {
        var g = NewGame(GameStatus.Played);
        g.PendingReview.Add("played");
        Apply(g, Rec(played: false));
        Assert.Contains("played", g.PendingReview);
        Assert.Equal(GameStatus.Played, g.Status);

        Apply(g, Rec(played: true));
        Assert.DoesNotContain("played", g.PendingReview);
    }

    [Fact]
    public void PendingGiveUp_IsNotOverwrittenBySite()
    {
        var g = NewGame(GameStatus.Dropped);
        g.PendingReview.Add("giveup");
        Apply(g, Rec(played: true, giveUp: false));

        Assert.Equal(GameStatus.Dropped, g.Status);
        Assert.Contains("giveup", g.PendingReview);
    }

    [Fact]
    public void ClearDateConflict_AsksAndFollowsAnswer()
    {
        var site = new DateTime(2026, 5, 5);
        var tool = new DateTime(2026, 4, 4);

        var useTool = new FakeResolver(true);
        var g1 = NewGame(GameStatus.Played);
        g1.ClearDate = tool;
        Apply(g1, Rec(played: true, clear: site), useTool);
        Assert.Equal([Conflict.ClearDate], useTool.Asked);
        Assert.Equal(tool, g1.ClearDate);
        Assert.Contains("cleardate", g1.PendingReview);

        var useSite = new FakeResolver(false);
        var g2 = NewGame(GameStatus.Played);
        g2.ClearDate = tool;
        Apply(g2, Rec(played: true, clear: site), useSite);
        Assert.Equal(site, g2.ClearDate);
        Assert.DoesNotContain("cleardate", g2.PendingReview);
    }

    [Fact]
    public void SameClearDate_DoesNotAsk()
    {
        var resolver = new FakeResolver(true);
        var g = NewGame(GameStatus.Played);
        g.ClearDate = new DateTime(2026, 5, 5);
        Apply(g, Rec(played: true, clear: new DateTime(2026, 5, 5)), resolver);
        Assert.Empty(resolver.Asked);
    }

    // ---------------- 得点 ----------------

    [Fact]
    public void Score_ImportedWhenToolEmpty()
    {
        var g = NewGame();
        var stats = Apply(g, Rec(score: 85));
        Assert.Equal(85, g.MyScore);
        Assert.Equal(1, stats.ScoreImported);
    }

    [Fact]
    public void Score_ToolOnly_QueuesPush()
    {
        var g = NewGame();
        g.MyScore = 70;
        Apply(g, Rec(score: null));
        Assert.Contains("score", g.PendingReview);
    }

    [Fact]
    public void Score_ClearedInTool_StaysPendingUntilSiteIsCleared()
    {
        var g = NewGame();
        g.MyScore = null;
        g.PendingReview.Add("score"); // ツールで空欄にして保存した（まだ反映していない）
        Apply(g, Rec(score: 60));
        Assert.Contains("score", g.PendingReview); // 空にして反映する
        Assert.Null(g.MyScore);                    // 批評空間の得点で上書きしない

        Apply(g, Rec(score: null)); // 反映が済んだ
        Assert.DoesNotContain("score", g.PendingReview);
    }

    [Fact]
    public void Score_EmptyInTool_WithoutPending_ImportsSiteScore_NeverClearsSite()
    {
        var g = NewGame();
        Apply(g, Rec(score: 60));
        Assert.Equal(60, g.MyScore);
        Assert.DoesNotContain("score", g.PendingReview);
    }

    [Fact]
    public void Score_Conflict_FollowsAnswer()
    {
        var g1 = NewGame();
        g1.MyScore = 70;
        Apply(g1, Rec(score: 90), new FakeResolver(true));
        Assert.Equal(70, g1.MyScore);
        Assert.Contains("score", g1.PendingReview);

        var g2 = NewGame();
        g2.MyScore = 70;
        Apply(g2, Rec(score: 90), new FakeResolver(false));
        Assert.Equal(90, g2.MyScore);
        Assert.DoesNotContain("score", g2.PendingReview);
    }

    // ---------------- 感想など ----------------

    [Fact]
    public void RemoteReview_IsImported_WhenNothingPending()
    {
        var g = NewGame();
        var remote = new Review { Hitokoto = "面白かった", TotalPlayTime = 30 };
        Apply(g, Rec(), remote: remote);
        Assert.Equal("面白かった", g.Review.Hitokoto);
        Assert.Equal(30, g.Review.TotalPlayTime);
    }

    [Fact]
    public void PendingReviewField_IsNotOverwrittenByRemote()
    {
        var g = NewGame();
        g.Review.Hitokoto = "ツールで書いた";
        g.PendingReview.Add("hitokoto");
        Apply(g, Rec(), remote: new Review { Hitokoto = "サイトの内容" });
        Assert.Equal("ツールで書いた", g.Review.Hitokoto);
        Assert.Contains("hitokoto", g.PendingReview);
    }

    [Fact]
    public void UnreadableRemoteField_IsSkipped()
    {
        var g = NewGame();
        g.Review.Memo = "ツールの長文";
        var remote = new Review { Memo = "" };
        remote.Unreadable.Add("memo");
        Apply(g, Rec(), remote: remote);
        Assert.Equal("ツールの長文", g.Review.Memo); // 読めなかった項目を空欄で上書きしない
    }

    [Fact]
    public void MemoConflict_AsksAndFollowsAnswer()
    {
        Game Make()
        {
            var g = NewGame();
            g.Review.Memo = "ツールの長文";
            g.PendingReview.Add("memo");
            return g;
        }
        var remote = new Review { Memo = "サイトの長文" };

        var g1 = Make();
        var r1 = new FakeResolver(true);
        Apply(g1, Rec(), r1, remote);
        Assert.Equal([Conflict.Memo], r1.Asked);
        Assert.Equal("ツールの長文", g1.Review.Memo);
        Assert.Contains("memo", g1.PendingReview);

        var g2 = Make();
        Apply(g2, Rec(), new FakeResolver(false), remote);
        Assert.Equal("サイトの長文", g2.Review.Memo);
        Assert.DoesNotContain("memo", g2.PendingReview);
    }

    [Fact]
    public void MemoDifferingOnlyByWhitespace_IsNotAConflict()
    {
        var g = NewGame();
        g.Review.Memo = "a\r\nb";
        g.PendingReview.Add("memo");
        var resolver = new FakeResolver(true);
        Apply(g, Rec(), resolver, new Review { Memo = "ab" });
        Assert.Empty(resolver.Asked);
        Assert.DoesNotContain("memo", g.PendingReview); // 同じ内容は反映済みとみなす
    }

    // ---------------- 作品情報・その他 ----------------

    [Fact]
    public void GameInfo_UpdatesStatsButKeepsOldValuesWhenMissing()
    {
        var g = NewGame();
        g.Median = 70;
        g.SellDay = "2020-01-01";
        Apply(g, Rec(), info: new EgsGameInfo(100, "", null, 75, 300));
        Assert.Equal(70, g.Median);          // 取得できなかった値は、今の値のまま
        Assert.Equal("2020-01-01", g.SellDay);
        Assert.Equal(75, g.EgsAverage);
        Assert.Equal(300, g.EgsCount);
    }

    [Fact]
    public void ApplyEgsState_NullRecordMeansUnknown()
    {
        var g = NewGame();
        g.EgsPlayed = true;
        SyncEngine.ApplyEgsState(g, null);
        Assert.False(g.EgsPlayed);
        Assert.Null(g.EgsPossession);
        Assert.NotNull(g.EgsSyncedAt);
    }

    [Fact]
    public void LooksLikeLostRecords_OnlyWhenPreviouslySyncedPlayedGamesExist()
    {
        var synced = NewGame();
        synced.EgsSyncedAt = DateTime.Now;
        synced.EgsPlayed = true;

        Assert.True(SyncEngine.LooksLikeLostRecords([synced], 0));
        Assert.False(SyncEngine.LooksLikeLostRecords([synced], 5));
        Assert.False(SyncEngine.LooksLikeLostRecords([NewGame()], 0)); // まだ一度も同期していない
    }
}
