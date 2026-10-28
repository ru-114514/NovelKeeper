using NovelKeeper.Services;

namespace NovelKeeper.Tests;

public class SyncThrottleTests
{
    private static readonly DateTime Start = new(2026, 10, 28, 12, 0, 0);

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)]   // 上限は5分
    [InlineData(99, 300)]
    [InlineData(-1, 30)]
    public void Cooldown_DoublesWithFailures_UpToFiveMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncThrottle.Cooldown(failures));

    [Fact]
    public void Remaining_IsNull_WhenNeverSynced() =>
        Assert.Null(SyncThrottle.Remaining(null, 0, Start));

    [Fact]
    public void Remaining_CountsFromTheAttempt_AndEndsAfter30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), SyncThrottle.Remaining(Start, 0, Start.AddSeconds(10)));
        Assert.Null(SyncThrottle.Remaining(Start, 0, Start.AddSeconds(30)));
    }

    [Fact]
    public void Remaining_IsLonger_AfterFailures() =>
        Assert.Equal(TimeSpan.FromSeconds(50), SyncThrottle.Remaining(Start, 1, Start.AddSeconds(10)));

    [Fact]
    public void Remaining_IsIgnored_WhenTheClockWentBack() =>
        Assert.Null(SyncThrottle.Remaining(Start, 3, Start.AddMinutes(-5)));
}

public class EgsTimeoutTests
{
    [Fact]
    public void Timeouts_AreRecognized()
    {
        Assert.True(EgsClient.IsTimeout(new TaskCanceledException()));
        Assert.True(EgsClient.IsTimeout(new TimeoutException()));
        Assert.True(EgsClient.IsTimeout(new HttpRequestException("x", null, System.Net.HttpStatusCode.GatewayTimeout)));
        Assert.True(EgsClient.IsTimeout(new EgsException("批評空間のSQLがエラーになりました。ERROR: canceling statement due to statement timeout")));
    }

    [Fact]
    public void OtherErrors_AreNotTimeouts()
    {
        Assert.False(EgsClient.IsTimeout(new EgsException("base64 が使えません")));
        Assert.False(EgsClient.IsTimeout(new HttpRequestException("x", null, System.Net.HttpStatusCode.InternalServerError)));
    }
}
