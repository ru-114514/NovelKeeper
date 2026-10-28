using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>一覧の「設定1〜3」ボタン：並び替えの条件を保存しておいて、ワンクリックで切り替える</summary>
public partial class MainWindow
{
    private Button[] SortPresetButtons => new[] { SortPreset1, SortPreset2, SortPreset3 };

    private static string DescribeSort(IEnumerable<SortKey> keys) => string.Join(" → ", keys.Select(k =>
    {
        var label = SortWindow.Options.FirstOrDefault(o => o.Property == k.Property).Label ?? k.Property;
        var cut = label.IndexOf('（'); // 「（降順で…）」などの補足は省く
        return (cut > 0 ? label[..cut] : label) + (k.Descending ? " ▼" : " ▲");
    }));

    /// <summary>ボタンの見た目を、設定の中身に合わせる（未設定は薄く、いまの並びと同じものは太字）</summary>
    private void RefreshSortPresetButtons()
    {
        if (_cfg == null || SortPreset1 == null) return;
        _cfg.NormalizeSortPresets();
        var buttons = SortPresetButtons;
        for (var i = 0; i < buttons.Length; i++)
        {
            var keys = _cfg.SortPresets[i];
            var set = keys.Count > 0;
            var current = set && SortKey.SameOrder(keys, _cfg.SortKeys);
            var clear = new[] { SortPresetClear1, SortPresetClear2, SortPresetClear3 }[i];
            clear.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
            clear.ToolTip = $"設定{i + 1}を解除します（空にします）";
            buttons[i].Opacity = set ? 1 : 0.5;
            buttons[i].FontWeight = current ? FontWeights.Bold : FontWeights.Normal;
            buttons[i].ToolTip = set
                ? $"設定{i + 1}：{DescribeSort(keys)}" + (current ? "（いまの並び）" : "")
                : $"設定{i + 1}：未設定（「⇅ 並び替え…」の画面で保存できます）";
        }
    }

    /// <summary>設定ボタンの横の「×」：確認してから、その設定を空にする</summary>
    private void SortPresetClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var i) || i < 0 || i >= EgsConfig.SortPresetCount) return;
        _cfg.NormalizeSortPresets();
        if (_cfg.SortPresets[i].Count == 0) return;
        if (MessageBox.Show(this, $"設定{i + 1}（{DescribeSort(_cfg.SortPresets[i])}）を解除しますか？\n（いまの並び方は変わりません）",
                "並び替えの設定", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _cfg.SortPresets[i] = new();
        _cfg.Save();
        RefreshSortPresetButtons();
        UpdateToolbarOverflow(); // ×が消えて幅が変わる
        StatusText.Text = $"設定{i + 1}を解除しました";
    }

    private void SortPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var i) || i < 0 || i >= EgsConfig.SortPresetCount) return;
        _cfg.NormalizeSortPresets();
        var keys = _cfg.SortPresets[i];
        if (keys.Count == 0)
        {
            StatusText.Text = $"設定{i + 1}は未設定です。「⇅ 並び替え…」の画面で、条件を決めて「設定{i + 1}へ保存」を選んでください";
            return;
        }
        _cfg.SortKeys = keys.Select(k => new SortKey { Property = k.Property, Descending = k.Descending }).ToList();
        _cfg.Save();
        ApplySort();
    }
}
