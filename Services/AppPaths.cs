using System.IO;
namespace ErogeLauncher.Services;

/// <summary>設定・DB の保存場所（%AppData%\NovelKeeper）</summary>
public static class AppPaths
{
    private const string AppFolderName = "NovelKeeper";
    private const string OldFolderName = "ErogeLauncher"; // 名前を変える前の保存場所

    public static string Dir { get; } = CreateDir();

    private static string CreateDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, AppFolderName);
        var oldDir = Path.Combine(appData, OldFolderName);

        // 旧名の保存場所しかなければ、そのまま新しい名前に移す（作品一覧・設定・ログイン情報を引き継ぐ）
        if (!Directory.Exists(dir) && Directory.Exists(oldDir))
        {
            try { Directory.Move(oldDir, dir); }
            catch { CopyDirectory(oldDir, dir); } // 移せなければコピーする（元は残す）
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>フォルダを中のフォルダ（backups・images など）ごと写す。写し先にあるファイルは上書きしない</summary>
    internal static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(dest)) File.Copy(file, dest);
        }
        foreach (var sub in Directory.GetDirectories(from))
            CopyDirectory(sub, Path.Combine(to, Path.GetFileName(sub)));
    }

    public static string Database => Path.Combine(Dir, "games.db");
    public static string Config => Path.Combine(Dir, "egs_config.json");
    public static string Credential => Path.Combine(Dir, "credential.bin");
}
