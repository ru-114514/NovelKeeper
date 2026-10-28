using NovelKeeper.Models;

namespace NovelKeeper.Services;

/// <summary>「マイベストブランド」の選び方</summary>
public enum PostBrandMode
{
    /// <summary>期間内のプレイ時間が最も長いブランド</summary>
    MostPlayed = 0,
    /// <summary>期間内に遊んだ作品の平均得点が最も高いブランド（作品が一定数以上あるもの）</summary>
    HighestScore = 1,
    /// <summary>選んだブランド（そのブランドの期間内の時間・本数を出す）</summary>
    Specific = 2,
}

/// <summary>投稿用レポートの「作品」1件</summary>
public record PostWork(long GameId, string Title, int? EgsId, double Seconds);

/// <summary>マイベストブランドの結果</summary>
public record PostBrand(string Brand, double Seconds, int GameCount, double? AverageScore);

/// <summary>期間内にクリアした作品1件</summary>
public record PostCleared(long GameId, string Title, int? EgsId, int? Score, DateTime Date);

/// <summary>投稿用レポートの集計結果</summary>
public record PostReport(
    DateTime From, DateTime To,
    double TotalSeconds,
    int ClearCount, double? AverageScore,
    IReadOnlyList<PostWork> Works,
    PostBrand? Brand,
    int BacklogCount)
{
    public int DayCount { get; init; }
    /// <summary>遊んだ日の数</summary>
    public int PlayedDays { get; init; }
    /// <summary>期間内に遊んだ作品の数（再プレイの除外を反映する）</summary>
    public int PlayedGameCount { get; init; }
    public (DateTime Day, double Seconds)? BestDay { get; init; }
    public SessionRow? LongestSession { get; init; }
    public int? PeakHour { get; init; }
    /// <summary>直前の同じ長さの期間の総プレイ時間（比較用の記録を渡さなかったら null）</summary>
    public double? PreviousSeconds { get; init; }
    public IReadOnlyList<SummaryBucket> Buckets { get; init; } = [];
    public IReadOnlyDictionary<DateTime, double> DailySeconds { get; init; } = new Dictionary<DateTime, double>();
    /// <summary>期間内にクリアした作品（終了日の古い順）</summary>
    public IReadOnlyList<PostCleared> Cleared { get; init; } = [];
    /// <summary>ブランドごとの集計（プレイ時間の長い順）</summary>
    public IReadOnlyList<PostBrand> Brands { get; init; } = [];
    /// <summary>クリアして得点を付けた作品の点数の分布（59以下 / 60代 / 70代 / 80代 / 90以上）</summary>
    public int[] ScoreBins { get; init; } = new int[5];
    /// <summary>自分の点と批評空間の中央値の平均（両方ある作品だけ。ない場合は null）</summary>
    public (double Mine, double Median)? MedianCompare { get; init; }
}

/// <summary>
/// 投稿用レポートの集計（画面にも DB にも触れないので、そのままテストできる）。
/// プレイ時間は SummaryCalculator と同じ集計（日をまたぐ記録は日ごとに按分）を使う。
/// </summary>
public static class PostReportCalculator
{
    /// <summary>「最高点のブランド」にする最小の作品数の初期値</summary>
    public const int DefaultMinBrandGames = 2;

    /// <summary>起点の日から、1か月（または1年）の最後の日を返す（起点の日を含む。例：10/1 から 1か月 → 10/31）</summary>
    public static DateTime PeriodEnd(DateTime start, bool oneYear) => start.Date.AddMonths(oneYear ? 12 : 1).AddDays(-1);

