using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ErogeLauncher.Services;

/// <summary>起動する exe の候補1つ分</summary>
public class ExeCandidate
{
    public string Path { get; init; } = "";
    /// <summary>この候補を選んだ理由（ショートカット、Unity など）</summary>
    public string Reason { get; init; } = "";
    public string Display =>
        System.IO.Path.GetFileName(Path) + (Reason == "" ? "" : $"（{Reason}）");
}

/// <summary>PC内で見つかったゲーム1本分</summary>
public class DetectedGame
{
    public string Source { get; init; } = "";        // 「フォルダ」「Steam」「ショートカット」
    public string DisplayName { get; init; } = "";   // 画面に出す名前
    public string InstallDir { get; init; } = "";
    public List<string> Names { get; init; } = new(); // 照合に使う名前の候補

    /// <summary>Steam のゲームなら steam://rungameid/xxx（起動は Steam 経由）</summary>
    public string? SteamUrl { get; init; }
    public bool IsSteam => SteamUrl != null;

    /// <summary>
    /// exe の候補（よさそうな順）。
    /// 通常のゲームは「起動する exe」、Steam のゲームは「監視する exe」として使う。
    /// </summary>
    public List<ExeCandidate> Candidates { get; init; } = new();

    public string LaunchText => IsSteam ? "Steam経由" : "exeを直接起動";

    /// <summary>起動する exe を1つに特定できたか</summary>
    public bool IsExeCertain =>
        Candidates.Count == 1 ||
        (Candidates.Count > 1 && Candidates[0].Reason != "" && Candidates.Skip(1).All(c => c.Reason == ""));

    public string CertainText =>
        IsExeCertain ? "✓ 特定" : Candidates.Count == 0 ? "exeなし" : $"要確認（候補{Candidates.Count}）";
}

/// <summary>
/// インストール済みのゲームを探す。
///  1. 指定フォルダ（例：D:\novelgame）の中のゲームフォルダ
///  2. Steam のライブラリにインストールされたゲーム
///  3. 指定フォルダ（デスクトップなど）とスタートメニューのショートカット
/// どの exe で起動するかは「ショートカット ＞ ゲームエンジンの特徴 ＞ 製品名・大きさ」の順で決める。
/// </summary>
public static class GameScanner
{
    // 起動用ではない exe（アンインストーラー、設定ツールなど）を除外する
    private static readonly Regex ExcludeExe = new(
        "unins|uninst|setup|install|config|cfg|setting|update|patch|crash|report|redist|" +
        "vcredist|dxsetup|directx|dotnet|notification_helper|7z|readme|manual|benchmark|" +
        "editor|server|dxwebsetup|oalinst|physx",
        RegexOptions.IgnoreCase);

    private const int MaxCandidates = 8;

