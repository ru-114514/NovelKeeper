using System.IO;
using System.Runtime.InteropServices;

namespace NovelKeeper.Services;

/// <summary>ショートカット（.lnk / .url）から、実際の起動先を取り出す</summary>
public static class ShortcutResolver
{
    public static string Resolve(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".lnk")
        {
            object? shell = null, shortcut = null;
            try
            {
                // Windows 標準の WScript.Shell を使ってリンク先を読む
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type != null)
                {
                    shell = Activator.CreateInstance(type)!;
                    shortcut = ((dynamic)shell).CreateShortcut(path);
                    string target = ((dynamic)shortcut).TargetPath;
                    if (!string.IsNullOrEmpty(target)) return target;
                }
            }
            catch (Exception ex) { Log.Warn("ショートカットを読めませんでした: " + path, ex); } // そのまま使う
            finally
            {
                // COM の部品は、使い終わったらすぐ解放する（自動検出で .lnk をたくさん読んでも残らないように）
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
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
