using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>保存する前の作品の状態（何が変わったかを調べるための控え）</summary>
public record SavedState(bool IsPlayed, DateTime? ClearDate, int? Score, bool Dropped, Review Review)
{
    public static SavedState Of(Game g) => new(g.IsPlayed, g.ClearDate, g.MyScore, g.Status == GameStatus.Dropped, g.Review.Clone());
}

/// <summary>批評空間へ送る内容（まだ反映していない項目から作る）と、送った内容の控え</summary>
public class PushPlan
{
    public required int EgsId { get; init; }
    /// <summary>反映する項目の名前（Game.PendingReview の写し）</summary>
    public required List<string> Keys { get; init; }
    /// <summary>プレイ済・クリア日・得点以外の項目</summary>
    public required List<string> ReviewKeys { get; init; }

    // ---- UpdateReviewAsync に渡す内容 ----
    public bool? Played { get; init; }
    public DateTime? ClearDate { get; init; }
    public int? Score { get; init; }
    public bool ClearScore { get; init; }

    // ---- 送った内容の控え（反映の確認と、通信中に画面で変更されたかの判定に使う） ----
    public required Review SentReview { get; init; }
    public bool SentPlayed { get; init; }
    public DateTime? SentClear { get; init; }
    public int? SentScore { get; init; }
    public bool SentDropped { get; init; }

    /// <summary>感想などの項目を、フォームの欄の名前と値にしたもの（なければ null）</summary>
    public Dictionary<string, string?>? FormValues(EgsConfig cfg) =>
        ReviewKeys.Count > 0 ? SentReview.ToFormValues(ReviewKeys, SentDropped, cfg.ReviewFormField) : null;
}

/// <summary>
/// ツール → 批評空間の規則：保存で何が変わったか、何を送るか、送った結果をどう確かめるか。
/// 画面にも通信にも触れないので、そのままテストできる。
/// </summary>
public static class PushEngine
{
    /// <summary>保存で変わった項目（Review.Keys の名前＋ played / cleardate / score / giveup）</summary>
    public static List<string> ChangedKeys(SavedState before, Game after)
    {
        var keys = Review.Diff(before.Review, after.Review);
        if (before.Dropped != (after.Status == GameStatus.Dropped)) keys.Add("giveup"); // 中断＝批評空間の「放棄」
        if (before.IsPlayed != after.IsPlayed) keys.Add("played");
        if (before.ClearDate?.Date != after.ClearDate?.Date) keys.Add("cleardate");
        // 空欄にした場合も反映する（批評空間は、得点があるとプレイ済を外せないため）
        if (before.Score != after.MyScore) keys.Add("score");
        return keys;
    }

    /// <summary>未反映の項目から、送る内容を作る。批評空間と連携していない・未反映がなければ null</summary>
    public static PushPlan? Plan(Game g)
    {
        if (g.EgsId is not int egsId || g.PendingReview.Count == 0) return null;

        var keys = g.PendingReview.ToList();
        var pushPlayed = keys.Contains("played") || keys.Contains("cleardate");
        var pushScore = keys.Contains("score"); // 空欄にした場合は、空にして送る
        var sentPlayed = g.IsPlayed;
        var sentScore = g.MyScore;

        return new PushPlan
        {
            EgsId = egsId,
            Keys = keys,
            ReviewKeys = keys.Where(k => k is not ("played" or "cleardate" or "score")).ToList(),
            Played = pushPlayed ? sentPlayed : null,
            ClearDate = pushPlayed && sentPlayed ? g.ClearDate : null,
            Score = pushScore ? sentScore : null,
            ClearScore = pushScore && sentScore == null,
            SentReview = g.Review.Clone(),
            SentPlayed = sentPlayed,
            SentClear = g.ClearDate?.Date,
            SentScore = sentScore,
            SentDropped = g.Status == GameStatus.Dropped,
        };
    }

    /// <summary>
    /// 送ったあとに批評空間を読み直した結果で、反映できた項目を「反映済み」にする。
    ///  - 批評空間で実際に変わったことを確かめた項目だけを反映済みにする
    ///  - 通信中に画面で同じ項目を変更していたら、その変更はまだ未反映のまま残す
    /// </summary>
    /// <param name="fresh">読み直した直後の作品（DB の最新の内容）。この内容を更新する</param>
    /// <param name="checkedOk">読み直しに成功したか（失敗したら、何も反映済みにしない）</param>
    /// <returns>反映済みになった項目</returns>
    public static List<string> Complete(PushPlan plan, bool checkedOk, EgsUserRecord? rec, Review? remote, Game fresh)
    {
        var done = new List<string>();
        if (checkedOk)
        {
            foreach (var key in plan.Keys)
            {
                var reflected = key switch
                {
                    "played" => (rec?.Played ?? false) == plan.SentPlayed,
                    "cleardate" => !plan.SentPlayed || plan.SentClear == null || rec?.ClearDate?.Date == plan.SentClear,
                    "score" => rec?.Score == plan.SentScore,
                    "giveup" => (rec?.GiveUp ?? false) == plan.SentDropped,
                    // 批評空間に記録の行そのものがない（購入予定を「ー」にして、空になった記録が消えたときなど）は、
                    // 送った値が空なら反映済み。空でない値を送ったのに行がないときは、反映されていない
                    _ when remote == null => rec == null && !Review.Diff(plan.SentReview, new Review()).Contains(key),
                    _ => !remote.Unreadable.Contains(key) && !Review.Diff(plan.SentReview, remote).Contains(key),
                };
                var unchangedLocally = key switch
                {
                    "played" => fresh.IsPlayed == plan.SentPlayed,
                    "cleardate" => fresh.ClearDate?.Date == plan.SentClear,
                    "score" => fresh.MyScore == plan.SentScore,
                    "giveup" => (fresh.Status == GameStatus.Dropped) == plan.SentDropped,
                    _ => !Review.Diff(plan.SentReview, fresh.Review).Contains(key),
                };
                if (reflected && unchangedLocally) done.Add(key);
            }
            SyncEngine.ApplyEgsState(fresh, rec);
            if (remote != null && !fresh.PendingReview.Contains("possession")) fresh.EgsPossession = remote.Possession;
        }

        fresh.PendingReview.ExceptWith(done);
        if (done.Contains("possession")) fresh.EgsPossession = plan.SentReview.Possession;
        return done;
    }
}
