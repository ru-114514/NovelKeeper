using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Data.Sqlite;

namespace NovelKeeper.Tests;

public class BeforePurchaseCleanupTests
{
    private static Game Before(string title, int? egsId = 1) =>
        new() { Title = title, EgsId = egsId, Review = new Review { Possession = false } };

    [Fact]
    public void Candidates_AreOnlyBeforePurchaseWorks()
    {
        var owned = new Game { Title = "所持", EgsPossession = true };
        var played = new Game { Title = "プレイ済", Status = GameStatus.Played, EgsPlayed = true };
        Assert.Equal(["購入前"], BeforePurchaseCleanup.Candidates([owned, Before("購入前"), played]).Select(g => g.Title));
    }

    [Fact]
    public void Prepare_ClearsPurchaseWillAndScore_AndAlwaysMarksThemPending()
    {
        var g = Before("A");
        g.Review.PurchaseWill = "1";
        g.Review.BeforeScore = 80;

        BeforePurchaseCleanup.Prepare(g);

        Assert.Equal("", g.Review.PurchaseWill);
        Assert.Null(g.Review.BeforeScore);
        Assert.Contains("purchase_will", g.PendingReview);
        Assert.Contains("before_tokuten", g.PendingReview);

        // 手元がすでに空でも、批評空間側に残っているかもしれないので、送る
        var empty = Before("B");
        BeforePurchaseCleanup.Prepare(empty);
        Assert.Contains("purchase_will", empty.PendingReview);
        var plan = PushEngine.Plan(empty)!;
        Assert.Equal("", plan.FormValues(new EgsConfig())!["data[before_purchase_will]"]);
    }

    [Fact]
    public void IsReflected_OnlyWhenBothItemsAreNoLongerPending()
    {
        var g = Before("A");
        BeforePurchaseCleanup.Prepare(g);
        Assert.False(BeforePurchaseCleanup.IsReflected(g));

        g.PendingReview.Remove("purchase_will");
        Assert.False(BeforePurchaseCleanup.IsReflected(g));

        g.PendingReview.Remove("before_tokuten");
        Assert.True(BeforePurchaseCleanup.IsReflected(g));
    }

    [Fact]
    public void RowGoneFromSite_AfterPrepare_IsConfirmed_ThroughTheNormalPushPath()
    {
        // 購入予定を「ー」にして記録が消えた（行がない・記録もない）→ 反映済みになり、削除してよい
        var g = Before("A");
        g.Review.PurchaseWill = "1";
        BeforePurchaseCleanup.Prepare(g);
        var plan = PushEngine.Plan(g)!;

        var fresh = Before("A");
        BeforePurchaseCleanup.Prepare(fresh);
        PushEngine.Complete(plan, true, null, null, fresh);

        Assert.True(BeforePurchaseCleanup.IsReflected(fresh));
    }

    [Fact]
    public void HasOwnContent_FlagsScoreAndReview()
    {
        Assert.False(BeforePurchaseCleanup.HasOwnContent(Before("A")));

        var scored = Before("B"); scored.MyScore = 70;
        Assert.True(BeforePurchaseCleanup.HasOwnContent(scored));

        var hitokoto = Before("C"); hitokoto.Review.Hitokoto = "楽しみ";
        Assert.True(BeforePurchaseCleanup.HasOwnContent(hitokoto));

        var memo = Before("D"); memo.Review.Memo = "メモ";
        Assert.True(BeforePurchaseCleanup.HasOwnContent(memo));
    }

    [Fact]
    public void Snapshot_CopiesTheDatabase_WithoutTouchingDailyBackups()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        var dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = Path.Combine(dir, "games.db");
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (42);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var backups = Path.Combine(dir, "backups");
            var path = DbBackup.Snapshot(db, backups, "before_cleanup");

            Assert.NotNull(path);
            Assert.StartsWith("before_cleanup_", Path.GetFileName(path));
            Assert.Empty(Directory.GetFiles(backups, "*.tmp"));
            using (var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT x FROM t";
                Assert.Equal(42L, cmd.ExecuteScalar());
            }
            SqliteConnection.ClearAllPools();

            Assert.Null(DbBackup.Snapshot(Path.Combine(dir, "missing.db"), backups, "before_cleanup"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class BeforePurchaseCleanupEraseTests
{
    [Fact]
    public void Prepare_EraseContent_ClearsScoreAndComments_AndMarksThemPending()
    {
        var g = new Game { Title = "A", MyScore = 80, Review = new Review { Hitokoto = "良い", Memo = "長文", PurchaseWill = "0_必ず購入" } };

        BeforePurchaseCleanup.Prepare(g, eraseContent: true);

        Assert.Null(g.MyScore);
        Assert.Equal("", g.Review.Hitokoto);
        Assert.Equal("", g.Review.Memo);
        Assert.Superset(new HashSet<string> { "purchase_will", "before_tokuten", "score", "hitokoto", "memo" }, g.PendingReview);
    }

    [Fact]
    public void Prepare_Default_KeepsScoreAndComments()
    {
        var g = new Game { Title = "A", MyScore = 80, Review = new Review { Hitokoto = "良い" } };
        BeforePurchaseCleanup.Prepare(g);
        Assert.Equal(80, g.MyScore);
        Assert.Equal("良い", g.Review.Hitokoto);
        Assert.DoesNotContain("score", g.PendingReview);
    }

    [Fact]
    public void IsReflected_WithErase_AlsoNeedsScoreAndCommentsReflected()
    {
        var g = new Game { Title = "A" };
        g.PendingReview.Add("score");
        Assert.True(BeforePurchaseCleanup.IsReflected(g));                       // 消さない整理では、得点は見ない
        Assert.False(BeforePurchaseCleanup.IsReflected(g, eraseContent: true));  // 消す整理では、得点が反映済みになるまで待つ
    }
}
