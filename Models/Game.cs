using NovelKeeper.Services;

namespace NovelKeeper.Models;

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
    /// <summary>この作品は、管理者権限で起動する（Windows の確認画面が出る。URL 形式の起動には使えない）</summary>
    public bool RunAsAdmin { get; set; }

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
    /// <summary>購入額（円。任意。このツールだけに保存し、批評空間には送らない。未入力なら null）</summary>
    public int? Price { get; set; }
    /// <summary>任意評価基準を使っていて、項目別の点数の合計と得点が食い違っているか（一覧の「得点」を赤くする）</summary>
    public bool ScoreMismatch => ScoreItemSettings.Enabled && ScoreItemRules.TotalMismatch(Review, MyScore);

    /// <summary>任意評価基準の基準が変わって、採点し直しが必要か</summary>
    public bool ScoreNeedsRescore => ScoreItemSettings.Enabled && ScoreItemRules.NeedsRescore(ScoreItemSettings.ItemsFor(Review), Review);

    /// <summary>一覧の「得点」を赤い枠で囲むか（項目の合計と食い違っている、または基準が変わって採点し直しが必要）</summary>
    public bool ScoreNeedsAttention => ScoreMismatch || ScoreNeedsRescore;

    /// <summary>「得点」が赤いときの説明（マウスを合わせたときに出す）</summary>
    public string ScoreAttentionText =>
        ScoreMismatch ? "項目の合計と得点が食い違っています。詳細欄で確認してください。"
        : ScoreNeedsRescore ? "登録後に基準を変更しています。採点し直してください。"
        : "";

    /// <summary>一覧の「購入額」列の表示（3,300 のようにカンマ区切り。未入力なら空欄）</summary>
    public string PriceText => Price?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    /// <summary>初めて起動した日時（プレイ時間の記録から）</summary>
    public DateTime? FirstPlayed { get; set; }

    // ---- 批評空間側の状態（最後に同期したときの値） ----
    public bool EgsPlayed { get; set; }
    public bool EgsGiveUp { get; set; }
    public int? EgsScore { get; set; }
    /// <summary>批評空間で「所持」に☑があるか（null は不明：未同期・未登録）</summary>
    public bool? EgsPossession { get; set; }
    /// <summary>批評空間に記録を登録した日（userreview.timestamp。null は不明）</summary>
    public DateTime? EgsRegistered { get; set; }
    public string RegisteredText => EgsRegistered?.ToString("yyyy/MM/dd") ?? "";
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

    /// <summary>「積みゲーに数えない」にした作品の ID（画面側が設定と同じ値にそろえる。一覧の状態に表示する）</summary>
    public static HashSet<long> BacklogExcludedIds { get; set; } = new();

    /// <summary>投稿設定の「今後優先したい作品」に入れた作品の ID（画面側が設定と同じ値にそろえる）</summary>
    public static HashSet<long> PriorityIds { get; set; } = new();

    public bool IsBacklogExcluded => BacklogExcludedIds.Contains(Id);
    public bool IsPriority => PriorityIds.Contains(Id);
    /// <summary>一覧の「特例」列の文字（優先に入れた作品は「★優先」、積みゲーに数えない作品は「除外」）</summary>
    public string MarkText => (IsPriority ? "★優先" : "") + (IsPriority && IsBacklogExcluded ? " " : "") + (IsBacklogExcluded ? "除外" : "");

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
    /// 購入前の作品か：「所持」が付いておらず（外れている、または不明）、「プレイ済」も「放棄」も付いておらず（購入予定など）、
    /// ツールでも起動設定・プレイ記録がない未プレイの作品
    /// </summary>
    public bool IsBeforePurchase =>
        Status == GameStatus.Unplayed && (Review.Possession ?? EgsPossession) != true && !EgsPlayed && !EgsGiveUp &&
        IsLaunchUnset && TotalSeconds == 0;

    /// <summary>発売日が今日より後の作品か（発売日が読めない・未登録なら false）</summary>
    public bool IsUnreleased => IsUnreleasedOn(DateTime.Today);

    /// <summary>発売日が今日以前の作品か（「表示:」の「発売後」用。発売日が読めない・未定なら false）</summary>
    public bool IsReleased => IsReleasedOn(DateTime.Today);

    internal bool IsUnreleasedOn(DateTime today) => ParsedSellDay is DateTime d && d > today.Date;

    /// <summary>発売日が今日以前の作品か（発売日が読めない・未定なら false）</summary>
    internal bool IsReleasedOn(DateTime today) => ParsedSellDay is DateTime d && d <= today.Date;

    private DateTime? ParsedSellDay =>
        SellDay is { Length: >= 10 } s &&
        DateTime.TryParse(s[..10], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.Date : null;

    /// <summary>
    /// 購入確認が必要か：発売済みなのに所持が付いておらず（起動設定・プレイ記録もない）、購入予定に何か選んである
    /// （必ず購入・多分購入・様子見）。買ったら「所持」を付けると、これに当てはまらなくなる
    /// </summary>
    public bool NeedsPurchaseCheck => NeedsPurchaseCheckOn(DateTime.Today);

    /// <summary>一覧の「購入確認」列の文字（赤いセルの中に表示する）</summary>
    public string PurchaseCheckText => NeedsPurchaseCheck ? "要確認" : "";

    internal bool NeedsPurchaseCheckOn(DateTime today) =>
        IsBeforePurchase && !string.IsNullOrEmpty(Review.PurchaseWill) && IsReleasedOn(today);

    /// <summary>一覧の行の色分けと、「表示:」の絞り込み用：発売日が先の作品は「発売前」、所持がなく手つかずの作品は「購入前」（両方なら「発売前」）</summary>
    public string Category => IsUnreleased ? "発売前" : IsBeforePurchase ? "購入前" : "";

    public string PossessionText => (Review.Possession ?? EgsPossession) == true ? "所持" : "";

    // ---- 得点登録フォームの項目（一覧の列で使う） ----
    public int? TotalPlayTimeHours => Review.TotalPlayTime;
    public int? FunTimeHours => Review.FunTime;
    public string OkazuText => Review.OkazuText;
    public string PurchaseWillText => Review.PurchaseWillText;

    /// <summary>購入額が未入力か（購入額での並び替えで、昇順・降順どちらでも未入力を最後にするための補助）</summary>
    public bool PriceMissing => Price == null;
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

    /// <summary>設定の「すべての作品を常に管理者権限で起動する」（一覧の色分け用。画面側が設定と同じ値にそろえる）</summary>
    public static bool AlwaysRunAsAdmin { get; set; }

    /// <summary>いま管理者権限で起動される作品か（一覧で紫にする。URL 形式の起動は管理者にできないので対象外）</summary>
    public bool RunsAsAdmin =>
        IsLaunchSet && (RunAsAdmin || AlwaysRunAsAdmin) && !NovelKeeper.Services.ShortcutResolver.IsUrl(ExePath!);

    public string LaunchText =>
        IsLaunchUnset ? "⬇ ここにドロップ"
        : CurrentTarget is LaunchTarget t ? t.Display
        : ExePath!.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ? "Steam"
        : System.IO.Path.GetFileName(ExePath);

    public string PlayTimeText => TotalSeconds == 0
        ? ""
        : Clock(TotalSeconds);

    /// <summary>秒を「12:30:51」（時:分:秒。時は桁を揃えず、分と秒は2桁）にする。一覧・詳細・CSV で同じ表記にする</summary>
    public static string Clock(long seconds)
    {
        var s = Math.Max(0, seconds);
        return $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}";
    }

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
