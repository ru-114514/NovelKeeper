using NovelKeeper.Models;

namespace NovelKeeper.Services;

/// <summary>点数×プレイ時間の散布図の点1つ分</summary>
public record ScorePoint(long GameId, string Title, double Seconds, int Score);

/// <summary>期間内にクリアした作品1つ分</summary>
public record ClearedItem(long GameId, string Title, DateTime Date, int? Score);

/// <summary>レポートの追加欄（時間帯ヒートマップ・点数×プレイ時間・クリアと積みゲー）の集計。画面に触れないのでそのままテストできる</summary>
public static class SummaryExtras
{
    /// <summary>曜日×時間帯のプレイ時間（秒）。[曜日, 時] で、曜日は 0=月 … 6=日</summary>
    public static double[,] Heatmap(IEnumerable<SessionRow> rows, DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        var end = to.AddDays(1);
        var cells = new double[7, 24];
        foreach (var row in rows)
            foreach (var (hour, seconds) in SummaryCalculator.Split(row))
            {
                if (hour < from || hour >= end) continue;
                cells[((int)hour.DayOfWeek + 6) % 7, hour.Hour] += seconds;
            }
        return cells;
    }

    /// <summary>期間内に遊んだ作品のうち、得点を付けているものの（期間内のプレイ時間, 得点）</summary>
    public static IReadOnlyList<ScorePoint> ScorePoints(IEnumerable<GameSummary> played, IEnumerable<Game> games)
    {
        var byId = games.ToDictionary(g => g.Id);
        return played
            .Where(p => byId.TryGetValue(p.GameId, out var g) && g.MyScore != null)
            .Select(p => new ScorePoint(p.GameId, p.Title, p.Seconds, byId[p.GameId].MyScore!.Value))
            .ToList();
    }

    /// <summary>終了日が期間内の「プレイ済」の作品（終了日順）。「クリア数の集計から除外」にした作品は含めない</summary>
    public static IReadOnlyList<ClearedItem> Cleared(IEnumerable<Game> games, DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        return games
            .Where(g => g.Status == GameStatus.Played && !g.Review.ExcludeFromClearCount
                        && g.ClearDate is DateTime d && d.Date >= from && d.Date <= to)
            .OrderBy(g => g.ClearDate).ThenBy(g => g.Title, StringComparer.CurrentCulture)
            .Select(g => new ClearedItem(g.Id, g.Title, g.ClearDate!.Value.Date, g.MyScore))
            .ToList();
    }

    /// <summary>積みゲー（未プレイで、発売前・購入前ではない作品）の本数</summary>
    public static int BacklogCount(IEnumerable<Game> games, IReadOnlySet<long>? excluded = null) =>
        games.Count(g => g.Status == GameStatus.Unplayed && g.Category == "" && (excluded == null || !excluded.Contains(g.Id)));

    // ---- 連続プレイ日数 ----

    /// <summary>「連続」に数える1日の最低プレイ時間（秒）。同じ日の合計が10分以上の日だけが、連続の1日になる</summary>
    public const double StreakMinSeconds = 600;

    /// <summary>連続した日数と、その最初・最後の日（0日のときは日付なし）</summary>
    public record Streak(int Days, DateTime? Start, DateTime? End);

    /// <summary>期間内で最も長い連続プレイ日数（同じ長さが複数あるときは、新しい方）</summary>
    public static Streak LongestStreak(IReadOnlyDictionary<DateTime, double> daily, DateTime from, DateTime to,
        double minSeconds = StreakMinSeconds)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        var best = new Streak(0, null, null);
        DateTime? runStart = null;
        var run = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (daily.GetValueOrDefault(d) >= minSeconds)
            {
                runStart ??= d;
                run++;
                if (run >= best.Days) best = new Streak(run, runStart, d);
            }
            else
            {
                runStart = null;
                run = 0;
            }
        }
        return best;
    }

    /// <summary>いまの連続日数。今日がまだ10分に届いていないときは、昨日までの連続を数える（今日これから遊べば続くため）</summary>
    public static int CurrentStreak(IReadOnlyDictionary<DateTime, double> daily, DateTime today, double minSeconds = StreakMinSeconds)
    {
        var d = today.Date;
        if (daily.GetValueOrDefault(d) < minSeconds) d = d.AddDays(-1);
        var n = 0;
        while (daily.GetValueOrDefault(d) >= minSeconds)
        {
            n++;
            d = d.AddDays(-1);
        }
        return n;
    }

    // ---- 年間・月間のまとめと目標 ----

    /// <summary>ある期間（年・月）のまとめ</summary>
    public record Recap(DateTime From, DateTime To, double Seconds, int PlayedDays, int Clears, int Games, Streak Longest);

    public static Recap BuildRecap(IEnumerable<SessionRow> rows, IReadOnlyList<Game> games, DateTime from, DateTime to)
    {
        var r = SummaryCalculator.Build(rows, from, to);
        return new Recap(r.From, r.To, r.TotalSeconds, r.PlayedDays, Cleared(games, r.From, r.To).Count, r.Games.Count,
            LongestStreak(r.DailySeconds, r.From, r.To));
    }

    /// <summary>月ごとの合計（古い月から。count か月分）</summary>
    public static IReadOnlyList<(DateTime Month, double Seconds, int Clears)> MonthlyTotals(
        IEnumerable<SessionRow> rows, IReadOnlyList<Game> games, DateTime lastMonth, int count)
    {
        var last = new DateTime(lastMonth.Year, lastMonth.Month, 1);
        var first = last.AddMonths(-(count - 1));
        var daily = SummaryCalculator.Build(rows, first, last.AddMonths(1).AddDays(-1)).DailySeconds;
        var list = new List<(DateTime, double, int)>();
        for (var m = first; m <= last; m = m.AddMonths(1))
        {
            var end = m.AddMonths(1).AddDays(-1);
            var sec = 0d;
            for (var d = m; d <= end; d = d.AddDays(1)) sec += daily.GetValueOrDefault(d);
            list.Add((m, sec, Cleared(games, m, end).Count));
        }
        return list;
    }

    public const int MaxGoalHours = 9999, MaxGoalClears = 999;

    /// <summary>目標に対する達成率（%）。目標が未設定（0以下）なら null</summary>
    public static int? GoalPercent(double actual, double goal) => goal <= 0 ? null : (int)Math.Round(actual / goal * 100);

    /// <summary>目標の入力欄の文字を数に直す（空欄は 0 = 未設定）。数でない・範囲外・小数は null</summary>
    public static int? ParseGoal(string? text, int max)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return 0;
        return int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= max ? n : null;
    }
}
