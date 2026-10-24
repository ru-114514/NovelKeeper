using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>年別の集計1行分。AvgScore は、その年にクリアした作品のうち得点がある作品の平均（なければ null）</summary>
public record YearTrend(int Year, double Seconds, int Games, int Clears, double? AvgScore, int Scored);

/// <summary>ブランド別・ジャンル別の集計1行分。AvgScore は、得点がある作品の平均（なければ null）</summary>
public record GroupTrend(string Name, int Games, int Clears, double Seconds, double? AvgScore, int Scored);

/// <summary>得点の帯（90〜100 など）ごとの作品数</summary>
public record ScoreBand(string Label, int Count);

/// <summary>
/// レポートの「傾向」欄の集計（年別の推移・ブランド別・得点の分布・ジャンル別）。
/// 画面に触れないので、そのままテストできる。
/// 「遊んだ作品」は、プレイ中・プレイ済・中断、またはプレイ時間の記録がある作品。
/// </summary>
public static class TrendStats
{
    public static bool IsPlayed(Game g) =>
        g.Status is GameStatus.Playing or GameStatus.Played or GameStatus.Dropped || g.TotalSeconds > 0;

    private static bool CountsAsClear(Game g) => g.Status == GameStatus.Played && !g.Review.ExcludeFromClearCount;

    /// <summary>年ごとの推移（新しい年から）。プレイ時間・遊んだ作品数はセッションの開始日の年、クリア数と平均点はクリア日の年で数える</summary>
    public static IReadOnlyList<YearTrend> ByYear(IEnumerable<SessionRow> rows, IEnumerable<Game> games)
    {
        var sessions = rows.ToList();
        var cleared = games.Where(g => CountsAsClear(g) && g.ClearDate != null).ToList();
        var years = sessions.Select(s => s.Start.Year).Concat(cleared.Select(g => g.ClearDate!.Value.Year)).Distinct();
        return years.OrderByDescending(y => y).Select(y =>
        {
            var inYear = sessions.Where(s => s.Start.Year == y).ToList();
            var clears = cleared.Where(g => g.ClearDate!.Value.Year == y).ToList();
            var scores = clears.Where(g => g.MyScore != null).Select(g => g.MyScore!.Value).ToList();
            return new YearTrend(y, inYear.Sum(s => (double)s.Seconds), inYear.Select(s => s.GameId).Distinct().Count(),
                clears.Count, scores.Count == 0 ? null : scores.Average(), scores.Count);
        }).ToList();
    }

    /// <summary>ブランド別（遊んだ作品が多い順、同数なら総プレイ時間が長い順）。ブランドが分からない作品は数えない</summary>
    public static IReadOnlyList<GroupTrend> ByBrand(IEnumerable<Game> games, int top = 10) =>
        Group(games.Where(g => IsPlayed(g) && !string.IsNullOrWhiteSpace(g.Brand)), g => new[] { g.Brand!.Trim() }, top);

    /// <summary>ジャンル別。traits に特徴を取得した作品だけが対象（作品に複数のジャンルがあれば、それぞれで数える）</summary>
    public static IReadOnlyList<GroupTrend> ByGenre(IEnumerable<Game> games, IReadOnlyDictionary<int, GameTraits> traits, int top = 10) =>
        Group(games.Where(g => IsPlayed(g) && g.EgsId != null && traits.ContainsKey(g.EgsId.Value)),
            g => SplitGenres(traits[g.EgsId!.Value].Genre), top);

    /// <summary>ジャンルの文字を分ける（「/」「、」「,」区切り）。空なら空の一覧</summary>
    public static IReadOnlyList<string> SplitGenres(string? genre) =>
        (genre ?? "").Split(new[] { '/', '／', '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct().ToList();

    /// <summary>ジャンルを取得済みの、遊んだ作品の数と、遊んだ作品の総数（取得の進み具合の表示用）</summary>
    public static (int Fetched, int Total) GenreCoverage(IEnumerable<Game> games, IReadOnlyDictionary<int, GameTraits> traits)
    {
        var played = games.Where(g => IsPlayed(g) && g.EgsId != null).ToList();
        return (played.Count(g => traits.ContainsKey(g.EgsId!.Value)), played.Count);
    }

    /// <summary>ジャンルの取得が必要な作品の批評空間 ID（遊んだ作品で、まだ取得していないもの）</summary>
    public static List<int> GenreNeedFetch(IEnumerable<Game> games, IReadOnlyDictionary<int, GameTraits> traits) =>
        games.Where(g => IsPlayed(g) && g.EgsId != null && !traits.ContainsKey(g.EgsId.Value))
            .Select(g => g.EgsId!.Value).Distinct().ToList();

    private static IReadOnlyList<GroupTrend> Group(IEnumerable<Game> games, Func<Game, IEnumerable<string>> keys, int top) =>
        games.SelectMany(g => keys(g).Select(k => (Key: k, Game: g)))
            .GroupBy(x => x.Key)
            .Select(grp =>
            {
                var list = grp.Select(x => x.Game).ToList();
                var scores = list.Where(g => g.MyScore != null).Select(g => g.MyScore!.Value).ToList();
                return new GroupTrend(grp.Key, list.Count, list.Count(CountsAsClear), list.Sum(g => (double)g.TotalSeconds),
                    scores.Count == 0 ? null : scores.Average(), scores.Count);
            })
            .OrderByDescending(r => r.Games).ThenByDescending(r => r.Seconds).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .Take(top)
            .ToList();

    /// <summary>遊んだ作品のうち得点がある作品を、得点の帯ごとに数える（高い帯から）</summary>
    public static IReadOnlyList<ScoreBand> ScoreBands(IEnumerable<Game> games)
    {
        var scores = games.Where(g => IsPlayed(g) && g.MyScore != null).Select(g => g.MyScore!.Value).ToList();
        return new[]
        {
            new ScoreBand("90〜100", scores.Count(s => s >= 90)),
            new ScoreBand("80〜89", scores.Count(s => s is >= 80 and < 90)),
            new ScoreBand("70〜79", scores.Count(s => s is >= 70 and < 80)),
            new ScoreBand("60〜69", scores.Count(s => s is >= 60 and < 70)),
            new ScoreBand("50〜59", scores.Count(s => s is >= 50 and < 60)),
            new ScoreBand("〜49", scores.Count(s => s < 50)),
        };
    }
}
