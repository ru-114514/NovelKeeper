using System.Globalization;

namespace ErogeLauncher.Services;

/// <summary>1作品のプレイ時間の記録1件（補正画面用）。Manual は、ここで追加・編集したもの</summary>
public record SessionEntry(long Id, DateTime Start, DateTime End, long Seconds, bool Manual);

/// <summary>プレイ時間の手動補正：入力の検証</summary>
public static class ManualSessionInput
{
    /// <summary>1件で入れられる最長の時間。これより長い分は、日を分けて複数件に入れる（レポートの時間帯が崩れないように）</summary>
    public const int MaxHours = 24;

    /// <summary>
    /// 日付・開始時刻・時間・分の入力を、開始日時と秒数にする。
    /// 全角の数字や「：」も受け付ける。終わりが現在より先になる入力は受け付けない。
    /// </summary>
    public static bool TryBuild(DateTime date, string? timeText, string? hoursText, string? minutesText, DateTime now,
        out DateTime start, out long seconds, out string error)
    {
        start = default;
        seconds = 0;

        if (!DateTime.TryParseExact(Normalize(timeText), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            error = "開始時刻は「20:30」のように入力してください。";
            return false;
        }

        if (!TryCount(hoursText, out var hours) || hours > MaxHours)
        {
            error = $"「時間」は 0～{MaxHours} の整数で入力してください。";
            return false;
        }
        if (!TryCount(minutesText, out var minutes) || minutes > 59)
        {
            error = "「分」は 0～59 の整数で入力してください。";
            return false;
        }

        var total = hours * 3600L + minutes * 60L;
        if (total <= 0)
        {
            error = "プレイ時間を入力してください（0 分より長く）。";
            return false;
        }
        if (total > MaxHours * 3600L)
        {
            error = $"1件で入れられるのは {MaxHours} 時間までです。日を分けて、複数件に入れてください。";
            return false;
        }

        start = date.Date + time.TimeOfDay;
        if (start.AddSeconds(total) > now)
        {
            error = "終わりの日時が現在より先になっています。日付や開始時刻を確認してください。";
            return false;
        }

        seconds = total;
        error = "";
        return true;
    }

    /// <summary>
    /// 入れようとしている記録（start から seconds 秒）と、時間が重なる既存の記録。重なりは警告に使う（登録は止めない）。
    /// 編集のときは、編集前の記録自身（ignore）を数えない
    /// </summary>
    public static List<SessionRow> Overlaps(IEnumerable<SessionRow> existing, long gameId, DateTime start, long seconds, SessionEntry? ignore = null)
    {
        var end = start.AddSeconds(seconds);
        return existing
            .Where(r => r.Start < end && r.End > start)
            .Where(r => !(ignore != null && r.GameId == gameId && r.Start == ignore.Start && r.Seconds == ignore.Seconds))
            .OrderBy(r => r.Start)
            .ToList();
    }

    /// <summary>空欄は 0。数字（全角も可）以外や、負の数は不可</summary>
    private static bool TryCount(string? text, out int value)
    {
        value = 0;
        var s = Normalize(text);
        if (s.Length == 0) return true;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>全角の数字と「：」を半角にして、前後の空白を除く</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var chars = text.Trim().Select(c => c switch
        {
            >= '０' and <= '９' => (char)('0' + (c - '０')),
            '：' => ':',
            _ => c,
        });
        return new string(chars.ToArray());
    }
}
