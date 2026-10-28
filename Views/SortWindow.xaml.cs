using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

public partial class SortWindow : Window
{
    /// <summary>並び替えに使える項目（表示名, Game のプロパティ名）</summary>
    public static readonly (string Label, string Property)[] Options =
    {
        ("（なし）", ""),
        ("タイトル", "Title"),
        ("ブランド", "Brand"),
        ("起動設定（降順で未設定が先）", "IsLaunchUnset"),
        ("状態（プレイ中→未プレイ→中断→プレイ済）", "StatusOrder"),
        ("プレイ時間", "TotalSeconds"),
        ("クリア日", "ClearDate"),
        ("発売日", "SellDay"),
        ("登録日", "EgsRegistered"),
        ("所持", "PossessionText"),
        ("購入予定", "PurchaseWillText"),
        ("購入確認（降順で要確認が先）", "NeedsPurchaseCheck"),
        ("得点", "MyScore"),
        ("中央値", "Median"),
        ("データ数", "EgsCount"),
        ("購入額（未入力は常に最後）", "Price"),
        ("平均値", "EgsAverage"),
        ("総プレイ時間", "TotalPlayTimeHours"),
        ("開始日", "StartDate"),
    };

    private readonly (ComboBox Key, ComboBox Dir)[] _rows;

    public List<SortKey> Result { get; private set; } = new();
    /// <summary>OK のときに、いまの条件を保存する設定の番号（0〜2。保存しないなら null）</summary>
    public int? SaveToPreset { get; private set; }

    public SortWindow(List<SortKey> current)
    {
        InitializeComponent();
        PresetBox.Items.Add("設定には保存しない");
        for (var i = 1; i <= EgsConfig.SortPresetCount; i++) PresetBox.Items.Add($"設定{i}へ保存");
        PresetBox.SelectedIndex = 0;
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

        var n = EgsConfig.SortPresetCount;
        var pick = PresetBox.SelectedIndex;
        if (pick >= 1 && pick <= n)
        {
            if (Result.Count == 0)
            {
                MessageBox.Show(this, "条件が1つもないので、設定には保存できません。", "並び替え");
                return;
            }
            SaveToPreset = pick - 1;
        }
        DialogResult = true;
    }
}
