using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace NovelKeeper.Tests;

public class EgsParsingTests
{
    private const string TableHtml = """
        <div id="query_result_header"><h3>SQLの実行結果</h3></div>
        <div id="query_result_main"><table>
        <tr><th>id</th><th>gamename</th></tr>
        <tr><td>1</td><td>テスト &amp; 作品</td></tr>
        <tr><td>2</td><td>二つ目</td></tr>
        </table></div>
        """;

    [Fact]
    public void ParsesResultTable()
    {
        var rows = EgsClient.ParseResultTable(TableHtml);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("テスト & 作品", rows[0]["gamename"]);
    }

    [Fact]
    public void ZeroRows_IsEmptyList_NotAnError()
    {
        // 実際の批評空間は、0件のとき表を出さず、見出しだけを返す
        var rows = EgsClient.ParseResultTable("<div id=\"query_result_header\"><h3>SQLの実行結果</h3></div><div id=\"query_result_main\"></div>");
        Assert.Empty(rows);
    }

    [Fact]
    public void SqlErrorPage_Throws_WithServerMessage()
    {
        var html = "<h3>エラー(SQL間違い)</h3><p>ERROR: syntax error at or near &quot;SELEC&quot;</p>";
        var ex = Assert.Throws<EgsException>(() => EgsClient.ParseResultTable(html));
        Assert.Contains("syntax error", ex.Message);
    }

    [Fact]
    public void UnexpectedPage_Throws_InsteadOfLookingLikeZeroRecords()
    {
        Assert.Throws<EgsException>(() => EgsClient.ParseResultTable("<html><body>メンテナンス中です</body></html>"));
        Assert.Throws<EgsException>(() => EgsClient.ParseResultTable(""));
    }

    [Fact]
    public void ReviewFormValues_UncheckedBoxesAreSentAsNull_AndEmptyNumbersAsEmpty()
    {
        var review = new Review { Possession = false, Okazu = null, TotalPlayTime = 12, Hitokoto = "x" };
        var values = review.ToFormValues(["possession", "okazu", "total_play_time", "hitokoto"], giveUp: false);

        Assert.Null(values["data[possession]"]);                 // ☑を外す＝送らない
        Assert.Equal("-999", values["data[okazu]"]);             // 「-」は -999
        Assert.Equal("12", values["data[total_play_time]"]);
        Assert.Equal("x", values["data[hitokoto]"]);
    }

    [Fact]
    public void ReviewDiff_IgnoresWhitespaceInLongText()
    {
        var a = new Review { Memo = "a b\r\nc" };
        var b = new Review { Memo = "ab c" };
        Assert.DoesNotContain("memo", Review.Diff(a, b));
        b.Memo = "abd";
        Assert.Contains("memo", Review.Diff(a, b));
    }
}
