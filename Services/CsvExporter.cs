using System.Globalization;
using System.IO;
using System.Text;
using NovelKeeper.Models;

namespace NovelKeeper.Services;

/// <summary>
/// 作品の一覧を CSV にする。Excel でそのまま開けるよう、BOM 付き UTF-8・改行は CRLF で書く。
/// 列の並びは次のとおり（感想の2列は、includeReview が true のときだけ出す）：
/// 作品名, ブランド, 発売日, 批評空間ID, 所持, 状態, 自分の得点, 開始日（ゲーム開始日）, クリア日, 総プレイ時間（時:分:秒）,
/// 中央値, 平均値, データ数, [一言感想, 長文感想], 批評空間側の状態, 最終同期日, 購入額(円)
/// </summary>
public static class CsvExporter
{
    private const string DateFormat = "yyyy/MM/dd";

    private static readonly (string Header, Func<Game, string> Value, bool Review)[] Columns =
    {
        ("作品名", g => g.Title, false),
        ("ブランド", g => g.Brand ?? "", false),
        ("発売日", g => SellDayText(g.SellDay), false),
        ("批評空間ID", g => g.EgsId?.ToString(CultureInfo.InvariantCulture) ?? "", false),
        ("所持", g => g.PossessionText, false),
        ("状態", g => StatusName(g.Status), false),
        ("自分の得点", g => g.MyScore?.ToString(CultureInfo.InvariantCulture) ?? "", false),
        ("開始日", g => Date(g.Review.StartDate), false),
        ("クリア日", g => Date(g.ClearDate), false),
        ("総プレイ時間", g => Hours(g.TotalSeconds), false),
        ("中央値", g => g.Median?.ToString(CultureInfo.InvariantCulture) ?? "", false),
        ("平均値", g => g.EgsAverage?.ToString(CultureInfo.InvariantCulture) ?? "", false),
        ("データ数", g => g.EgsCount?.ToString(CultureInfo.InvariantCulture) ?? "", false),
        ("一言感想", g => g.Review.Hitokoto, true),
        ("長文感想", g => g.Review.Memo, true),
        ("批評空間側の状態", g => g.EgsText, false),
        ("最終同期日", g => Date(g.EgsSyncedAt), false),
        ("購入額(円)", g => g.Price?.ToString(CultureInfo.InvariantCulture) ?? "", false), // 最終列。未入力なら空欄
    };

    /// <summary>
    /// 「全作品」として書き出す対象。「購入前の作品を一覧に表示する」がオフのときは、一覧と同じく購入前の作品を除く
    /// （表示していない作品が、CSV にだけ出てしまわないように）
    /// </summary>
    public static List<Game> SelectAll(IEnumerable<Game> games, bool showBeforePurchase) =>
        games.Where(g => showBeforePurchase || !g.IsBeforePurchase || g.NeedsPurchaseCheck).ToList();

    /// <summary>書き出す列の見出し（画面の「書き出す列」の表示にも使う）</summary>
    public static IReadOnlyList<string> ColumnNames(bool includeReview = false, IReadOnlyList<IReadOnlyList<ScoreItem>>? scoreTemplates = null) =>
        Columns.Where(c => includeReview || !c.Review).Select(c => c.Header)
            .Concat(TemplateColumns(scoreTemplates).Select(c => c.Header)).ToList();

    /// <summary>
    /// 任意評価基準の項目別の点数の列。最後に、基準ごとに「評価1_1」「評価1_2」…「評価2_1」…「評価3_1」…の順で、
    /// 設定した項目の数だけ足す（見出しは「評価1_1(項目名)」。その作品が使っていない基準の欄は空欄）
    /// </summary>
    private static List<(string Header, Func<Game, string> Value, bool Review)> TemplateColumns(IReadOnlyList<IReadOnlyList<ScoreItem>>? templates)
    {
        var cols = new List<(string, Func<Game, string>, bool)>();
        if (templates == null) return cols;
        for (var t = 0; t < templates.Count; t++)
            for (var k = 0; k < templates[t].Count; k++)
            {
                var item = templates[t][k];
                cols.Add(($"評価{t + 1}_{k + 1}({item.Name})", g => ScoreItemRules.CellText(item, g.Review), false));
            }
        return cols;
    }

    /// <summary>作品の並びのまま、CSV の文字列にする</summary>
    public static string Build(IEnumerable<Game> games, bool includeReview = false, IReadOnlyList<IReadOnlyList<ScoreItem>>? scoreTemplates = null)
    {
        var cols = Columns.Where(c => includeReview || !c.Review).ToList();
        // 任意評価基準を使っているときは、最後に項目別の点数の列を足す（既存の列の位置は変えない）
        cols.AddRange(TemplateColumns(scoreTemplates));
        var sb = new StringBuilder();
        sb.Append(string.Join(",", cols.Select(c => Quote(c.Header)))).Append("\r\n");
        foreach (var g in games)
            sb.Append(string.Join(",", cols.Select(c => Quote(c.Value(g))))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>同じ名前のファイルがあれば、名前の末尾に (1) (2) … を付けて、まだないパスにする（上書きしない）</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>ファイルに書く（BOM 付き UTF-8）。途中で止まっても、書きかけのファイルを残さない</summary>
    public static void Write(string path, IEnumerable<Game> games, bool includeReview = false, IReadOnlyList<IReadOnlyList<ScoreItem>>? scoreTemplates = null)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Build(games, includeReview, scoreTemplates),new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// 値を CSV の1欄にする。カンマ・引用符・改行・前後の空白を含むときだけ引用符で囲む。
    /// 先頭が = + - @ のときは、Excel が式として実行しないよう、先頭に ' を付ける。
    /// </summary>
    internal static string Quote(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        var needsQuote = value.Length > 0 &&
                         (value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ');
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string StatusName(GameStatus s) => s switch
    {
        GameStatus.Unplayed => "未プレイ",
        GameStatus.Playing => "プレイ中",
        GameStatus.Played => "プレイ済",
        GameStatus.Dropped => "中断",
        _ => "",
    };

    private static string Date(DateTime? d) => d?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "";

    /// <summary>秒を「12:30:51」（時:分:秒）に。記録がない（0 秒）なら空欄</summary>
    private static string Hours(long seconds) => seconds <= 0 ? "" : Game.Clock(seconds);

    /// <summary>批評空間の発売日（2020-01-31 など）を 2020/01/31 にする。読めなければそのまま</summary>
    private static string SellDayText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        return s.Length >= 10 && DateTime.TryParse(s[..10], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString(DateFormat, CultureInfo.InvariantCulture)
            : s;
    }
}
