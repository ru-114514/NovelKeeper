
namespace NovelKeeper.Models;

/// <summary>
/// 作品の「起動するもの」1つ分。
/// コンプリートボックスのように1作品に複数のゲームが入っている場合は、収録作ごとに登録する。
/// exe だけでなく、ショートカット・Steam の URL・バッチなど、Windows で開けるファイルなら何でも指定できる。
/// </summary>
public class LaunchTarget
{
    public long Id { get; set; }
    public long GameId { get; set; }
    /// <summary>表示名（空ならファイル名を表示）</summary>
    public string Label { get; set; } = "";
    /// <summary>起動するファイル（または steam://～ の URL）</summary>
    public string Path { get; set; } = "";
    /// <summary>監視するプロセス名（通常は空欄でOK）</summary>
    public string? MonitorProcess { get; set; }
    public int SortOrder { get; set; }

    /// <summary>今「使う」ものか（一覧の ▶ 印）</summary>
    public bool IsCurrent { get; set; }

    public bool IsUrl => Path.Contains("://");

    public string Display =>
        Label != "" ? Label
        : IsUrl ? (Path.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ? "Steam" : Path)
        : System.IO.Path.GetFileName(Path);

    public string CurrentMark => IsCurrent ? "▶" : "";
}
