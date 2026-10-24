using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>放置作品の見直しの1件。基準の日（Playing は最後に遊んだ日、Unplayed は発売日）から Days 日たっている</summary>
public record StaleItem(Game Game, DateTime? Since, int? Days);

/// <summary>
/// 放置作品の見直し：長く触っていない「プレイ中」と、発売からだいぶたっても手つかずの「未プレイ」の候補を出す。
/// 画面にも DB にも触れないので、そのままテストできる
/// </summary>
public static class StaleReview
{
    public const int DefaultPlayingDays = 90;
    public const int DefaultUnplayedYears = 1;

    /// <summary>
    /// 候補。プレイ中は「最後に遊んだ日（なければ初めて起動した日）」から playingDays 日以上、
    /// 未プレイ（積みゲーに数える作品）は発売日から unplayedYears 年以上たったもの。
    /// 並びは、プレイ中（古い順）→ 未プレイ（発売の古い順）。skipIds（保留した作品）は出さない
    /// </summary>
    public static List<StaleItem> Candidates(IEnumerable<Game> games, IReadOnlyDictionary<long, DateTime> lastPlayed, DateTime today,
        int playingDays = DefaultPlayingDays, int unplayedYears = DefaultUnplayedYears,
        IReadOnlySet<long>? backlogExcluded = null, IReadOnlySet<long>? skipIds = null)
    {
        today = today.Date;
        var playing = new List<StaleItem>();
        var unplayed = new List<StaleItem>();
        foreach (var g in games)
        {
            if (skipIds != null && skipIds.Contains(g.Id)) continue;
            if (g.Status == GameStatus.Playing)
            {
                DateTime? since = lastPlayed.TryGetValue(g.Id, out var lp) ? lp.Date : g.FirstPlayed?.Date;
                if (since == null) playing.Add(new StaleItem(g, null, null)); // 記録がない：状態だけ「プレイ中」
                else if ((today - since.Value).TotalDays >= playingDays) playing.Add(new StaleItem(g, since, (int)(today - since.Value).TotalDays));
            }
            else if (g.Status == GameStatus.Unplayed && g.Category == "" && (backlogExcluded == null || !backlogExcluded.Contains(g.Id))
                     && SellDate(g) is DateTime sell && sell <= today.AddYears(-unplayedYears))
            {
                unplayed.Add(new StaleItem(g, sell, (int)(today - sell).TotalDays));
            }
        }
        return playing.OrderByDescending(i => i.Days ?? int.MaxValue).ThenBy(i => i.Game.Title, StringComparer.CurrentCulture)
            .Concat(unplayed.OrderBy(i => i.Since).ThenBy(i => i.Game.Title, StringComparer.CurrentCulture))
            .ToList();
    }

    private static DateTime? SellDate(Game g) =>
        g.SellDay is { Length: >= 10 } s &&
        DateTime.TryParse(s[..10], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.Date : null;
}
