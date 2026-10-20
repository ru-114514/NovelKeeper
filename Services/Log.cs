using System.IO;

namespace ErogeLauncher.Services;

/// <summary>
/// 動作の記録（%AppData%\NovelKeeper\log.txt）。不具合の原因を調べるためのもの。
/// 書けなくても本体の動作は止めない。大きくなったら log.old.txt に回して、古い記録は1世代だけ残す。
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    /// <summary>記録を置くフォルダ（既定は %AppData%\NovelKeeper。テストが本物のフォルダを汚さないよう、差し替えられる）</summary>
    public static string? Directory { get; set; }

    private static string Dir => Directory ?? AppPaths.Dir;

    public static string Path => System.IO.Path.Combine(Dir, "log.txt");

    public static void Info(string message) => Write("INFO ", message);

    /// <summary>想定内の失敗（握りつぶしてよいが、原因調査のため残す）</summary>
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>想定外のエラー（スタックトレースつき）</summary>
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(Path, System.IO.Path.Combine(Dir, "log.old.txt"), overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 記録できなくても、本体の動作は続ける
        }
    }
}
