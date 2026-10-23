using System.Text;

namespace ErogeLauncher.Services;

/// <summary>
/// 作品を追加するときの検索の SQL を作る。批評空間のタイトルは、入力と表記がずれやすい
/// （「スカーレット」が「スカアレット」、「ＡＢＣ」と「ABC」、中黒や記号の有無など）ので、次のようにゆるく探す。
///  ・空白で分けた語を、すべて含む作品（AND）。語の順番や間の記号は問わない
///  ・語ごとに、タイトル（入力どおり・半角全角をそろえたもの）、読み（ふりがな。カタカナ・記号なし）、ブランド名のどれかに含まれればよい
///  ・ひらがなで入力しても、読みのカタカナに当てる
///  ・長音「ー」は、「ア・イ・ウ・エ・オ」などで表記された作品にも当たるよう、1文字の代わりとして扱う
/// 画面にも通信にも触れないので、そのままテストできる。
/// </summary>
public static class EgsSearchQuery
{
    private const int Limit = 100;

    public static string Build(string keyword)
    {
        var tokens = Tokens(keyword);
        if (tokens.Count == 0) tokens.Add(keyword.Trim());

        var conditions = tokens.Select(TokenCondition).ToList();
        var whole = Like(keyword.Trim(), wildcardLongVowel: false);
        return $"""
            SELECT g.id, g.gamename, b.brandname, g.sellday, g.median, g.average2, g.count2
            FROM gamelist g JOIN brandlist b ON g.brandname = b.id
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY (g.gamename ILIKE {whole}) DESC, g.sellday DESC NULLS LAST
            LIMIT {Limit}
            """;
    }

    /// <summary>空白（全角も）で分けた語</summary>
    internal static List<string> Tokens(string keyword) =>
        keyword.Split([' ', '\t', '　'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>1つの語について、タイトル・読み・ブランド名のどれかに当たる条件</summary>
    internal static string TokenCondition(string token)
    {
        var parts = new List<string>();
        foreach (var v in TitleVariants(token))
            parts.Add($"g.gamename ILIKE {Like(v, wildcardLongVowel: true)}");

        var reading = Reading(token);
        if (reading != null) parts.Add($"g.furigana ILIKE {Like(reading, wildcardLongVowel: true)}");

        parts.Add($"b.brandname ILIKE {Like(token, wildcardLongVowel: false)}");
        return "(" + string.Join(" OR ", parts) + ")";
    }

    /// <summary>タイトルに当てる表記：入力どおり、半角にそろえたもの、全角にそろえたもの</summary>
    internal static List<string> TitleVariants(string token)
    {
        var half = token.Normalize(NormalizationForm.FormKC);
        var list = new List<string> { token, half, ToFullWidthAscii(half) };
        return list.Where(s => s != "").Distinct().ToList();
    }

    /// <summary>
    /// 読み（カタカナ）に当てる語。ひらがなをカタカナにし、記号・空白・英数字以外を取り除く。
    /// 読みはカタカナだけなので、かなと長音以外が混じる語（漢字・英数字）は使わない（null）
    /// </summary>
    internal static string? Reading(string token)
    {
        var kana = new StringBuilder();
        foreach (var c in token.Normalize(NormalizationForm.FormKC))
        {
            var k = c is >= 'ぁ' and <= 'ゖ' ? (char)(c + 0x60) : c; // ひらがな → カタカナ
            if (k is >= 'ァ' and <= 'ヺ' or 'ー') kana.Append(k);
            else if (IsIgnorable(k)) continue;
            else return null;
        }
        return kana.Length == 0 ? null : kana.ToString();
    }

    /// <summary>読みに入っていない、無視してよい文字（中黒・記号・空白）</summary>
    private static bool IsIgnorable(char c) =>
        c is '・' or '・' or '･' or '*' or '＊' or '~' or '～' or '〜' or '-' or '‐' or '－' or '!' or '！' or '?' or '？'
          or '☆' or '★' or '♪' or '♥' or '♡' or ':' or '：' or '/' or '／' or '.' or '。' or ',' or '、' or '「' or '」' or '『' or '』'
          or '(' or ')' or '[' or ']' || char.IsWhiteSpace(c);

    /// <summary>ILIKE の '%…%' にする。%・_・\ は文字として扱い、長音「ー」は1文字の代わり（_）にする。引用符は二重にする</summary>
    private static string Like(string text, bool wildcardLongVowel)
    {
        var sb = new StringBuilder("'%");
        foreach (var c in text)
        {
            switch (c)
            {
                case '\'': sb.Append("''"); break;
                case '\\': sb.Append("\\\\"); break;
                case '%': sb.Append("\\%"); break;
                case '_': sb.Append("\\_"); break;
                case 'ー' when wildcardLongVowel: sb.Append('_'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append("%'").ToString();
    }

    /// <summary>半角の英数字・記号を全角にする（批評空間のタイトルに全角で入っている場合に当てるため）</summary>
    private static string ToFullWidthAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is > ' ' and <= '~' && c != '\'' && c != '\\' && c != '%' && c != '_' ? (char)(c + 0xFEE0) : c);
        return sb.ToString();
    }
}
