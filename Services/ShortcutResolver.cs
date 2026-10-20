using System.IO;

namespace ErogeLauncher.Services;

/// <summary>ショートカット（.lnk / .url）から、実際の起動先を取り出す</summary>
public static class ShortcutResolver
{
    public static string Resolve(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".lnk")
        {
            try
            {
                // Windows 標準の WScript.Shell を使ってリンク先を読む
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null)
                {
                    dynamic shell = Activator.CreateInstance(type)!;
                    dynamic shortcut = shell.CreateShortcut(path);
                    string target = shortcut.TargetPath;
                    if (!string.IsNullOrEmpty(target)) return target;
                }
            }
            catch (Exception ex) { Log.Warn("ショートカットを読めませんでした: " + path, ex); } // そのまま使う
            return path;
        }

        if (ext == ".url")
        {
            // Steam のデスクトップショートカットは「URL=steam://rungameid/xxx」の形
            try
            {
                foreach (var line in File.ReadLines(path))
                    if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return line[4..].Trim();
            }
            catch (Exception ex) { Log.Warn("ショートカットを読めませんでした: " + path, ex); }
            return path;
        }

        return path;
    }

    public static bool IsUrl(string? path) => path != null && path.Contains("://");
}
