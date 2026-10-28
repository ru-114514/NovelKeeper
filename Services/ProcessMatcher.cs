using System.IO;
using NovelKeeper.Models;

namespace NovelKeeper.Services;

/// <summary>
/// 起動中のプロセスが、どの作品のプレイ中かを判定する（画面にもプロセス一覧にも触れないので、そのままテストできる）。
///  ① exe が「ゲームのフォルダの中」にあれば、その作品（いちばん深く一致したフォルダの作品）
///  ② だめなら、「監視するプロセス名」が一致する作品
/// 起動するものが複数ある作品（コンプリートボックスなど）は、そのすべてを判定に使う。
/// </summary>
public sealed class ProcessMatcher
{
    private sealed record Target(Game Game, string? Folder, string? ProcessName);

    private readonly List<Target> _targets = new();
    private readonly HashSet<string> _broadFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>判定の手がかりのある作品の数（0 なら、プロセスを調べる必要がない）</summary>
    public int Count => _targets.Count;

    /// <param name="broadFolders">フォルダ判定に使ってはいけない広い場所（ゲームを置く親フォルダ、Program Files など）</param>
    public ProcessMatcher(IEnumerable<Game> games, IEnumerable<string> broadFolders)
    {
        foreach (var folder in broadFolders)
            if (Normalize(folder) is string f) _broadFolders.Add(f.TrimEnd('\\'));

        foreach (var g in games) AddTargets(g);
    }

    /// <summary>広すぎてフォルダ判定に使えない場所（ゲームを置く親フォルダ、Program Files など）の一覧</summary>
    public static List<string> BroadFolders(IEnumerable<string> gameFolders) => new List<string>(gameFolders)
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
    };

    /// <summary>プロセスのプレイ中の作品（どの作品でもなければ null）</summary>
    public Game? Match(string processName, string? exePath)
    {
        // ① フォルダで判定（いちばん深く一致したフォルダの作品を選ぶ）
        if (exePath != null)
        {
            Target? best = null;
            foreach (var t in _targets)
            {
                if (t.Folder == null || !exePath.StartsWith(t.Folder, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || t.Folder.Length > best.Folder!.Length) best = t;
            }
            if (best != null) return best.Game;
        }

        // ② プロセス名で判定（「監視するプロセス名」を設定した作品など）
        return _targets.FirstOrDefault(t => t.ProcessName != null &&
                                            string.Equals(t.ProcessName, processName, StringComparison.OrdinalIgnoreCase))?.Game;
    }

    private void AddTargets(Game g)
    {
        // 判定に使う元：今使うもの（＋Steam のインストール先）と、登録してあるすべての起動するもの
        var sources = new List<(string? Path, string? Monitor)> { (g.ExePath, g.MonitorProcess) };
        sources.AddRange(g.Targets.Select(t => ((string?)t.Path, t.MonitorProcess)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? folder, string? proc)
        {
            if (folder != null && IsTooBroad(folder)) folder = null; // ドライブ直下や D:\Games 自体などは使わない
            if (folder == null && proc == null) return;
            var normalized = folder == null ? null : Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            if (!seen.Add((normalized ?? "") + "|" + (proc ?? ""))) return;
            _targets.Add(new Target(g, normalized, proc));
        }

        if (!string.IsNullOrWhiteSpace(g.InstallDir)) Add(g.InstallDir, null);

        foreach (var (path, monitor) in sources)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var isUrl = ShortcutResolver.IsUrl(path);
            var folder = isUrl ? null : Path.GetDirectoryName(path);
            var proc = string.IsNullOrWhiteSpace(monitor) ? null : Path.GetFileNameWithoutExtension(monitor.Trim());
            if (folder != null && IsTooBroad(folder) && proc == null && !isUrl)
                proc = Path.GetFileNameWithoutExtension(path); // フォルダで判定できないときは exe 名で
            Add(folder, proc);
        }
    }

    private bool IsTooBroad(string folder)
    {
        if (Normalize(folder) is not string f) return true;
        f = f.TrimEnd('\\');
        if (string.Equals(f, Path.GetPathRoot(f)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true; // ドライブ直下
        if (f.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase)) return true;
        return _broadFolders.Contains(f);
    }

    /// <summary>フルパスにする（空欄・不正なパスは null）</summary>
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
