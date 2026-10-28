using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

/// <summary>Windows の機能の呼び出し（プレイ時間の計測で使う）が、正しく値を返すか</summary>
public class NativeMethodsTests
{
    [Fact]
    public void GetProcessPath_ReturnsThisProcessExe()
    {
        var path = NativeMethods.GetProcessPath(Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
    }

    [Fact]
    public void GetProcessPath_UnknownProcess_ReturnsNull()
    {
        Assert.Null(NativeMethods.GetProcessPath(-1));
    }

    [Fact]
    public void IdleSeconds_IsNotNegative()
    {
        Assert.True(NativeMethods.IdleSeconds() >= 0);
    }
}
