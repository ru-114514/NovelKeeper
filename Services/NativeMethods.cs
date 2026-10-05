using System.Runtime.InteropServices;
using System.Text;

namespace ErogeLauncher.Services;

/// <summary>Windows の機能を直接呼び出す部分（プロセスの場所、操作がない時間、前面のウィンドウ）</summary>
internal static class NativeMethods
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>プロセスの exe のフルパス（取得できなければ null）</summary>
    public static string? GetProcessPath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>最後にマウス・キーボードを操作してからの秒数</summary>
    public static double IdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    /// <summary>前面にあるウィンドウのプロセス ID</summary>
    public static int ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }
}
