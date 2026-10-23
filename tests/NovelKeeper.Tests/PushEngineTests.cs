using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>ツール → 批評空間の規則（何が変わったか／何を送るか／送った結果の確かめ方）のテスト</summary>
public class PushEngineTests
{
    private static Game NewGame(GameStatus status = GameStatus.Unplayed) => new() { Id = 1, EgsId = 100, Title = "テスト", Status = status };

    private static EgsUserRecord Rec(bool played = false, DateTime? clear = null, bool giveUp = false, int? score = null) =>
        new(100, played, clear, giveUp, score, true);

    // ---------------- ChangedKeys（保存で何が変わったか） ----------------

    [Fact]
    public void ChangedKeys_NothingChanged_IsEmpty()
    {
        var g = NewGame(GameStatus.Played);
        g.MyScore = 80;
        g.ClearDate = new DateTime(2026, 1, 1);
        Assert.Empty(PushEngine.ChangedKeys(SavedState.Of(g), g));
    }

    [Fact]
    public void ChangedKeys_DetectsEachKindOfChange()
    {
        var g = NewGame(GameStatus.Played);
        g.MyScore = 80;
        g.ClearDate = new DateTime(2026, 1, 1);
        var before = SavedState.Of(g);

        g.Status = GameStatus.Unplayed;
        g.ClearDate = null;
        g.MyScore = null;
        g.Review = new Review { Hitokoto = "追記" };

        var keys = PushEngine.ChangedKeys(before, g);
        Assert.Contains("played", keys);
        Assert.Contains("cleardate", keys);
        Assert.Contains("score", keys); // 空欄にした場合も、変更として数える
        Assert.Contains("hitokoto", keys);
        Assert.DoesNotContain("giveup", keys);
    }

    [Fact]
    public void ChangedKeys_DroppedIsGiveUp_NotPlayed()
    {
        var g = NewGame();
        var before = SavedState.Of(g);
        g.Status = GameStatus.Dropped;

        var keys = PushEngine.ChangedKeys(before, g);
        Assert.Equal(["giveup"], keys);
    }

    [Fact]
    public void ChangedKeys_ClearDateComparedByDayOnly()
    {
        var g = NewGame(GameStatus.Played);
        g.ClearDate = new DateTime(2026, 1, 1, 0, 0, 0);
        var before = SavedState.Of(g);
        g.ClearDate = new DateTime(2026, 1, 1, 13, 30, 0);
        Assert.DoesNotContain("cleardate", PushEngine.ChangedKeys(before, g));
    }

    // ---------------- Plan（何を送るか） ----------------

    [Fact]
    public void Plan_IsNull_WhenNotLinkedOrNothingPending()
    {
        var unlinked = NewGame();
        unlinked.EgsId = null;
        unlinked.PendingReview.Add("score");
        Assert.Null(PushEngine.Plan(unlinked));

        Assert.Null(PushEngine.Plan(NewGame()));
    }

    [Fact]
    public void Plan_Unplayed_SendsPlayedFalse_WithoutClearDate()
    {
        var g = NewGame();
        g.PendingReview.UnionWith(["played", "cleardate"]);
        var plan = PushEngine.Plan(g)!;

        Assert.False(plan.Played);
        Assert.Null(plan.ClearDate);
        Assert.Null(plan.Score);
        Assert.False(plan.ClearScore); // 得点は触らない
    }

    [Fact]
    public void Plan_Played_SendsClearDate()
    {
        var g = NewGame(GameStatus.Played);
        g.ClearDate = new DateTime(2026, 5, 6);
        g.PendingReview.Add("cleardate");
        var plan = PushEngine.Plan(g)!;

        Assert.True(plan.Played);
        Assert.Equal(new DateTime(2026, 5, 6), plan.ClearDate);
    }

    [Fact]
    public void Plan_ScoreEmptied_ClearsSiteScore_ButOnlyWhenPending()
    {
        var g = NewGame();
        g.MyScore = null;
        g.PendingReview.Add("score");
        var plan = PushEngine.Plan(g)!;
        Assert.True(plan.ClearScore);
        Assert.Null(plan.Score);

        var withScore = NewGame();
        withScore.MyScore = 70;
        withScore.PendingReview.Add("score");
        var plan2 = PushEngine.Plan(withScore)!;
        Assert.Equal(70, plan2.Score);
        Assert.False(plan2.ClearScore);

        var notPending = NewGame();
        notPending.MyScore = null;
        notPending.PendingReview.Add("hitokoto");
        var plan3 = PushEngine.Plan(notPending)!;
        Assert.False(plan3.ClearScore); // 得点が未反映でなければ、空にしない
        Assert.Null(plan3.Score);
    }

