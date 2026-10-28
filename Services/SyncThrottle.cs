namespace NovelKeeper.Services;

/// <summary>
/// 「批評空間と同期」を続けて押せない時間の決め方。
///  - 前回の同期を始めてから 30 秒は、成功・失敗を問わず押せない
///  - 失敗が続くほど、待ち時間を倍にする（30秒 → 60秒 → 120秒 → …、上限5分）
///  - 時計が戻っていたとき（今が前回より前）は、制限しない
/// </summary>
public static class SyncThrottle
{
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    public static TimeSpan Cooldown(int failures)
    {
        var doubled = BaseCooldown.TotalSeconds * Math.Pow(2, Math.Clamp(failures, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(doubled, MaxCooldown.TotalSeconds));
    }

    /// <summary>次に押せるまでの残り時間。押してよければ null</summary>
    public static TimeSpan? Remaining(DateTime? lastAttempt, int failures, DateTime now)
    {
        if (lastAttempt is not DateTime last) return null;
        var elapsed = now - last;
        if (elapsed < TimeSpan.Zero) return null;
        var left = Cooldown(failures) - elapsed;
        return left > TimeSpan.Zero ? left : null;
    }
}
