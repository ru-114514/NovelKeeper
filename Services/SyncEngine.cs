using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>ツールと批評空間で内容が食い違ったときの種類</summary>
public enum Conflict { ClearDate, Score, Memo }

/// <summary>食い違いをどちらで解決するか決める（画面では確認ダイアログ、テストでは固定の答え）</summary>
public interface IConflictResolver
{
    /// <returns>true ＝ ツールの内容を批評空間に反映する／false ＝ 批評空間の内容をツールに取り込む</returns>
    bool UseTool(Game game, Conflict kind, string toolText, string siteText);
}

/// <summary>同期の結果の件数</summary>
public class SyncStats
{
    public int Imported;      // 批評空間に合わせて「プレイ済」にした作品
    public int Dropped;       // 批評空間に合わせて「中断」にした作品
    public int ScoreImported; // 批評空間の得点を取り込んだ作品
}

/// <summary>
/// 批評空間 → ツールの突き合わせ（同期）の規則。画面にも通信にも触れないので、そのままテストできる。
/// ツール側で変更してまだ反映していない項目（Game.PendingReview）は、批評空間の内容で上書きしない。
/// </summary>
public static class SyncEngine
{
    /// <summary>批評空間側の状態（最後に同期したときの値）を記録する</summary>
    public static void ApplyEgsState(Game g, EgsUserRecord? rec)
    {
        g.EgsPlayed = rec?.Played ?? false;
        g.EgsGiveUp = rec?.GiveUp ?? false;
        g.EgsPossession = rec?.Possession; // 批評空間に登録がなければ「不明」
        g.EgsScore = rec?.Score;
        g.EgsClearDate = rec?.ClearDate;
        g.EgsSyncedAt = DateTime.Now;
    }

    /// <summary>
    /// 批評空間の応答が「記録0件」でも、前回までは記録があったなら、取得に失敗した可能性がある
    /// （そのまま同期すると、全作品が批評空間で未登録のように扱われる）。
    /// </summary>
    public static bool LooksLikeLostRecords(IEnumerable<Game> games, int recordCount) =>
        recordCount == 0 && games.Any(g => g.EgsId != null && g.EgsSyncedAt != null && (g.EgsPlayed || g.EgsScore != null));

    /// <summary>批評空間から読み込んだ感想などを取り込む（まだ反映していない項目は、ツールの内容を残す）</summary>
    public static void ApplyRemoteReview(Game g, Review remote, IConflictResolver? resolver)
    {
        var different = Review.Diff(g.Review, remote);
        foreach (var (key, _) in Review.Keys)
        {
            if (key == "giveup") continue; // 放棄は状態（中断）として別に扱う
            if (remote.Unreadable.Contains(key)) continue; // 読み込めなかった項目は、ツールの内容のまま
            if (!different.Contains(key))
            {
                g.PendingReview.Remove(key); // 同じ内容なら、反映済みとみなす
                continue;
            }
            if (!g.PendingReview.Contains(key))
            {
                g.Review.CopyFrom(remote, key);
                continue;
            }
            // ツールで変更してまだ反映していない → ツールの内容を残して、反映する。
            // 長文感想だけは、批評空間側にも別の内容があれば、どちらを使うか聞く
            if (key == "memo" && Review.Norm(remote.Memo) != "" && Review.Norm(remote.Memo) != Review.Norm(g.Review.Memo) &&
                resolver != null && !resolver.UseTool(g, Conflict.Memo, Head(g.Review.Memo), Head(remote.Memo)))
            {
                g.Review.CopyFrom(remote, key);
                g.PendingReview.Remove(key);
            }
        }
        if (!g.PendingReview.Contains("possession")) g.EgsPossession = remote.Possession;
    }

