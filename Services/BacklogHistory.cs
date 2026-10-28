namespace NovelKeeper.Services;

/// <summary>ある日の積みゲーの本数</summary>
public class BacklogSnapshot
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
}

/// <summary>積みゲーの増減（baselineDate の記録に比べて、delta 本増えた／減った）</summary>
public record BacklogChange(DateTime BaselineDate, int Baseline, int Now, bool IsExact)
{
    public int Delta => Now - Baseline;
}

/// <summary>
/// 積みゲーの本数の記録（アプリを使った日に、その日の本数を1日1件残す）。画面に触れないので、そのままテストできる。
/// 記録が始まる前の本数は分からないので、増減は記録のある日どうしの比較になる
/// </summary>
public static class BacklogHistory
{
    /// <summary>残す記録の上限（古いものから捨てる。1日1件なので約3年分）</summary>
    public const int MaxSnapshots = 1100;

    /// <summary>今日の本数を記録する（同じ日は上書き）。変わったら true</summary>
    public static bool Record(List<BacklogSnapshot> history, int count, DateTime today)
    {
        today = today.Date;
        var same = history.FirstOrDefault(h => h.Date.Date == today);
        if (same != null)
        {
            if (same.Count == count) return false;
            same.Count = count;
            return true;
        }
        history.Add(new BacklogSnapshot { Date = today, Count = count });
        if (history.Count > MaxSnapshots) history.RemoveRange(0, history.Count - MaxSnapshots);
        return true;
    }

    /// <summary>
    /// 期間（from〜to）の積みゲーの増減。基準は「from より前の最後の記録」（なければ、いちばん古い記録）。
    /// 期間の終わりの本数は、to が今日以降なら現在の本数、過去なら to までの最後の記録。基準が取れない・終わりが分からないときは null
    /// </summary>
    public static BacklogChange? Change(IReadOnlyList<BacklogSnapshot> history, DateTime from, DateTime to, int current, DateTime today)
    {
        if (history.Count == 0) return null;
        from = from.Date; to = to.Date; today = today.Date;
        var sorted = history.OrderBy(h => h.Date).ToList();

        var before = sorted.LastOrDefault(h => h.Date < from);
        var baseline = before ?? sorted[0];
        var exact = before != null;

        int now;
        if (to >= today) now = current;
        else
        {
            var end = sorted.LastOrDefault(h => h.Date <= to);
            if (end == null) return null;
            now = end.Count;
        }
        if (baseline.Date > to) return null; // 期間より新しい記録しかない
        return new BacklogChange(baseline.Date, baseline.Count, now, exact);
    }

    /// <summary>「前月比 −3本（9/30 の記録より）」のような一言</summary>
    public static string Describe(BacklogChange? c)
    {
        if (c == null) return "増減の記録はまだありません（アプリを使うと日ごとに記録されます）";
        var sign = c.Delta > 0 ? $"+{c.Delta}" : c.Delta < 0 ? $"−{-c.Delta}" : "±0";
        var from = c.IsExact ? $"{c.BaselineDate:M/d} の記録より" : $"記録を始めた {c.BaselineDate:M/d} より";
        return $"{sign} 本（{from}）";
    }
}
