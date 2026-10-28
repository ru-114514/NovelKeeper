namespace NovelKeeper.Services;

/// <summary>プレイ記録1件（レポートの集計元）</summary>
public record SessionRow(long GameId, string Title, DateTime Start, DateTime End, long Seconds);

/// <summary>作品ごとの合計</summary>
public record GameSummary(long GameId, string Title, double Seconds);

/// <summary>グラフの棒1本分</summary>
public record SummaryBucket(string Label, string Detail, double Seconds);

/// <summary>期間の集計結果</summary>
public record SummaryResult(
    DateTime From, DateTime To,
    double TotalSeconds, int PlayedDays,
    IReadOnlyList<SummaryBucket> Buckets, bool IsHourly,
    IReadOnlyList<GameSummary> Games,
    IReadOnlyDictionary<DateTime, double> DailySeconds,
    (DateTime Day, double Seconds)? BestDay,   // 最も遊んだ日
    SessionRow? LongestSession,                // 最長の1回
    int? PeakHour)                             // よく遊ぶ時間帯（0〜23）
{
    public int DayCount => (To - From).Days + 1;
}

/// <summary>
/// 期間のプレイ状況の集計（画面にも DB にも触れないので、そのままテストできる）。
///  - 1日は 0:00〜23:59（暦の日）。期間は開始日・終了日とも含む
///  - 日をまたぐセッションは、時刻の幅に比例して各日・各時間帯に振り分ける
///    （放置などで除外した時間があるので、「数えた秒数」を時刻の幅で按分する）
/// </summary>
public static class SummaryCalculator
{
    /// <summary>1日だけなら時間帯別、これ以下の日数なら日別、さらに長ければ週別→月別にする</summary>
    public const int MaxDailyBars = 62;
    public const int MaxWeeklyBars = 400;

    /// <param name="from">期間の最初の日（時刻は無視）</param>
    /// <param name="to">期間の最後の日（その日の 23:59 まで含む）</param>
    public static SummaryResult Build(IEnumerable<SessionRow> rows, DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        var end = to.AddDays(1); // 終了日の翌日 0:00（含まない）

        var hours = new Dictionary<DateTime, double>();           // 時間帯の頭 → 秒
        var games = new Dictionary<long, (string Title, double Seconds)>();

        var hourOfDay = new double[24];
        SessionRow? longest = null;

        foreach (var row in rows)
        {
            var inRange = false;
            foreach (var (hour, seconds) in Split(row))
            {
                if (hour < from || hour >= end) continue;
                inRange = true;
                hours[hour] = hours.GetValueOrDefault(hour) + seconds;
                hourOfDay[hour.Hour] += seconds;
                var g = games.GetValueOrDefault(row.GameId, (row.Title, 0d));
                games[row.GameId] = (row.Title, g.Item2 + seconds);
            }
            if (inRange && (longest == null || row.Seconds > longest.Seconds)) longest = row;
        }

        var days = hours.GroupBy(h => h.Key.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var dayCount = (to - from).Days + 1;
        var ranking = games
            .Select(g => new GameSummary(g.Key, g.Value.Title, g.Value.Seconds))
            .Where(g => g.Seconds >= 0.5)
            .OrderByDescending(g => g.Seconds)
            .ThenBy(g => g.Title, StringComparer.CurrentCulture)
            .ToList();

        List<SummaryBucket> buckets;
        var hourly = dayCount == 1;
        if (hourly)
        {
            buckets = Enumerable.Range(0, 24)
                .Select(h => new SummaryBucket(h.ToString(), $"{h}:00〜{h}:59", hours.GetValueOrDefault(from.AddHours(h))))
                .ToList();
        }
        else if (dayCount <= MaxDailyBars)
        {
            buckets = Enumerable.Range(0, dayCount)
                .Select(i => from.AddDays(i))
                .Select(d => new SummaryBucket(
                    $"{d.Month}/{d.Day}", $"{d:yyyy/MM/dd}（{Weekday(d)}）", days.GetValueOrDefault(d)))
                .ToList();
        }
        else if (dayCount <= MaxWeeklyBars)
        {
            buckets = GroupBuckets(days, from, to, WeekStart, d => $"{d.Month}/{d.Day}",
                (s, e) => $"{s:yyyy/MM/dd}〜{e:yyyy/MM/dd}の週");
        }
        else
        {
            buckets = GroupBuckets(days, from, to, d => new DateTime(d.Year, d.Month, 1),
                d => $"{d.Year % 100}/{d.Month}", (s, _) => $"{s:yyyy年M月}");
        }

        (DateTime, double)? bestDay = days.Count == 0 ? null : days.OrderByDescending(d => d.Value).Select(d => (d.Key, d.Value)).First();
        if (bestDay is { Item2: < 1 }) bestDay = null;
        var peak = hourOfDay.Max();
        int? peakHour = peak < 1 ? null : Array.IndexOf(hourOfDay, peak);

        return new SummaryResult(from, to, days.Values.Sum(), days.Count(d => d.Value >= 1), buckets, hourly, ranking,
            days, bestDay, longest, peakHour);
    }

    /// <summary>直前の同じ長さの期間（比較用）。例：10/20〜10/22 なら 10/17〜10/19</summary>
    public static (DateTime From, DateTime To) PreviousRange(DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        var n = (to - from).Days + 1;
        return (from.AddDays(-n), from.AddDays(-1));
    }

    /// <summary>前の期間との差の表示（例：+2時間30分（+25%）、-15分（-10%））</summary>
    public static string FormatDelta(double current, double previous)
    {
        var diff = current - previous;
        if (Math.Round(Math.Abs(diff)) < 1) return "変化なし";
        var sign = diff > 0 ? "+" : "-";
        var text = sign + FormatDuration(Math.Abs(diff));
        return previous >= 1 ? $"{text}（{sign}{Math.Abs(diff) / previous * 100:0}%）" : text;
    }

    /// <summary>週の始まり（月曜）</summary>
    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static string Weekday(DateTime d) => "日月火水木金土"[(int)d.DayOfWeek].ToString();

    private static List<SummaryBucket> GroupBuckets(
        Dictionary<DateTime, double> days, DateTime from, DateTime to,
        Func<DateTime, DateTime> keyOf, Func<DateTime, string> label, Func<DateTime, DateTime, string> detail)
    {
        var sums = new SortedDictionary<DateTime, double>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var k = keyOf(d);
            sums[k] = sums.GetValueOrDefault(k) + days.GetValueOrDefault(d);
        }
        return sums.Select(kv =>
        {
            var s = kv.Key < from ? from : kv.Key; // 期間の外にはみ出す週・月は、期間内だけを示す
            return new SummaryBucket(label(kv.Key), detail(s, EndOfBucket(kv.Key, keyOf, to)), kv.Value);
        }).ToList();
    }