    public static PostReport Build(
        IReadOnlyList<Game> games, IEnumerable<SessionRow> sessions, DateTime from, DateTime to,
        PostBrandMode brandMode = PostBrandMode.MostPlayed, string? specificBrand = null,
        int minBrandGames = DefaultMinBrandGames, bool excludeClearedBefore = false,
        IEnumerable<SessionRow>? previousSessions = null, IReadOnlySet<long>? replayGameIds = null,
        IReadOnlySet<long>? hiddenGameIds = null, IReadOnlySet<long>? backlogExcludedIds = null)
    {
        // 起点の日より前にクリアした作品（再プレイ）は、時間・日数・作品・ブランドなど、プレイ記録の集計すべてから外す
        // （手動で「再プレイとして含める」にした作品は残す）。クリア数・点数・積みゲーは、記録ではなく作品の状態から数える。
        // 「隠す」にした作品（クリア済みでなくても）も、同じプレイ記録の集計から外す
        var past = excludeClearedBefore ? PastClearedIds(games, from, replayGameIds) : new HashSet<long>();
        if (hiddenGameIds != null) past.UnionWith(hiddenGameIds);
        if (past.Count > 0)
        {
            sessions = sessions.Where(s => !past.Contains(s.GameId));
            previousSessions = previousSessions?.Where(s => !past.Contains(s.GameId));
        }
        var summary = SummaryCalculator.Build(sessions, from, to);
        var byId = games.ToDictionary(g => g.Id);

        // クリア数・点数：終了日が期間内の「プレイ済」の作品
        var cleared = games
            .Where(g => g.Status == GameStatus.Played && !g.Review.ExcludeFromClearCount
                        && g.ClearDate is DateTime d && d.Date >= summary.From && d.Date <= summary.To)
            .ToList();
        var scores = cleared.Where(g => g.MyScore != null).Select(g => (double)g.MyScore!.Value).ToList();

        // 作品：期間内に遊んだものを、期間内のプレイ時間が長い順に
        var works = summary.Games
            .Select(s => new PostWork(s.GameId, s.Title, byId.GetValueOrDefault(s.GameId)?.EgsId, s.Seconds))
            .ToList();

        var backlog = SummaryExtras.BacklogCount(games, backlogExcludedIds);

        var brands = BrandStats(summary.Games, byId);

        double? previous = null;
        if (previousSessions != null)
        {
            var (pf, pt) = SummaryCalculator.PreviousRange(summary.From, summary.To);
            previous = pf >= new DateTime(1900, 1, 1) ? SummaryCalculator.Build(previousSessions, pf, pt).TotalSeconds : 0;
        }

        var bins = new int[5];
        foreach (var s in scores) bins[s < 60 ? 0 : s < 70 ? 1 : s < 80 ? 2 : s < 90 ? 3 : 4]++;

        var both = cleared.Where(g => g.MyScore != null && g.Median != null).ToList();
        (double, double)? compare = both.Count == 0 ? null
            : (both.Average(g => (double)g.MyScore!.Value), both.Average(g => (double)g.Median!.Value));

        return new PostReport(summary.From, summary.To, summary.TotalSeconds,
            cleared.Count, scores.Count == 0 ? null : scores.Average(),
            works, BestBrand(brands, brandMode, specificBrand, minBrandGames), backlog)
        {
            DayCount = summary.DayCount,
            PlayedDays = summary.PlayedDays,
            PlayedGameCount = summary.Games.Count,
            BestDay = summary.BestDay,
            LongestSession = summary.LongestSession,
            PeakHour = summary.PeakHour,
            PreviousSeconds = previous,
            Buckets = summary.Buckets,
            DailySeconds = summary.DailySeconds,
            Cleared = cleared.OrderBy(g => g.ClearDate).ThenBy(g => g.Title, StringComparer.CurrentCulture)
                .Select(g => new PostCleared(g.Id, g.Title, g.EgsId, g.MyScore, g.ClearDate!.Value.Date)).ToList(),
            Brands = brands.OrderByDescending(b => b.Seconds).ThenByDescending(b => b.AverageScore ?? -1)
                .ThenBy(b => b.Brand, StringComparer.CurrentCulture).ToList(),
            ScoreBins = bins,
            MedianCompare = compare,
        };
    }

    /// <summary>プレイ済で、終了日が起点の日より前か、終了日が空欄の作品（過去にクリア済みとみなす）</summary>
    public static bool IsPastCleared(Game g, DateTime from) =>
        g.Status == GameStatus.Played && (g.ClearDate is not DateTime d || d.Date < from.Date);

    /// <summary>起点の日より前にクリア済みの作品（終了日が空欄のプレイ済も含む）。「再プレイとして含める」にした作品は入れない</summary>
    public static HashSet<long> PastClearedIds(IEnumerable<Game> games, DateTime from, IReadOnlySet<long>? replayGameIds = null) => games
        .Where(g => IsPastCleared(g, from) && (replayGameIds == null || !replayGameIds.Contains(g.Id)))
        .Select(g => g.Id).ToHashSet();

