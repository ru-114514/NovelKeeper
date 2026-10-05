using System.Windows;
using System.Windows.Controls;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

public partial class SortWindow : Window
{
    /// <summary>並び替えに使える項目（表示名, Game のプロパティ名）</summary>
    public static readonly (string Label, string Property)[] Options =
    {
        ("（なし）", ""),
        ("タイトル", "Title"),
        ("ブランド", "Brand"),
        ("発売日", "SellDay"),
        ("中央値", "Median"),
        ("自分の得点", "MyScore"),
        ("状態（プレイ中→未プレイ→中断→プレイ済）", "StatusOrder"),
        ("クリア日", "ClearDate"),
        ("プレイ時間", "TotalSeconds"),
        ("起動設定（降順で未設定が先）", "IsLaunchUnset"),
    };

    private readonly (ComboBox Key, ComboBox Dir)[] _rows;

    public List<SortKey> Result { get; private set; } = new();

    public SortWindow(List<SortKey> current)
    {
        InitializeComponent();
        _rows = new[] { (Key1, Dir1), (Key2, Dir2), (Key3, Dir3) };

        for (var i = 0; i < _rows.Length; i++)
        {
            var (key, dir) = _rows[i];
            foreach (var o in Options) key.Items.Add(o.Label);
            dir.Items.Add("昇順 ▲");
            dir.Items.Add("降順 ▼");

            var k = i < current.Count ? current[i] : null;
            var index = k == null ? 0 : Array.FindIndex(Options, o => o.Property == k.Property);
            key.SelectedIndex = index < 0 ? 0 : index;
            dir.SelectedIndex = k?.Descending == true ? 1 : 0;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _rows
            .Where(r => r.Key.SelectedIndex > 0)
            .Select(r => new SortKey
            {
                Property = Options[r.Key.SelectedIndex].Property,
                Descending = r.Dir.SelectedIndex == 1,
            })
            .GroupBy(k => k.Property).Select(g => g.First()) // 同じ項目の重複は最初の1つだけ
            .ToList();
        DialogResult = true;
    }
}
