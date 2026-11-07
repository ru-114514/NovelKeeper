using System.Text.Json.Serialization;
using NovelKeeper.Models;

namespace NovelKeeper.Services;

/// <summary>
/// 任意評価基準の項目1つ分（設定で決める。全作品で共通）。
/// 項目は2階層まで作れる：小項目（Children）を持つ項目は「大項目」で、点数は小項目に入れ、大項目の最高点は小項目の最高点の合計になる。
/// 小項目のない項目は、これまでどおりの1項目。Adjust の項目は「調整点」（加点・減点の欄。100点の内訳に入れない）
/// </summary>
public class ScoreItem
{
    /// <summary>名前を変えても作品ごとの点数がつながるよう、固定の ID を付ける</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>この項目の最高点（点数を入れる項目の合計がちょうど 100 になる）。大項目は小項目の合計。調整点は範囲の上限</summary>
    public int Max { get; set; }
    /// <summary>基準点（詳細欄の「基準点を入力」で、空欄の項目に入れる点数。0〜最高点。null なら基準点なし。大項目には付けない）</summary>
    public int? Base { get; set; }
    /// <summary>小項目（あれば大項目。小項目の下には項目を作れない）。古い設定ファイルにはなく、null になる</summary>
    public List<ScoreItem>? Children { get; set; }
    /// <summary>調整点の欄か（加点・減点。範囲は Min〜Max。空欄なら 0。基準1つにつき1つまで）</summary>
    public bool Adjust { get; set; }
    /// <summary>調整点の範囲の下限（0 以下。マイナスにすると減点できる）。調整点でなければ使わない</summary>
    public int Min { get; set; }

    /// <summary>小項目を持つ大項目か</summary>
    [JsonIgnore] public bool IsGroup => Children is { Count: > 0 };
}

/// <summary>点数を入れる欄1つ分（小項目、小項目のない項目、調整点）。Group は、小項目のときの大項目</summary>
public record ScoreField(ScoreItem Item, ScoreItem? Group)
{
    public string Id => Item.Id;
    /// <summary>表示や CSV の見出しに使う名前（小項目は「大項目/小項目」）</summary>
    public string Label => Group == null ? Item.Name : $"{Group.Name}/{Item.Name}";
    public bool IsAdjust => Item.Adjust;
    /// <summary>入れられる点数の下限（調整点だけマイナスになりうる）</summary>
    public int Min => Item.Adjust ? Item.Min : 0;
    public int Max => Item.Max;
}

/// <summary>採点したときの欄の名前（小項目は「大項目/小項目」）と最高点（調整点は範囲の上限と下限）</summary>
public record ScoreBasis(string Name, int Max, int Min = 0);

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

    /// <summary>全基準の、点数を入れる欄の項目（基準をまたいで使う）</summary>
    public static List<ScoreItem> AllItems() => Templates.SelectMany(t => ScoreItemRules.Fields(t)).Select(f => f.Item).ToList();

    /// <summary>作品が使う基準の番号。その基準が使えないときは、使える最初の基準にする（なければ 0）</summary>
    public static int TemplateOf(Review r) =>
        ItemsFor(r.ScoreTemplate).Count > 0 ? r.ScoreTemplate : UsableTemplates().DefaultIfEmpty(0).First();

    public static string NameOf(int template) => template >= 0 && template < Names.Count ? Names[template] : DefaultName(template);
}

/// <summary>任意評価基準の規則（画面にも DB にも触れないので、そのままテストできる）</summary>
public static class ScoreItemRules
{
    public const int TotalMax = 100;
    /// <summary>調整点の範囲の限度（-100〜+100）</summary>
    public const int AdjustLimit = 100;

    /// <summary>
    /// 点数を入れる欄を、設定の並びのまま平たく並べる（大項目は小項目に置き換え、調整点も含む）。
    /// 小項目のない基準（前の版の設定）では、項目そのものが並ぶ
    /// </summary>
    public static List<ScoreField> Fields(IReadOnlyList<ScoreItem>? items)
    {
        var list = new List<ScoreField>();
        if (items == null) return list;
        foreach (var item in items)
        {
            if (item == null) continue;
            if (item.IsGroup)
                list.AddRange(item.Children!.Where(c => c != null).Select(c => new ScoreField(c, item)));
            else
                list.Add(new ScoreField(item, null));
        }
        return list;
    }

    /// <summary>100点の内訳になる欄（調整点を除く）</summary>
    public static List<ScoreField> Leaves(IReadOnlyList<ScoreItem>? items) => Fields(items).Where(f => !f.IsAdjust).ToList();

    /// <summary>調整点の欄（なければ null）</summary>
    public static ScoreField? AdjustField(IReadOnlyList<ScoreItem>? items) => Fields(items).FirstOrDefault(f => f.IsAdjust);

    /// <summary>最高点の合計（調整点を除く）</summary>
    public static int Sum(IEnumerable<ScoreItem> items) => Leaves(items.ToList()).Sum(f => f.Max);

