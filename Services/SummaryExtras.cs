using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

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

    /// <summary>終了日が期間内の「プレイ済」の作品（終了日順）</summary>
    public static IReadOnlyList<ClearedItem> Cleared(IEnumerable<Game> games, DateTime from, DateTime to)
    {
        from = from.Date;
        to = to.Date;
        if (to < from) (from, to) = (to, from);
        return games
            .Where(g => g.Status == GameStatus.Played && g.ClearDate is DateTime d && d.Date >= from && d.Date <= to)
            .OrderBy(g => g.ClearDate).ThenBy(g => g.Title, StringComparer.CurrentCulture)
            .Select(g => new ClearedItem(g.Id, g.Title, g.ClearDate!.Value.Date, g.MyScore))
            .ToList();
    }

    /// <summary>積みゲー（未プレイで、発売前・購入前ではない作品）の本数</summary>
    public static int BacklogCount(IEnumerable<Game> games) =>
        games.Count(g => g.Status == GameStatus.Unplayed && g.Category == "");
}