    /// <summary>ショートカット1つ分（同じリンク先のものはまとめる）</summary>
    private class ShortcutInfo
    {
        public string Target { get; init; } = "";
        public List<string> Names { get; } = new();
        public bool IsSteam => Target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase);
        public string? SteamAppId => Regex.Match(Target, @"rungameid/(\d+)") is { Success: true } m ? m.Groups[1].Value : null;
    }

    // ================================================================
    // まとめて検出する
    // ================================================================

    public static List<DetectedGame> ScanAll(
        IEnumerable<string> gameFolders, bool scanSteam,
        IEnumerable<string> shortcutFolders, bool scanStartMenu)
    {
        var shortcuts = ScanShortcuts(shortcutFolders, scanStartMenu);
        var used = new HashSet<ShortcutInfo>();
        var result = new List<DetectedGame>();
        var roots = gameFolders.Where(Directory.Exists).ToList();

        // ---- 1. ゲームフォルダ ----
        foreach (var root in roots)
        {
            var gameDirs = new List<string>();
            CollectGameDirs(root, 0, gameDirs);

            foreach (var dir in gameDirs)
            {
                // このフォルダの中を指しているショートカット
                var sc = shortcuts.Where(s => !s.IsSteam && IsUnder(s.Target, dir)).ToList();
                used.UnionWith(sc);

                var candidates = RankExes(dir, sc.Select(s => s.Target), maxDepth: 2);
                if (candidates.Count == 0) continue;

                var names = new List<string>();
                names.AddRange(sc.SelectMany(s => s.Names));            // ショートカット名（日本語のタイトルが多い）
                names.Add(Path.GetFileName(dir));
                var parent = Path.GetDirectoryName(dir);
                if (parent != null && !SamePath(parent, root)) names.Add(Path.GetFileName(parent));
                var info = ReadVersionInfo(candidates[0].Path);
                names.AddRange(info);
                names.Add(Path.GetFileNameWithoutExtension(candidates[0].Path));

                result.Add(new DetectedGame
                {
                    Source = sc.Count > 0 ? "フォルダ＋ショートカット" : "フォルダ",
                    DisplayName = sc.SelectMany(s => s.Names).FirstOrDefault() ?? info.FirstOrDefault() ?? Path.GetFileName(dir),
                    InstallDir = dir,
                    Names = Clean(names),
                    Candidates = candidates,
                });
            }
        }

        // ---- 2. Steam ----
        var steamApps = scanSteam ? ScanSteam() : new List<DetectedGame>();
        foreach (var app in steamApps)
        {
            var appId = Regex.Match(app.SteamUrl!, @"rungameid/(\d+)").Groups[1].Value;
            var sc = shortcuts.Where(s => s.SteamAppId == appId).ToList();
            used.UnionWith(sc);
            app.Names.InsertRange(0, sc.SelectMany(s => s.Names)); // デスクトップのショートカット名も照合に使う
            result.Add(app);
        }

        // ---- 3. 残りのショートカット（D:\novelgame 以外にあるゲームなど） ----
        foreach (var s in shortcuts.Where(x => !used.Contains(x)))
        {
            if (s.IsSteam)
            {
                // ライブラリから見つからなかった Steam のゲーム（監視する exe は不明）
                if (s.SteamAppId == null) continue;
                result.Add(new DetectedGame
                {
                    Source = "ショートカット",
                    DisplayName = s.Names[0],
                    SteamUrl = s.Target,
                    Names = Clean(s.Names),
                });
                continue;
            }

            if (!File.Exists(s.Target) || !s.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (ExcludeExe.IsMatch(Path.GetFileNameWithoutExtension(s.Target))) continue;

            var dir = Path.GetDirectoryName(s.Target)!;
            var names = new List<string>(s.Names);
            names.AddRange(ReadVersionInfo(s.Target));
            names.Add(Path.GetFileName(dir));

            result.Add(new DetectedGame
            {
                Source = "ショートカット",
                DisplayName = s.Names[0],
                InstallDir = dir,
                Names = Clean(names),
                Candidates = RankExes(dir, new[] { s.Target }, maxDepth: 1),
            });
        }

        return result;
    }

    /// <summary>
    /// exe を直接含むフォルダを「ゲームのフォルダ」とみなして集める。
    /// D:\novelgame\ゲーム名\ と D:\novelgame\ブランド名\ゲーム名\ の両方に対応する。
    /// </summary>
    private static void CollectGameDirs(string dir, int level, List<string> found)
    {
        if (level > 0 && GetExes(dir).Any())
        {
            found.Add(dir);
            return;
        }
        if (level >= 3) return;
        foreach (var sub in SafeDirs(dir))
            CollectGameDirs(sub, level + 1, found);
    }

    // ================================================================
    // ショートカット
    // ================================================================

    private static List<ShortcutInfo> ScanShortcuts(IEnumerable<string> folders, bool scanStartMenu)
    {
        var files = new List<string>();

        // 指定フォルダは、そのフォルダの直下だけ（サブフォルダは個別に指定してもらう）
        foreach (var f in folders.Where(Directory.Exists))
        {
            files.AddRange(SafeFiles(f, "*.lnk"));
            files.AddRange(SafeFiles(f, "*.url"));
        }

        // スタートメニューはサブフォルダも含めて探す
        if (scanStartMenu)
        {
            foreach (var sm in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                     }.Where(Directory.Exists))
            {
                try { files.AddRange(Directory.GetFiles(sm, "*.lnk", SearchOption.AllDirectories)); } catch { }
            }
        }

        var byTarget = new Dictionary<string, ShortcutInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var target = ShortcutResolver.Resolve(file);
            if (string.IsNullOrWhiteSpace(target) || SamePathSafe(target, file)) continue;
            if (ShortcutResolver.IsUrl(target) && !target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) continue;

            if (!byTarget.TryGetValue(target, out var info))
                byTarget[target] = info = new ShortcutInfo { Target = target };
            info.Names.Add(Path.GetFileNameWithoutExtension(file));
        }
        return byTarget.Values.ToList();
    }

    // ================================================================
    // Steam
    // ================================================================

    /// <param name="onlyAppId">指定すると、そのゲームだけを調べる（全ゲームを調べ直さずに済む）</param>
    public static List<DetectedGame> ScanSteam(string? onlyAppId = null)
    {
        var result = new List<DetectedGame>();
        foreach (var library in GetSteamLibraries())
        {
            var steamapps = Path.Combine(library, "steamapps");
            foreach (var acf in SafeFiles(steamapps, $"appmanifest_{onlyAppId ?? "*"}.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf, Encoding.UTF8); } catch { continue; }

                var appId = GetAcfValue(text, "appid");
                var name = GetAcfValue(text, "name");
                var installDir = GetAcfValue(text, "installdir");
                if (appId == null || name == null || installDir == null) continue;
                if (appId == "228980" || name.Contains("Redistributable") || name.StartsWith("Steam Linux Runtime"))
                    continue;

                var dir = Path.Combine(steamapps, "common", installDir);
                var candidates = Directory.Exists(dir)
                    ? RankExes(dir, Array.Empty<string>(), maxDepth: 2)
                    : new List<ExeCandidate>();

                var names = new List<string> { name, installDir };
                if (candidates.Count > 0) names.AddRange(ReadVersionInfo(candidates[0].Path));

                result.Add(new DetectedGame
                {
                    Source = "Steam",
                    DisplayName = name,
                    SteamUrl = $"steam://rungameid/{appId}",
                    InstallDir = dir,
                    Names = Clean(names),
                    Candidates = candidates,
                });
            }
        }
        return result;
    }

    /// <summary>steam://rungameid/xxx から、監視すべきプロセス名を探す</summary>
    public static string? FindSteamMonitorProcess(string steamUrl)
    {
        var exe = FindSteamApp(steamUrl)?.Candidates.FirstOrDefault()?.Path;
        return exe != null ? Path.GetFileName(exe) : null;
    }

    /// <summary>steam://rungameid/xxx から、そのゲームのインストール先フォルダを探す</summary>
    public static string? FindSteamInstallDir(string steamUrl)
    {
        var app = FindSteamApp(steamUrl);
        return app != null && Directory.Exists(app.InstallDir) ? app.InstallDir : null;
    }

    private static DetectedGame? FindSteamApp(string steamUrl)
    {
        var m = Regex.Match(steamUrl, @"rungameid/(\d+)");
        return m.Success ? ScanSteam(m.Groups[1].Value).FirstOrDefault() : null;
    }

    private static List<string> GetSteamLibraries()
    {
        var libraries = new List<string>();
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamPath)) return libraries;

        steamPath = steamPath.Replace('/', '\\');
        libraries.Add(steamPath);

        // 追加のライブラリ（D: など）は libraryfolders.vdf に書かれている
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    private static string? GetAcfValue(string acf, string key)
    {
        var m = Regex.Match(acf, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ================================================================
    // exe の候補を、よさそうな順に並べる
    // ================================================================

    internal static List<ExeCandidate> RankExes(string dir, IEnumerable<string> shortcutTargets, int maxDepth)
    {
        var targets = new HashSet<string>(shortcutTargets.Where(File.Exists), StringComparer.OrdinalIgnoreCase);

        // フォルダ内の exe（除外対象を除く）＋ショートカットのリンク先
        var files = new List<(string Path, int Depth)>();
        CollectExes(dir, 0, maxDepth, files);
        foreach (var t in targets)
            if (!files.Any(f => string.Equals(f.Path, t, StringComparison.OrdinalIgnoreCase)))
                files.Add((t, 0));

        var dirName = Path.GetFileName(dir.TrimEnd('\\'));
        var scored = new List<(ExeCandidate Candidate, double Score)>();

        foreach (var (path, depth) in files)
        {
            double score = 0;
            var reasons = new List<string>();
            var folder = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);

            // ① ショートカットのリンク先（メーカー指定の起動方法なので最優先）
            if (targets.Contains(path)) { score += 1000; reasons.Add("ショートカット"); }

            // ② ゲームエンジンの特徴
            if (Directory.Exists(Path.Combine(folder, name + "_Data"))) { score += 500; reasons.Add("Unity"); }
            if (SafeFiles(folder, "*.xp3").Any()) { score += 300; reasons.Add("吉里吉里"); }
            if (File.Exists(Path.Combine(folder, name + ".py"))) { score += 200; reasons.Add("Ren'Py"); }
            if (name.EndsWith("-32")) score -= 100;

            // ③ 製品名がフォルダ名に近い・浅い場所にある・大きい
            var product = ReadVersionInfo(path).FirstOrDefault();
            if (product != null) score += 100 * Similarity(product, dirName);
            score -= depth * 50;
            try { score += Math.Log10(Math.Max(1, new FileInfo(path).Length)) * 10; } catch { }

            scored.Add((new ExeCandidate { Path = path, Reason = string.Join("・", reasons) }, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Take(MaxCandidates)
            .Select(x => x.Candidate)
            .ToList();
    }

    private static void CollectExes(string dir, int depth, int maxDepth, List<(string, int)> found)
    {
        foreach (var f in GetExes(dir)) found.Add((f, depth));
        if (depth >= maxDepth) return;
        foreach (var sub in SafeDirs(dir))
            CollectExes(sub, depth + 1, maxDepth, found);
    }

    private static IEnumerable<string> GetExes(string dir) =>
        SafeFiles(dir, "*.exe").Where(f => !ExcludeExe.IsMatch(Path.GetFileNameWithoutExtension(f)));

    /// <summary>exe に埋め込まれた製品名・説明（日本語のタイトルが入っていることが多い）</summary>
    private static List<string> ReadVersionInfo(string exe)
    {
        var list = new List<string>();
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(v.ProductName)) list.Add(v.ProductName.Trim());
            if (!string.IsNullOrWhiteSpace(v.FileDescription)) list.Add(v.FileDescription.Trim());
        }
        catch { /* 読めない exe は無視 */ }
        return list;
    }

    // ================================================================
    // タイトルの照合
    // ================================================================

    /// <summary>2つの名前の似ている度合い（0～1）</summary>
    public static double Similarity(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return 0;
        if (na == nb) return 1;

        // 片方がもう片方を含む（副題の有無など）
        var (shorter, longer) = na.Length <= nb.Length ? (na, nb) : (nb, na);
        if (shorter.Length >= 3 && longer.Contains(shorter))
            return 0.7 + 0.3 * shorter.Length / longer.Length;

        return 1.0 - (double)Levenshtein(na, nb) / Math.Max(na.Length, nb.Length);
    }

    /// <summary>全角→半角、大文字→小文字にして、記号や空白を取り除く</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // ================================================================
    // 補助
    // ================================================================

    private static List<string> Clean(IEnumerable<string> names) =>
        names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); } catch { return Array.Empty<string>(); }
    }

    private static bool IsUnder(string path, string dir)
    {
        try
        {
            var p = Path.GetFullPath(path);
            var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool SamePathSafe(string a, string b)
    {
        try { return !ShortcutResolver.IsUrl(a) && SamePath(a, b); } catch { return false; }
    }
}
