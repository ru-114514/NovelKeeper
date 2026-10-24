using System.Globalization;
using System.Text.Json.Serialization;
using ErogeLauncher.Services;

namespace ErogeLauncher.Models;

/// <summary>
/// 批評空間の得点登録フォームの項目のうち、プレイ済・得点・クリア日（終了日）以外のもの。
/// ツールの DB には JSON でまとめて保存する。
/// </summary>
public class Review
{
    /// <summary>所持（null は不明）</summary>
    public bool? Possession { get; set; }
    /// <summary>総プレイ時間（時間）</summary>
    public int? TotalPlayTime { get; set; }
    /// <summary>面白さが分かるまでのプレイ時間（時間）</summary>
    public int? FunTime { get; set; }
    /// <summary>おかずに使えた?（null は「-」）</summary>
    public int? Okazu { get; set; }
    /// <summary>購入予定（"" は「-」）</summary>
    public string PurchaseWill { get; set; } = "";
    /// <summary>購入前得点</summary>
    public int? BeforeScore { get; set; }
    /// <summary>ゲーム開始日</summary>
    public DateTime? StartDate { get; set; }
    /// <summary>platform（批評空間の番号）</summary>
    public int? Platform { get; set; }
    /// <summary>動作状況（1 動作 / 2 条件付きで動作 / 3 動作不可）</summary>
    public int? Operation { get; set; }
    /// <summary>詳細状況</summary>
    public string PlatformMemo { get; set; } = "";
    /// <summary>一言感想</summary>
    public string Hitokoto { get; set; } = "";
    /// <summary>一言感想をトップページに表示しない</summary>
    public bool Sage { get; set; }
    /// <summary>長文感想</summary>
    public string Memo { get; set; } = "";
    /// <summary>ネタバレ</summary>
    public bool Netabare { get; set; }

    /// <summary>任意評価基準の項目別の採点（項目の ID → 点数）。ツールだけに保存し、批評空間には送らない。なければ null</summary>
    public Dictionary<string, int>? ItemScores { get; set; }
    /// <summary>採点したときの項目の名前と最高点（項目の ID → 内容）。基準が変わったかを見分けるために残す</summary>
    public Dictionary<string, ScoreBasis>? ItemBasis { get; set; }

    /// <summary>クリア数の集計（レポート・目標・投稿設定）から除外する。過去にクリアして記録を忘れた作品など向け。ツールだけに保存し、批評空間には送らない</summary>
    public bool ExcludeFromClearCount { get; set; }

    /// <summary>批評空間から読み込めなかった項目（取り込まない。空欄で上書きしないため）</summary>
    [JsonIgnore] public HashSet<string> Unreadable { get; set; } = new();

    public Review Clone()
    {
        var c = (Review)MemberwiseClone();
        c.Unreadable = new HashSet<string>(); // 写した先では「読み込めなかった」は引き継がない
        c.ItemScores = ItemScores == null ? null : new Dictionary<string, int>(ItemScores);
        c.ItemBasis = ItemBasis == null ? null : new Dictionary<string, ScoreBasis>(ItemBasis);
        return c;
    }

    // ================================================================
    // 選択肢（批評空間のフォームと同じもの）
    // ================================================================

    public static readonly (int? Value, string Label)[] OkazuOptions =
    {
        (null, "-"), (4, "とても使えた"), (3, "かなり使えた"), (2, "だいぶ使えた"), (1, "それなりに使えた"),
        (-1, "あまり使えなかった"), (-2, "まったく使えなかった"), (-998, "そもそも趣向があわなかった"),
    };

    public static readonly (string Value, string Label)[] PurchaseWillOptions =
    {
        ("", "-"), ("0_必ず購入", "必ず購入"), ("多分購入", "多分購入"), ("様子見", "様子見"),
    };

    public static readonly (int? Value, string Label)[] PlatformOptions =
    {
        (null, "-"), (350, "Windows11"), (351, "Windows11 on Arm"), (165, "Windows10(64bit)"), (164, "Windows10(32bit)"),
        (8, "Windows8(64bit)"), (7, "Windows8(32bit)"), (317, "Wine on macOS"), (6, "Windows7(64bit)"),
        (5, "Windows7(32bit)"), (4, "WindowsVista"), (3, "WindowsXP"), (2, "Windows2000"), (1, "Windows98"),
    };

    public static readonly (int? Value, string Label)[] OperationOptions =
    {
        (null, "-"), (1, "動作"), (2, "条件付きで動作"), (3, "動作不可・インストール不可"),
    };

    // ================================================================
    // 表示用
    // ================================================================

    [JsonIgnore] public string OkazuText => Okazu == null ? "" : OkazuOptions.FirstOrDefault(o => o.Value == Okazu).Label ?? Okazu.ToString()!;
    [JsonIgnore] public string PurchaseWillText => PurchaseWillOptions.FirstOrDefault(o => o.Value == PurchaseWill).Label is { } l && l != "-" ? l : PurchaseWill;
    [JsonIgnore] public string PlatformText => Platform == null ? "" : PlatformOptions.FirstOrDefault(o => o.Value == Platform).Label ?? Platform.ToString()!;
    [JsonIgnore] public string OperationText => Operation == null ? "" : OperationOptions.FirstOrDefault(o => o.Value == Operation).Label ?? Operation.ToString()!;

