using System.Runtime.CompilerServices;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

internal static class TestSetup
{
    /// <summary>テスト中の記録は、本物の %AppData%\NovelKeeper ではなく一時フォルダに書く</summary>
    [ModuleInitializer]
    internal static void RedirectLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NovelKeeperTests_log");
        Directory.CreateDirectory(dir);
        Log.Directory = dir;
    }
}
