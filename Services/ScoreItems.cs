using ErogeLauncher.Models;

namespace ErogeLauncher.Services;

/// <summary>任意評価基準の項目1つ分（設定タブで決める。全作品で共通）</summary>
public class ScoreItem
{
    /// <summary>名前を変えても作品ごとの点数がつながるよう、固定の ID を付ける</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>この項目の最高点（全項目の合計がちょうど 100 になる）</summary>
    public int Max { get; set; }
}

/// <summary>採点したときの項目の名前と最高点</summary>
public record ScoreBasis(string Name, int Max);

/// <summary>作品の詳細欄で入力した採点の検査結果</summary>
public class ScoreCheck
{
    /// <summary>項目の ID → エラーの文</summary>
    public Dictionary<string, string> Errors { get; } = new();
    /// <summary>入力できた点数（項目の ID → 点数）</summary>
    public Dictionary<string, int> Scores { get; } = new();
    /// <summary>1項目でも入力があるか</summary>
    public bool AnyEntered { get; set; }
    /// <summary>合計（入力がなければ null）</summary>
    public int? Total { get; set; }
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// いま使っている任意評価基準（設定の内容を、一覧などの表示から参照するための置き場）。
/// 基準は最大 3 つ（ジャンルなどで使い分ける）。作品ごとにどの基準で採点したかを Review.ScoreTemplate に持つ。
/// 項目が検査に通らない基準は、使えない（空）として扱う
/// </summary>
public static class ScoreItemSettings
{
    public const int TemplateCount = 3;

    public static bool Enabled { get; set; }
    public static IReadOnlyList<IReadOnlyList<ScoreItem>> Templates { get; set; } = Enumerable.Range(0, TemplateCount).Select(_ => (IReadOnlyList<ScoreItem>)Array.Empty<ScoreItem>()).ToList();
    public static IReadOnlyList<string> Names { get; set; } = Enumerable.Range(0, TemplateCount).Select(DefaultName).ToList();

    public static string DefaultName(int index) => $"基準{index + 1}";

    public static IReadOnlyList<ScoreItem> ItemsFor(int template) =>
        template >= 0 && template < Templates.Count ? Templates[template] : Array.Empty<ScoreItem>();

    public static IReadOnlyList<ScoreItem> ItemsFor(Review r) => ItemsFor(r.ScoreTemplate);

    /// <summary>詳細欄で選べる基準（項目がある基準）の番号</summary>
    public static List<int> UsableTemplates() =>
        Enumerable.Range(0, Templates.Count).Where(i => Templates[i].Count > 0).ToList();

    /// <summary>全基準の項目（CSV の列など、基準をまたいで使う）</summary>
    public static List<ScoreItem> AllItems() => Templates.SelectMany(t => t).ToList();

    /// <summary>作品が使う基準の番号。その基準が使えないときは、使える最初の基準にする（なければ 0）</summary>
    public static int TemplateOf(Review r) =>
        ItemsFor(r.ScoreTemplate).Count > 0 ? r.ScoreTemplate : UsableTemplates().DefaultIfEmpty(0).First();

    public static string NameOf(int template) => template >= 0 && template < Names.Count ? Names[template] : DefaultName(template);
}

/// <summary>任意評価基準の規則（画面にも DB にも触れないので、そのままテストできる）</summary>
public static class ScoreItemRules
{
    public const int MaxItems = 10;
    public const int TotalMax = 100;

    public static int Sum(IEnumerable<ScoreItem> items) => items.Sum(i => i.Max);

    /// <summary>項目の設定を検査する。問題がなければ null、あれば画面に出す文</summary>
    public static string? ValidateTemplate(IReadOnlyList<ScoreItem> items)
    {
        if (items.Count == 0) return "項目がありません。1つ以上の項目を追加してください。";
        if (items.Count > MaxItems) return $"項目は {MaxItems} 個までです。";
        if (items.Any(i => string.IsNullOrWhiteSpace(i.Name))) return "名前が空の項目があります。";
        if (items.Select(i => i.Name.Trim()).Distinct().Count() != items.Count) return "同じ名前の項目があります。";
        if (items.Any(i => i.Max < 1 || i.Max > TotalMax)) return $"最高点は 1～{TotalMax} の整数にしてください。";
        var sum = Sum(items);
        if (sum != TotalMax) return "入力エラー。採点基準は合計が100点になるように入力してください。";
        return null;
    }