    private static string Head(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    /// <summary>作品1つ分の同期：批評空間の記録を突き合わせて、ツールの内容を更新する</summary>
    /// <param name="info">作品の最新情報（中央値など）。取得できなかったときは null</param>
    /// <param name="remote">批評空間の感想など。取得できなかったときは null</param>
    public static void ApplyRecord(Game g, EgsUserRecord? rec, EgsGameInfo? info, Review? remote,
        IConflictResolver resolver, SyncStats stats)
    {
        ApplyEgsState(g, rec);
        if (info != null)
        {
            if (!string.IsNullOrWhiteSpace(info.SellDay)) g.SellDay = info.SellDay;
            g.Median = info.Median ?? g.Median;
            g.EgsAverage = info.Average ?? g.EgsAverage;
            g.EgsCount = info.Count ?? g.EgsCount;
        }

        // ---- 所持・感想など（ツールで変更してまだ反映していない項目は、ツールの内容を反映する） ----
        if (remote != null) ApplyRemoteReview(g, remote, resolver);

        var remotePlayed = rec?.Played ?? false;
        var remoteGiveUp = rec?.GiveUp ?? false;

        // ---- 放棄（ツールの「中断」） ----
        if (g.PendingReview.Contains("giveup") && remoteGiveUp == (g.Status == GameStatus.Dropped))
            g.PendingReview.Remove("giveup"); // すでに同じ

        // ---- プレイ済・クリア日 ----
        if (g.PendingReview.Contains("played") || g.PendingReview.Contains("cleardate"))
        {
            // ツールで変更してまだ反映していない → ツールの内容を反映する（同じなら反映済みにする）
            var same = remotePlayed == g.IsPlayed &&
                       (!g.IsPlayed || g.ClearDate == null || rec?.ClearDate?.Date == g.ClearDate?.Date);
            if (same)
            {
                g.PendingReview.Remove("played");
                g.PendingReview.Remove("cleardate");
            }
        }
        else if (g.PendingReview.Contains("giveup"))
        {
            // 中断の変更をまだ反映していない → 批評空間の状態では上書きしない
        }
        else if (remoteGiveUp)
        {
            // 批評空間で「放棄」→ ツールでは「中断」（プレイ済の☑より優先）
            if (g.Status != GameStatus.Dropped) { g.Status = GameStatus.Dropped; stats.Dropped++; }
        }
        else if (remotePlayed)
        {
            // 批評空間でプレイ済 → ツールもプレイ済にする
            if (!g.IsPlayed) { g.Status = GameStatus.Played; stats.Imported++; }

            if (g.ClearDate == null)
            {
                g.ClearDate = rec!.ClearDate;
            }
            else if (rec!.ClearDate == null)
            {
                g.PendingReview.Add("cleardate"); // 批評空間にクリア日がない → ツールの日付を反映
            }
            else if (rec.ClearDate.Value.Date != g.ClearDate.Value.Date)
            {
                // クリア日が食い違う → どちらを使うか選んでもらう
                if (resolver.UseTool(g, Conflict.ClearDate, $"{g.ClearDate:yyyy/MM/dd}", $"{rec.ClearDate:yyyy/MM/dd}"))
                    g.PendingReview.Add("cleardate");
                else
                    g.ClearDate = rec.ClearDate;
            }
        }
        else if (g.IsPlayed)
        {
            g.PendingReview.Add("played"); // ツールではプレイ済だが、批評空間では未登録
            g.PendingReview.Add("cleardate");
        }

        // ---- 得点 ----
        var siteScore = rec?.Score;
        if (g.PendingReview.Contains("score"))
        {
            // 同じなら反映済み。ツールの得点が空欄で批評空間に得点が残っているときは、
            // 「空欄にした」操作がまだ反映されていないので、未反映のまま残す（ツールで空欄にして保存したときだけ、この状態になる）
            if (g.MyScore == siteScore) g.PendingReview.Remove("score");
        }
        else if (g.MyScore == null && siteScore != null)
        {
            g.MyScore = siteScore;
            stats.ScoreImported++;
        }
        else if (g.MyScore != null && siteScore == null)
        {
            g.PendingReview.Add("score");
        }
        else if (g.MyScore != null && siteScore != null && g.MyScore != siteScore)
        {
            if (resolver.UseTool(g, Conflict.Score, g.MyScore.ToString()!, siteScore.ToString()!))
                g.PendingReview.Add("score");
            else
                g.MyScore = siteScore;
        }
    }
}