    /// <summary>「再プレイとして含める」の候補：起点の日より前にクリア済みで、期間内にプレイ記録がある作品（含めると決めた作品は、記録がなくても残す）</summary>
    public record ReplayCandidate(long GameId, string Title, double Seconds, bool Included);

    public static IReadOnlyList<ReplayCandidate> ReplayCandidates(
        IEnumerable<Game> games, IEnumerable<SessionRow> sessionsInPeriod, DateTime from, DateTime to, IReadOnlySet<long> replayGameIds)
    {
        var seconds = SummaryCalculator.Build(sessionsInPeriod, from, to).Games.ToDictionary(g => g.GameId, g => g.Seconds);
        return games
            .Where(g => replayGameIds.Contains(g.Id) || (IsPastCleared(g, from) && seconds.ContainsKey(g.Id)))
            .Select(g => new ReplayCandidate(g.Id, g.Title, seconds.GetValueOrDefault(g.Id), replayGameIds.Contains(g.Id)))
            .OrderByDescending(c => c.Seconds).ThenBy(c => c.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>「作品を隠す」の候補：期間内にプレイ記録があって、まだ集計に出る作品（隠すと決めた作品は、記録がなくても残す）。再プレイ除外で外れている作品は出さない</summary>
    public record HideCandidate(long GameId, string Title, double Seconds, bool Hidden);

    public static IReadOnlyList<HideCandidate> HideCandidates(
        IEnumerable<Game> games, IEnumerable<SessionRow> sessionsInPeriod, DateTime from, DateTime to,
        IReadOnlySet<long> hiddenGameIds, bool excludeClearedBefore, IReadOnlySet<long>? replayGameIds = null)
    {
        var seconds = SummaryCalculator.Build(sessionsInPeriod, from, to).Games.ToDictionary(g => g.GameId, g => g.Seconds);
        var past = excludeClearedBefore ? PastClearedIds(games, from, replayGameIds) : new HashSet<long>();
        return games
            .Where(g => hiddenGameIds.Contains(g.Id) || (seconds.ContainsKey(g.Id) && !past.Contains(g.Id)))
            .Select(g => new HideCandidate(g.Id, g.Title, seconds.GetValueOrDefault(g.Id), hiddenGameIds.Contains(g.Id)))
            .OrderByDescending(c => c.Seconds).ThenBy(c => c.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private static List<PostBrand> BrandStats(IReadOnlyList<GameSummary> played, Dictionary<long, Game> byId) => played
        .Select(p => (Game: byId.GetValueOrDefault(p.GameId), p.Seconds))
        .Where(x => x.Game != null && !string.IsNullOrWhiteSpace(x.Game.Brand))
        .GroupBy(x => x.Game!.Brand!.Trim())
        .Select(g =>
        {
            var sc = g.Where(x => x.Game!.MyScore != null).Select(x => (double)x.Game!.MyScore!.Value).ToList();
            return new PostBrand(g.Key, g.Sum(x => x.Seconds), g.Count(), sc.Count == 0 ? null : sc.Average());
        })
        .ToList();

    private static PostBrand? BestBrand(List<PostBrand> brands, PostBrandMode mode, string? specific, int minGames)
    {
        switch (mode)
        {
            case PostBrandMode.Specific:
                if (string.IsNullOrWhiteSpace(specific)) return null;
                return brands.FirstOrDefault(b => string.Equals(b.Brand, specific.Trim(), StringComparison.OrdinalIgnoreCase))
                       ?? new PostBrand(specific.Trim(), 0, 0, null);
            case PostBrandMode.HighestScore:
                // 同率なら、プレイ時間が長い方
                return brands.Where(b => b.AverageScore != null && b.GameCount >= minGames)
                    .OrderByDescending(b => b.AverageScore).ThenByDescending(b => b.Seconds)
                    .ThenBy(b => b.Brand, StringComparer.CurrentCulture).FirstOrDefault();
            default:
                // 同率なら、平均得点が高い方
                return brands.OrderByDescending(b => b.Seconds).ThenByDescending(b => b.AverageScore ?? -1)
                    .ThenBy(b => b.Brand, StringComparer.CurrentCulture).FirstOrDefault();
        }
    }
}