    private static DateTime EndOfBucket(DateTime key, Func<DateTime, DateTime> keyOf, DateTime to)
    {
        var e = key;
        while (e < to && keyOf(e.AddDays(1)) == key) e = e.AddDays(1);
        return e > to ? to : e;
    }

    /// <summary>1件のセッションを、時刻の幅に比例して1時間ごとに振り分ける</summary>
    public static IEnumerable<(DateTime Hour, double Seconds)> Split(SessionRow row)
    {
        var start = row.Start;
        var span = (row.End - row.Start).TotalSeconds;
        var hourOf = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
        if (row.Seconds <= 0) yield break;
        if (span <= 0)
        {
            yield return (hourOf, row.Seconds); // 幅がない（開始と終了が同時）ときは開始の時間帯に入れる
            yield break;
        }
        for (var h = hourOf; h < row.End; h = h.AddHours(1))
        {
            var overlapStart = h > start ? h : start;
            var next = h.AddHours(1);
            var overlapEnd = next < row.End ? next : row.End;
            var overlap = (overlapEnd - overlapStart).TotalSeconds;
            if (overlap > 0) yield return (h, row.Seconds * overlap / span);
        }
    }

    /// <summary>縦軸の目盛りの間隔（秒）。目盛りが5本以内になる最小のきりのよい値（全期間の月別など、時間が大きいときも線が増えすぎない）</summary>
    public static double AxisStep(double maxSeconds)
    {
        double[] steps = { 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400 };
        foreach (var s in steps)
            if (maxSeconds / s <= 5) return s;
        var step = 86400d;
        foreach (var k in new[] { 2, 5, 10, 20, 50, 100, 200, 500, 1000 })
        {
            step = 86400d * k;
            if (maxSeconds / step <= 5) break;
        }
        return step;
    }

    /// <summary>時間の表示（例：2時間05分、7分、30秒）</summary>
    public static string FormatDuration(double seconds)
    {
        var s = (long)Math.Round(seconds);
        if (s >= 3600) return $"{s / 3600}時間{s % 3600 / 60:00}分";
        if (s >= 60) return $"{s / 60}分";
        return s > 0 ? $"{s}秒" : "0分";
    }
}