    // ================================================================
    // 批評空間のフォームとの対応
    // ================================================================

    /// <summary>項目の名前（変更の記録・反映に使う）と、画面での名前</summary>
    public static readonly (string Key, string Label)[] Keys =
    {
        ("possession", "所持"),
        ("giveup", "放棄"),
        ("total_play_time", "総プレイ時間"),
        ("fun_time", "面白さが分かるまでのプレイ時間"),
        ("okazu", "おかずに使えた?"),
        ("purchase_will", "購入予定"),
        ("before_tokuten", "購入前得点"),
        ("start_date", "ゲーム開始日"),
        ("platform", "platform・動作状況・詳細状況"),
        ("hitokoto", "一言感想"),
        ("sage", "トップページに表示しない"),
        ("memo", "長文感想"),
        ("netabare", "ネタバレ"),
    };

    /// <summary>
    /// 指定した項目を、得点登録フォームに送る値にする（値が null の項目は「☑を外す」＝送らない）。
    /// giveup（放棄）は、ツールの状態が「中断」かどうかで決める。
    /// </summary>
    /// <param name="field">項目の名前 → 得点登録フォームの欄の name 属性（設定ファイルで変えられる。EgsConfig.ReviewFormField）</param>
    public Dictionary<string, string?> ToFormValues(IEnumerable<string> keys, bool giveUp, Func<string, string> field)
    {
        static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        static string? Check(bool b) => b ? "on" : null;

        var d = new Dictionary<string, string?>();
        foreach (var key in keys.Distinct())
        {
            switch (key)
            {
                case "possession": d[field("possession")] = Check(Possession == true); break;
                case "giveup": d[field("giveup")] = Check(giveUp); break;
                case "total_play_time": d[field("total_play_time")] = N(TotalPlayTime); break;
                case "fun_time": d[field("fun_time")] = N(FunTime); break;
                case "okazu": d[field("okazu")] = (Okazu ?? -999).ToString(CultureInfo.InvariantCulture); break;
                case "purchase_will": d[field("purchase_will")] = PurchaseWill ?? ""; break;
                case "before_tokuten": d[field("before_tokuten")] = N(BeforeScore); break;
                case "start_date": d[field("start_date")] = StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; break;
                case "platform":
                    d[field("platform")] = N(Platform);
                    d[field("operation")] = N(Operation);
                    d[field("platform_memo")] = PlatformMemo ?? "";
                    break;
                case "hitokoto": d[field("hitokoto")] = Hitokoto ?? ""; break;
                case "sage": d[field("sage")] = Check(Sage); break;
                case "memo": d[field("memo")] = Memo ?? ""; break;
                case "netabare": d[field("netabare")] = Check(Netabare); break;
            }
        }
        return d;
    }

    /// <summary>2つの内容で、違っている項目の名前（giveup は扱わない）</summary>
    public static List<string> Diff(Review a, Review b)
    {
        var list = new List<string>();
        if (a.Possession != b.Possession) list.Add("possession");
        if (a.TotalPlayTime != b.TotalPlayTime) list.Add("total_play_time");
        if (a.FunTime != b.FunTime) list.Add("fun_time");
        if (a.Okazu != b.Okazu) list.Add("okazu");
        if ((a.PurchaseWill ?? "") != (b.PurchaseWill ?? "")) list.Add("purchase_will");
        if (a.BeforeScore != b.BeforeScore) list.Add("before_tokuten");
        if (a.StartDate?.Date != b.StartDate?.Date) list.Add("start_date");
        if (a.Platform != b.Platform || a.Operation != b.Operation || Norm(a.PlatformMemo) != Norm(b.PlatformMemo)) list.Add("platform");
        if (Norm(a.Hitokoto) != Norm(b.Hitokoto)) list.Add("hitokoto");
        if (a.Sage != b.Sage) list.Add("sage");
        if (Norm(a.Memo) != Norm(b.Memo)) list.Add("memo");
        if (a.Netabare != b.Netabare) list.Add("netabare");
        return list;
    }

    /// <summary>批評空間から読み込んだ内容のうち、指定した項目だけを写す</summary>
    public void CopyFrom(Review src, string key)
    {
        switch (key)
        {
            case "possession": Possession = src.Possession; break;
            case "total_play_time": TotalPlayTime = src.TotalPlayTime; break;
            case "fun_time": FunTime = src.FunTime; break;
            case "okazu": Okazu = src.Okazu; break;
            case "purchase_will": PurchaseWill = src.PurchaseWill; break;
            case "before_tokuten": BeforeScore = src.BeforeScore; break;
            case "start_date": StartDate = src.StartDate; break;
            case "platform": Platform = src.Platform; Operation = src.Operation; PlatformMemo = src.PlatformMemo; break;
            case "hitokoto": Hitokoto = src.Hitokoto; break;
            case "sage": Sage = src.Sage; break;
            case "memo": Memo = src.Memo; break;
            case "netabare": Netabare = src.Netabare; break;
        }
    }

    /// <summary>
    /// 比べるときは、空白・改行の違いは同じとみなす
    /// （批評空間の読み込み方によっては改行が失われるため。改行だけの変更は「変更なし」になる）
    /// </summary>
    public static string Norm(string? s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", "");
}