    [Fact]
    public void Plan_SeparatesReviewKeys_AndBuildsFormValuesFromConfig()
    {
        var g = NewGame();
        g.Review.Hitokoto = "よかった";
        g.PendingReview.UnionWith(["score", "hitokoto"]);
        var plan = PushEngine.Plan(g)!;

        Assert.Equal(["hitokoto"], plan.ReviewKeys);
        Assert.Equal("よかった", plan.FormValues(new EgsConfig())!["data[hitokoto]"]);

        // 批評空間の欄の名前が変わったら、設定で追従できる
        var cfg = new EgsConfig();
        cfg.ReviewFields["hitokoto"] = "data[comment]";
        Assert.Equal("よかった", plan.FormValues(cfg)!["data[comment]"]);

        var scoreOnly = NewGame();
        scoreOnly.PendingReview.Add("score");
        Assert.Null(PushEngine.Plan(scoreOnly)!.FormValues(new EgsConfig()));
    }

    [Fact]
    public void Plan_TakesASnapshot_LaterEditsDoNotChangeIt()
    {
        var g = NewGame();
        g.Review.Hitokoto = "送る内容";
        g.PendingReview.Add("hitokoto");
        var plan = PushEngine.Plan(g)!;

        g.Review.Hitokoto = "通信中に変えた内容";
        Assert.Equal("送る内容", plan.SentReview.Hitokoto);
    }

    // ---------------- Complete（送った結果の確かめ方） ----------------

    [Fact]
    public void Complete_MarksReflectedKeysDone_AndKeepsTheRest()
    {
        var g = NewGame();
        g.MyScore = 70;
        g.PendingReview.UnionWith(["played", "score"]);
        var plan = PushEngine.Plan(g)!;

        // 批評空間では、得点は変わったが、プレイ済は外れなかった
        var fresh = NewGame();
        fresh.MyScore = 70;
        fresh.PendingReview.UnionWith(["played", "score"]);
        var done = PushEngine.Complete(plan, true, Rec(played: true, score: 70), null, fresh);

        Assert.Equal(["score"], done);
        Assert.Equal(["played"], fresh.PendingReview);
        Assert.True(fresh.EgsPlayed);
        Assert.Equal(70, fresh.EgsScore);
    }

    [Fact]
    public void Complete_ScoreCleared_IsReflectedWhenSiteScoreIsGone()
    {
        var g = NewGame();
        g.MyScore = null;
        g.PendingReview.UnionWith(["played", "score"]);
        var plan = PushEngine.Plan(g)!;

        var fresh = NewGame();
        fresh.PendingReview.UnionWith(["played", "score"]);
        var done = PushEngine.Complete(plan, true, Rec(played: false, score: null), null, fresh);

        Assert.Contains("played", done);
        Assert.Contains("score", done);
        Assert.Empty(fresh.PendingReview);

        // 得点が残ったままなら、反映できていない
        var fresh2 = NewGame();
        fresh2.PendingReview.UnionWith(["played", "score"]);
        var done2 = PushEngine.Complete(plan, true, Rec(played: true, score: 80), null, fresh2);
        Assert.Empty(done2);
        Assert.Equal(2, fresh2.PendingReview.Count);
    }

    [Fact]
    public void Complete_WhenReadBackFailed_NothingIsMarkedDone()
    {
        var g = NewGame(GameStatus.Played);
        g.PendingReview.Add("played");
        var plan = PushEngine.Plan(g)!;
        var fresh = NewGame(GameStatus.Played);
        fresh.PendingReview.Add("played");

        var done = PushEngine.Complete(plan, false, null, null, fresh);

        Assert.Empty(done);
        Assert.Contains("played", fresh.PendingReview);
        Assert.False(fresh.EgsPlayed); // 確認できなかったので、批評空間側の状態は更新しない
    }

    [Fact]
    public void Complete_ChangeMadeDuringCommunication_StaysPending()
    {
        var g = NewGame(GameStatus.Played);
        g.PendingReview.Add("played");
        var plan = PushEngine.Plan(g)!;

        // 通信している間に、画面で「未プレイ」に戻された
        var fresh = NewGame(GameStatus.Unplayed);
        fresh.PendingReview.Add("played");
        var done = PushEngine.Complete(plan, true, Rec(played: true), null, fresh);

        Assert.Empty(done);
        Assert.Contains("played", fresh.PendingReview);
    }