    /// <summary>大項目の最高点（小項目の最高点の合計。小項目がなければ自分の最高点）</summary>
    public static int GroupMax(ScoreItem group) => group.IsGroup ? group.Children!.Where(c => c != null).Sum(c => c.Max) : group.Max;

    /// <summary>大項目の小計（入力できた小項目の点数の合計）</summary>
    public static int GroupSubtotal(ScoreItem group, IReadOnlyDictionary<string, int> scores) =>
        group.Children?.Where(c => c != null).Sum(c => scores.GetValueOrDefault(c.Id)) ?? 0;

    /// <summary>項目の設定を検査する。問題がなければ null、あれば画面に出す文。どんな形の設定でも例外は出さない（取り込んだ設定も通るため）</summary>
    public static string? ValidateTemplate(IReadOnlyList<ScoreItem>? items)
    {
        if (items == null || items.Count == 0) return "項目がありません。1つ以上の項目を追加してください。";
        if (items.Any(i => i == null) || items.Any(i => i.Children != null && i.Children.Any(c => c == null)))
            return "項目の設定が壊れています。項目を設定し直してください。";
        var children = items.Where(i => i.Children != null).SelectMany(i => i.Children!).ToList();
        var ids = items.Concat(children).Select(i => i.Id).ToList();
        if (ids.Any(string.IsNullOrEmpty) || ids.Distinct().Count() != ids.Count) return "項目の設定が壊れています。項目を設定し直してください。";
        if (children.Any(c => c.Children is { Count: > 0 })) return "小項目の下には、項目を作れません。";
        if (children.Any(c => c.Adjust)) return "調整点は、小項目には置けません。";
        if (items.Any(i => i.Adjust && i.IsGroup)) return "調整点には、小項目を付けられません。";
        if (items.Count(i => i.Adjust) > 1) return "調整点は 1 つまでです。";
        if (items.Concat(children).Any(i => string.IsNullOrWhiteSpace(i.Name))) return "名前が空の項目があります。";
        if (HasDuplicateName(items) || items.Any(i => i.IsGroup && HasDuplicateName(i.Children!))) return "同じ名前の項目があります。";
        var leaves = Leaves(items);
        if (leaves.Count == 0) return "点数を入れる項目がありません。調整点のほかに、1つ以上の項目を追加してください。";
        if (leaves.Any(f => f.Max < 1 || f.Max > TotalMax)) return $"最高点は 1～{TotalMax} の整数にしてください。";
        if (leaves.Any(f => f.Item.Base is int b && (b < 0 || b > f.Max))) return "基準点は 0～最高点の整数にしてください。";
        if (AdjustField(items) is { } adj)
        {
            if (adj.Min < -AdjustLimit || adj.Min > 0 || adj.Max < 0 || adj.Max > AdjustLimit || adj.Min == adj.Max)
                return $"調整点の範囲は、下限を -{AdjustLimit}～0、上限を 0～{AdjustLimit} の整数にしてください（下限と上限を同じにはできません）。";
            if (adj.Item.Base is int ab && (ab < adj.Min || ab > adj.Max)) return "調整点の基準点は、範囲の中の整数にしてください。";
        }
        if (Sum(items) != TotalMax) return "入力エラー。採点基準は合計が100点になるように入力してください。";
        return null;

        static bool HasDuplicateName(IEnumerable<ScoreItem> list)
        {
            var names = list.Select(i => i.Name.Trim()).ToList();
            return names.Distinct().Count() != names.Count;
        }
    }

    /// <summary>100点を項目数で均等に振り分けた最高点（余りは先頭から1点ずつ）</summary>
    public static int[] Distribute(int count)
    {
        if (count <= 0) return Array.Empty<int>();
        var each = TotalMax / count;
        var rest = TotalMax % count;
        return Enumerable.Range(0, count).Select(i => each + (i < rest ? 1 : 0)).ToArray();
    }

    private static string RangeError(ScoreField f) => $"入力エラー。{f.Min}～{f.Max}点の範囲で入力してください。";

    /// <summary>
    /// 詳細欄の入力（欄の ID → 入力した文字）を検査する。
    /// 全部の欄が空欄なら入力なし（従来どおり得点欄に直接入れる）。1つでも入力したら、調整点のほかの全部の欄の入力が必要。
    /// 調整点は空欄なら 0。合計（項目の合計＋調整点）が 0～100 でなければエラー
    /// </summary>
    public static ScoreCheck Check(IReadOnlyList<ScoreItem> items, IReadOnlyDictionary<string, string> texts)
    {
        var r = new ScoreCheck();
        var fields = Fields(items);
        string TextOf(ScoreField f) => (texts.GetValueOrDefault(f.Id) ?? "").Trim();
        foreach (var f in fields)
        {
            var t = TextOf(f);
            if (t == "") continue;
            r.AnyEntered = true;
            if (!int.TryParse(t, out var v) || v < f.Min || v > f.Max)
                r.Errors[f.Id] = RangeError(f);
            else
                r.Scores[f.Id] = v;
        }
        if (r.AnyEntered)
        {
            foreach (var f in fields)
            {
                // 空欄の調整点は 0 として扱うが、点数としては残さない（あとで範囲を変えても、採点し直しの対象にしないため）
                if (TextOf(f) == "" && !f.IsAdjust) r.Errors[f.Id] = RangeError(f);
            }
            if (r.Ok)
            {
                var total = r.Scores.Values.Sum();
                if (total < 0 || total > TotalMax)
                    r.Errors[AdjustField(items)?.Id ?? fields[^1].Id] = $"入力エラー。合計が 0～{TotalMax}点になるように、調整点を直してください（いまの合計は {total}点）。";
                else
                    r.Total = total;
            }
        }
        return r;
    }

