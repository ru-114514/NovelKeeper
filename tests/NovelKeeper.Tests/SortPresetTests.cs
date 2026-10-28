using System.Text.Json;
using NovelKeeper.Services;

namespace NovelKeeper.Tests;

/// <summary>並び替えの設定1〜3（保存・読み込み・同じ並びの判定）のテスト</summary>
public class SortPresetTests
{
    private static SortKey K(string p, bool desc = false) => new() { Property = p, Descending = desc };

    [Fact]
    public void SameOrder_ComparesPropertyDirectionAndOrder()
    {
        Assert.True(SortKey.SameOrder([K("Title"), K("MyScore", true)], [K("Title"), K("MyScore", true)]));
        Assert.False(SortKey.SameOrder([K("Title")], [K("Title", true)]));
        Assert.False(SortKey.SameOrder([K("Title"), K("MyScore")], [K("MyScore"), K("Title")]));
        Assert.False(SortKey.SameOrder([K("Title")], [K("Title"), K("MyScore")]));
        Assert.True(SortKey.SameOrder([], []));
    }

    [Fact]
    public void NewConfig_HasThreeEmptyPresets()
    {
        var c = new EgsConfig();
        Assert.Equal(3, c.SortPresets.Count);
        Assert.All(c.SortPresets, p => Assert.Empty(p));
    }

    [Fact]
    public void Normalize_PadsTrimsAndFixesNulls()
    {
        var c = new EgsConfig { SortPresets = new() { new() { K("Title") } } };
        c.NormalizeSortPresets();
        Assert.Equal(3, c.SortPresets.Count);
        Assert.Single(c.SortPresets[0]);

        c.SortPresets = new() { null!, new(), new(), new() { K("Brand") } };
        c.NormalizeSortPresets();
        Assert.Equal(3, c.SortPresets.Count);
        Assert.NotNull(c.SortPresets[0]);
    }

    [Fact]
    public void Presets_SurviveJsonRoundTrip_AndOldConfigLoads()
    {
        var c = new EgsConfig();
        c.SortPresets[1] = [K("ClearDate", true), K("Title")];
        var back = JsonSerializer.Deserialize<EgsConfig>(JsonSerializer.Serialize(c))!;
        Assert.True(SortKey.SameOrder(c.SortPresets[1], back.SortPresets[1]));

        var old = JsonSerializer.Deserialize<EgsConfig>("{}")!;
        old.NormalizeSortPresets();
        Assert.Equal(3, old.SortPresets.Count);
    }
}