    [Fact]
    public void Complete_ReviewKey_NeedsRemoteToMatch_AndUnreadableIsNotTrusted()
    {
        var g = NewGame();
        g.Review.Hitokoto = "送った";
        g.PendingReview.Add("hitokoto");
        var plan = PushEngine.Plan(g)!;

        Game Fresh()
        {
            var f = NewGame();
            f.Review.Hitokoto = "送った";
            f.PendingReview.Add("hitokoto");
            return f;
        }

        Assert.Equal(["hitokoto"], PushEngine.Complete(plan, true, Rec(), new Review { Hitokoto = "送った" }, Fresh()));
        Assert.Empty(PushEngine.Complete(plan, true, Rec(), new Review { Hitokoto = "違う" }, Fresh()));
        Assert.Empty(PushEngine.Complete(plan, true, Rec(), null, Fresh()));

        var unreadable = new Review { Hitokoto = "送った" };
        unreadable.Unreadable.Add("hitokoto");
        Assert.Empty(PushEngine.Complete(plan, true, Rec(), unreadable, Fresh()));
    }

    [Fact]
    public void Complete_NoRowOnTheSite_CountsAsReflected_OnlyForEmptyValues()
    {
        // 購入予定を「ー」にして空になった記録は、批評空間から消える（行がない）。送った値が空なら、反映済み
        Game WithPending(string key, Action<Review> set)
        {
            var g = NewGame();
            set(g.Review);
            g.PendingReview.Add(key);
            return g;
        }

        var cleared = WithPending("purchase_will", r => r.PurchaseWill = "");
        var plan = PushEngine.Plan(cleared)!;
        Assert.Equal(["purchase_will"], PushEngine.Complete(plan, true, null, null, WithPending("purchase_will", r => r.PurchaseWill = "")));

        // 値を送ったのに行がない／読み直しに失敗した／サイトに記録（rec）はあるのに行がない、は反映済みにしない
        var set = WithPending("purchase_will", r => r.PurchaseWill = "1");
        var setPlan = PushEngine.Plan(set)!;
        Assert.Empty(PushEngine.Complete(setPlan, true, null, null, WithPending("purchase_will", r => r.PurchaseWill = "1")));
        Assert.Empty(PushEngine.Complete(plan, false, null, null, WithPending("purchase_will", r => r.PurchaseWill = "")));
        Assert.Empty(PushEngine.Complete(plan, true, Rec(), null, WithPending("purchase_will", r => r.PurchaseWill = "")));
    }

    [Fact]
    public void Complete_Possession_IsRecordedOnceReflected()
    {
        var g = NewGame();
        g.Review.Possession = true;
        g.PendingReview.Add("possession");
        var plan = PushEngine.Plan(g)!;

        var fresh = NewGame();
        fresh.Review.Possession = true;
        fresh.PendingReview.Add("possession");
        var done = PushEngine.Complete(plan, true, Rec(), new Review { Possession = true }, fresh);

        Assert.Equal(["possession"], done);
        Assert.True(fresh.EgsPossession);
    }

    // ---------------- 設定（フォームの欄の名前） ----------------

    [Fact]
    public void ReviewFormField_UsesDefaults_AllowsOverride_AndIgnoresBlankOverride()
    {
        var cfg = new EgsConfig();
        Assert.Equal("data[memo]", cfg.ReviewFormField("memo"));

        cfg.ReviewFields["memo"] = "data[review][memo]";
        Assert.Equal("data[review][memo]", cfg.ReviewFormField("memo"));

        cfg.ReviewFields["memo"] = "  ";
        Assert.Equal("data[memo]", cfg.ReviewFormField("memo"));

        cfg.ReviewFields.Remove("memo"); // 設定ファイルに書かれていない項目も、既定値で動く
        Assert.Equal("data[memo]", cfg.ReviewFormField("memo"));
    }

    [Fact]
    public void EveryReviewKeyHasADefaultFormField()
    {
        var cfg = new EgsConfig();
        var review = new Review();
        var keys = Review.Keys.Select(k => k.Key).ToList();
        var values = review.ToFormValues(keys, false, cfg.ReviewFormField); // どの項目も、欄の名前を引ける（例外にならない）
        Assert.NotEmpty(values);
        Assert.All(values.Keys, name => Assert.StartsWith("data[", name));
    }
}

public class ConfigCompatibilityTests
{
    [Fact]
    public void OldConfigWithoutReviewFields_StillWorks_AndPartialOverrideKeepsOtherDefaults()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<EgsConfig>("""{ "PlayedField": "data[play]" }""")!;
        Assert.Equal("data[hitokoto]", old.ReviewFormField("hitokoto")); // 設定ファイルに ReviewFields がなくても、既定値で動く

        var partial = System.Text.Json.JsonSerializer.Deserialize<EgsConfig>("""{ "ReviewFields": { "memo": "data[x]" } }""")!;
        Assert.Equal("data[x]", partial.ReviewFormField("memo"));
        Assert.Equal("data[hitokoto]", partial.ReviewFormField("hitokoto")); // 書かなかった項目は既定値
    }
}
