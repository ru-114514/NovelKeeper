using NovelKeeper.Services;

namespace NovelKeeper.Tests;

public class EgsSearchQueryTests
{
    [Fact]
    public void SpaceSeparatedWords_AreAllRequired_InAnyOrder()
    {
        var sql = EgsSearchQuery.Build("さくらの雲　スカーレット");  // 全角空白でも分ける
        Assert.Equal(2, EgsSearchQuery.Tokens("さくらの雲　スカーレット").Count);
        Assert.Contains(" AND ", sql);
        Assert.Contains("g.gamename ILIKE '%さくらの雲%'", sql);
        Assert.Contains("g.gamename ILIKE '%スカ_レット%'", sql); // 長音は1文字の代わり（スカアレットにも当たる）
    }

    [Fact]
    public void Hiragana_IsMatchedAgainstKatakanaReading_WithoutSymbols()
    {
        Assert.Equal("アンラベルトリガー", EgsSearchQuery.Reading("あんらべる・とりがー"));
        Assert.Equal("コールドウォー", EgsSearchQuery.Reading("コールド ウォー"));
        Assert.Contains("g.furigana ILIKE '%アンラベルトリガ_%'", EgsSearchQuery.Build("アンラベル・トリガー"));
    }

    [Theory]
    [InlineData("Fate")]      // 英字
    [InlineData("恋愛")]      // 漢字
    [InlineData("Cold War")]  // 英数字（読みはカタカナなので使わない）
    public void WordsWithKanjiOrLatin_DoNotUseTheReading(string word) =>
        Assert.Null(EgsSearchQuery.Reading(word.Split(' ')[0]));

    [Fact]
    public void WidthVariants_AreTried_ForTheTitle()
    {
        var sql = EgsSearchQuery.Build("ABC１２３");
        Assert.Contains("g.gamename ILIKE '%ABC１２３%'", sql); // 入力どおり
        Assert.Contains("g.gamename ILIKE '%ABC123%'", sql);   // 半角
        Assert.Contains("g.gamename ILIKE '%ＡＢＣ１２３%'", sql); // 全角
        Assert.Contains("g.gamename ILIKE '%ﾊﾟﾗ%'", EgsSearchQuery.Build("ﾊﾟﾗ")); // 半角かな（入力どおり）
        Assert.Contains("g.furigana ILIKE '%パラ%'", EgsSearchQuery.Build("ﾊﾟﾗ"));  // 全角かなにそろえて読みに当てる
    }

    [Fact]
    public void Brand_IsAlsoSearched()
        => Assert.Contains("b.brandname ILIKE '%アリスソフト%'", EgsSearchQuery.Build("アリスソフト 大悪司"));

    [Fact]
    public void QuotesAndLikeWildcards_AreEscaped()
    {
        var sql = EgsSearchQuery.Build("a'b%_");
        Assert.Contains(@"ILIKE '%a''b\%\_%'", sql);
        Assert.DoesNotContain("'%a'b", sql);
    }

    [Fact]
    public void Ordering_PutsTheExactTitleFirst_ThenNewest()
        => Assert.Contains("ORDER BY (g.gamename ILIKE '%大悪司%') DESC, g.sellday DESC NULLS LAST", EgsSearchQuery.Build("大悪司"));

    [Fact]
    public void WhitespaceOnlyOrEmpty_StillBuildsValidSql()
        => Assert.Contains("WHERE (", EgsSearchQuery.Build("   "));
}
