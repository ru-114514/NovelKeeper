namespace ErogeLauncher.Models;

public enum GameStatus
{
    Unplayed = 0, // 未プレイ（積み）
    Playing = 1,  // プレイ中
    Played = 2,   // プレイ済
    Dropped = 3,  // 中断
}

/// <summary>ツールで管理する1作品</summary>
public class Game
{
    public long Id { get; set; }

    // ---- 批評空間から取得する情報 ----
    public int? EgsId { get; set; }
    public string Title { get; set; } = "";
    public string? Brand { get; set; }
    public string? SellDay { get; set; }
    public int? Median { get; set; }
    /// <summary>批評空間の平均値</summary>
    public int? EgsAverage { get; set; }
    /// <summary>批評空間のデータ数（得点を付けた人数）</summary>
    public int? EgsCount { get; set; }

    // ---- ランチャー設定 ----
    public string? ExePath { get; set; }
    /// <summary>監視するプロセス名（空なら起動した exe 自体を監視）</summary>
    public string? MonitorProcess { get; set; }
    /// <summary>ゲームのフォルダ（プレイ中の判定に使う。空なら実行ファイルのフォルダ）</summary>
    public string? InstallDir { get; set; }

    /// <summary>起動するものの一覧（収録作ごと。数に制限なし）。ExePath は「今使うもの」</summary>
    public List<LaunchTarget> Targets { get; set; } = new();

    /// <summary>今使う起動するもの（ExePath と同じパスのもの）</summary>
    public LaunchTarget? CurrentTarget =>
        IsLaunchUnset ? null
        : Targets.FirstOrDefault(t => string.Equals(t.Path, ExePath, StringComparison.OrdinalIgnoreCase));

    // ---- 状態 ----
    public GameStatus Status { get; set; } = GameStatus.Unplayed;
    public DateTime? ClearDate { get; set; }
    public long TotalSeconds { get; set; }
    /// <summary>自分の得点（0～100、未入力なら null）</summary>
    public int? MyScore { get; set; }

    /// <summary>得点登録フォームのその他の項目（所持・総プレイ時間・感想など）</summary>
    public Review Review { get; set; } = new();
    /// <summary>ツールで変更して、まだ批評空間に反映していない項目（Review.Keys の名前）</summary>
    public HashSet<string> PendingReview { get; set; } = new();
    /// <summary>初めて起動した日時（プレイ時間の記録から）</summary>
    public DateTime? FirstPlayed { get; set; }

    // ---- 批評空間側の状態（最後に同期したときの値） ----
    public bool EgsPlayed { get; set; }
    public bool EgsGiveUp { get; set; }
    public int? EgsScore { get; set; }
    /// <summary>批評空間で「所持」に☑があるか（null は不明：未同期・未登録）</summary>
    public bool? EgsPossession { get; set; }
    public DateTime? EgsClearDate { get; set; }
    public DateTime? EgsSyncedAt { get; set; }

    // ---- 一覧表示用 ----
    public bool IsPlayed => Status == GameStatus.Played;

    public string StatusText => Status switch
    {
        GameStatus.Unplayed => "未プレイ",
        GameStatus.Playing => "プレイ中",
        GameStatus.Played => "☑ プレイ済",
        GameStatus.Dropped => "中断",
        _ => "",
    };

    /// <summary>並び替え用の状態の順番：プレイ中 ＞ 未プレイ ＞ 中断（放棄） ＞ プレイ済</summary>
    public int StatusOrder => Status switch
    {
        GameStatus.Playing => 0,
        GameStatus.Unplayed => 1,
        GameStatus.Dropped => 2,
        GameStatus.Played => 3,
        _ => 9,
    };

    /// <summary>
    /// 購入前の作品か：批評空間で「所持」も「プレイ済」も「放棄」も付いておらず（購入予定など）、
    /// ツールでも起動設定・プレイ記録がない未プレイの作品
    /// </summary>
    public bool IsBeforePurchase =>
        Status == GameStatus.Unplayed && (Review.Possession ?? EgsPossession) == false && !EgsPlayed && !EgsGiveUp &&
        IsLaunchUnset && TotalSeconds == 0;

    public string PossessionText => (Review.Possession ?? EgsPossession) == true ? "所持" : "";

    // ---- 得点登録フォームの項目（一覧の列で使う） ----
    public int? TotalPlayTimeHours => Review.TotalPlayTime;
    public int? FunTimeHours => Review.FunTime;
    public string OkazuText => Review.OkazuText;
    public string PurchaseWillText => Review.PurchaseWillText;
    public int? BeforeScore => Review.BeforeScore;
    public DateTime? StartDate => Review.StartDate;
    public string StartDateText => Review.StartDate?.ToString("yyyy/MM/dd") ?? "";
    public string PlatformText => Review.PlatformText;
    public string OperationText => Review.OperationText;
    public string PlatformMemoHead => Head(Review.PlatformMemo);
    public string Hitokoto => Review.Hitokoto;
    public string SageText => Review.Sage ? "☑" : "";
    public string MemoHead => Head(Review.Memo);
    public string NetabareText => Review.Netabare ? "ネタバレ" : "";

    /// <summary>長い文章の1行目だけ（一覧用）</summary>
    private static string Head(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var line = s.Trim().Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    public string ClearDateText => ClearDate?.ToString("yyyy/MM/dd") ?? "";

    /// <summary>実行ファイルが未設定か（一覧の「起動設定」欄で使う）</summary>
    public bool IsLaunchUnset => string.IsNullOrWhiteSpace(ExePath);
    public bool IsLaunchSet => !IsLaunchUnset;

    public string LaunchText =>
        IsLaunchUnset ? "⬇ ここにドロップ"
        : CurrentTarget is LaunchTarget t ? t.Display
        : ExePath!.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ? "Steam"
        : System.IO.Path.GetFileName(ExePath);

    public string PlayTimeText => TotalSeconds == 0
        ? ""
        : $"{TotalSeconds / 3600}時間{TotalSeconds % 3600 / 60:00}分";

    public string EgsText
    {
        get
        {
            if (EgsId == null) return "未連携";
            if (EgsSyncedAt == null) return "未同期";
            if (EgsGiveUp) return "放棄";
            if (!EgsPlayed) return "未プレイ";
            return EgsClearDate is DateTime d ? $"プレイ済 {d:yyyy/MM/dd}" : "プレイ済";
        }
    }
}