    /// <summary>基準点がある欄があるか（詳細欄の「基準点を入力」を出すかの判定）</summary>
    public static bool HasBase(IReadOnlyList<ScoreItem> items) => Fields(items).Any(f => f.Item.Base != null);

    /// <summary>基準点の合計（基準点がある欄だけ。調整点の基準点も含む）</summary>
    public static int BaseSum(IReadOnlyList<ScoreItem> items) => Fields(items).Sum(f => f.Item.Base ?? 0);

    /// <summary>
    /// 基準点を入れる対象：入力欄が空で、基準点がある欄の（ID → 基準点）。
    /// すでに入力した欄（エラーの文字も含む）は対象にしない（上書きしない）
    /// </summary>
    public static Dictionary<string, int> BaseFill(IReadOnlyList<ScoreItem> items, IReadOnlyDictionary<string, string> texts) =>
        Fields(items).Where(f => f.Item.Base != null && (texts.GetValueOrDefault(f.Id) ?? "").Trim() == "")
            .ToDictionary(f => f.Id, f => f.Item.Base!.Value);

    /// <summary>採点した内容を作品の記録に書く（入力なしなら項目別の採点を消す）。大項目には点数を持たせない（小項目の合計で出す）</summary>
    /// <param name="touched">
    /// 画面で点数を触った欄の ID。指定したときは、触っていない欄は以前の基準の記録を残す
    /// （基準が変わった欄は、採点し直すまで赤いままにするため）。null なら全部の欄を今の基準で記録する
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
        review.ItemBasis = Fields(items).ToDictionary(f => f.Id,
            f => touched != null && !touched.Contains(f.Id) && old != null && old.TryGetValue(f.Id, out var b)
                ? b
                : new ScoreBasis(f.Label, f.Max, f.Min));
    }

    /// <summary>項目別の採点があるか</summary>
    public static bool HasItemScores(Review r) => r.ItemScores is { Count: > 0 };

    /// <summary>
    /// 基準が変わったのに、採点し直していない欄の ID（名前か最高点が変わった・新しく足した欄。小項目は大項目の名前が変わったときも）。
    /// 項目別の採点がない作品（得点だけ入れた作品）は対象にしない。
    /// 調整点は、あとから足しても 0 として扱えるので、点数があって範囲や名前が変わったときだけ対象にする
    /// </summary>
    public static HashSet<string> StaleItems(IReadOnlyList<ScoreItem> items, Review r)
    {
        var stale = new HashSet<string>();
        if (!HasItemScores(r)) return stale;
        foreach (var f in Fields(items))
        {
            var has = r.ItemScores!.ContainsKey(f.Id);
            if (f.IsAdjust && !has) continue;
            if (has && r.ItemBasis != null && r.ItemBasis.TryGetValue(f.Id, out var b) &&
                b.Name == f.Label && b.Max == f.Max && b.Min == f.Min) continue;
            stale.Add(f.Id);
        }
        return stale;
    }

    /// <summary>設定から消した項目（小項目を付けて大項目にした項目も含む）の点数が、この作品に残っているか</summary>
    public static bool HasRemovedItems(IReadOnlyList<ScoreItem> items, Review r)
    {
        if (!HasItemScores(r)) return false;
        var ids = Fields(items).Select(f => f.Id).ToHashSet();
        return r.ItemScores!.Keys.Any(id => !ids.Contains(id));
    }

    /// <summary>基準が変わって、採点し直しが必要な作品か</summary>
    public static bool NeedsRescore(IReadOnlyList<ScoreItem> items, Review r) =>
        StaleItems(items, r).Count > 0 || HasRemovedItems(items, r);

    /// <summary>項目別の点数の合計（調整点も含む）が、得点と食い違っているか（批評空間の得点を取り込んだとき、オフの間に得点を直したときなど）</summary>
    public static bool TotalMismatch(Review r, int? myScore) =>
        HasItemScores(r) && r.ItemScores!.Values.Sum() != myScore;

    /// <summary>CSV などに出す、欄の点数（なければ空欄）</summary>
    public static string CellText(ScoreItem item, Review r) =>
        r.ItemScores != null && r.ItemScores.TryGetValue(item.Id, out var v) ? v.ToString() : "";
}