    /// <summary>100点を項目数で均等に振り分けた最高点（余りは先頭から1点ずつ）</summary>
    public static int[] Distribute(int count)
    {
        if (count <= 0) return Array.Empty<int>();
        var each = TotalMax / count;
        var rest = TotalMax % count;
        return Enumerable.Range(0, count).Select(i => each + (i < rest ? 1 : 0)).ToArray();
    }

    /// <summary>
    /// 詳細欄の入力（項目の ID → 入力した文字）を検査する。
    /// 全項目が空欄なら入力なし（従来どおり得点欄に直接入れる）。1項目でも入力したら、全項目の入力が必要
    /// </summary>
    public static ScoreCheck Check(IReadOnlyList<ScoreItem> items, IReadOnlyDictionary<string, string> texts)
    {
        var r = new ScoreCheck();
        foreach (var item in items)
        {
            var t = (texts.GetValueOrDefault(item.Id) ?? "").Trim();
            if (t == "") continue;
            r.AnyEntered = true;
            if (!int.TryParse(t, out var v) || v < 0 || v > item.Max)
                r.Errors[item.Id] = $"入力エラー。0～{item.Max}点の範囲で入力してください。";
            else
                r.Scores[item.Id] = v;
        }
        if (r.AnyEntered)
        {
            foreach (var item in items)
                if ((texts.GetValueOrDefault(item.Id) ?? "").Trim() == "")
                    r.Errors[item.Id] = $"入力エラー。0～{item.Max}点の範囲で入力してください。";
            if (r.Ok) r.Total = r.Scores.Values.Sum();
        }
        return r;
    }

    /// <summary>採点した内容を作品の記録に書く（入力なしなら項目別の採点を消す）</summary>
    /// <param name="touched">
    /// 画面で点数を触った項目の ID。指定したときは、触っていない項目は以前の基準の記録を残す
    /// （基準が変わった項目は、採点し直すまで赤いままにするため）。null なら全項目を今の基準で記録する
    /// </param>
    public static void Apply(Review review, IReadOnlyList<ScoreItem> items, ScoreCheck check, IReadOnlySet<string>? touched = null)
    {
        if (!check.AnyEntered)
        {
            review.ItemScores = null;
            review.ItemBasis = null;
            return;
        }
        var old = review.ItemBasis;
        review.ItemScores = new Dictionary<string, int>(check.Scores);
        review.ItemBasis = items.ToDictionary(i => i.Id,
            i => touched != null && !touched.Contains(i.Id) && old != null && old.TryGetValue(i.Id, out var b)
                ? b
                : new ScoreBasis(i.Name, i.Max));
    }

    /// <summary>項目別の採点があるか</summary>
    public static bool HasItemScores(Review r) => r.ItemScores is { Count: > 0 };

    /// <summary>
    /// 基準が変わったのに、採点し直していない項目の ID（名前か最高点が変わった・新しく足した項目）。
    /// 項目別の採点がない作品（得点だけ入れた作品）は対象にしない
    /// </summary>
    public static HashSet<string> StaleItems(IReadOnlyList<ScoreItem> items, Review r)
    {
        var stale = new HashSet<string>();
        if (!HasItemScores(r)) return stale;
        foreach (var item in items)
        {
            if (r.ItemScores!.ContainsKey(item.Id) && r.ItemBasis != null &&
                r.ItemBasis.TryGetValue(item.Id, out var b) && b.Name == item.Name && b.Max == item.Max) continue;
            stale.Add(item.Id);
        }
        return stale;
    }

    /// <summary>設定から消した項目の点数が、この作品に残っているか</summary>
    public static bool HasRemovedItems(IReadOnlyList<ScoreItem> items, Review r) =>
        HasItemScores(r) && r.ItemScores!.Keys.Any(id => items.All(i => i.Id != id));

    /// <summary>基準が変わって、採点し直しが必要な作品か</summary>
    public static bool NeedsRescore(IReadOnlyList<ScoreItem> items, Review r) =>
        StaleItems(items, r).Count > 0 || HasRemovedItems(items, r);

    /// <summary>項目別の点数の合計が、得点と食い違っているか（批評空間の得点を取り込んだとき、オフの間に得点を直したときなど）</summary>
    public static bool TotalMismatch(Review r, int? myScore) =>
        HasItemScores(r) && r.ItemScores!.Values.Sum() != myScore;

    /// <summary>一覧に出す、項目の点数（なければ空欄）</summary>
    public static string CellText(ScoreItem item, Review r) =>
        r.ItemScores != null && r.ItemScores.TryGetValue(item.Id, out var v) ? v.ToString() : "";
}
