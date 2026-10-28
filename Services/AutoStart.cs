using Microsoft.Win32;

namespace NovelKeeper.Services;

/// <summary>Windows の起動時に自動で起動する設定（現在のユーザーの「スタートアップ」に登録）</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NovelKeeper";
    private const string OldValueName = "ErogeLauncher"; // 旧名の登録が残っていたら消す

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue(OldValueName, throwOnMissingValue: false);
        if (enable && Environment.ProcessPath != null)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray"); // --tray：画面を出さずに常駐で起動
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
