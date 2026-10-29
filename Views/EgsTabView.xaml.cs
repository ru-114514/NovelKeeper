using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 「批評空間連携」タブ：一括取り込み・同期のボタンと、連携の詳細設定（ページや欄の名前）。
/// 取り込み・同期の中身はメイン画面にあり、IMainHost を通して頼む
/// </summary>
public partial class EgsTabView : UserControl
{
    private IMainHost _host = null!;
    private EgsConfig _cfg = null!;

    public EgsTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（InitEgsTab より先に、1回だけ呼ぶ）</summary>
    internal void Init(IMainHost host)
    {
        _host = host;
        _cfg = host.Config;
    }

    internal void InitEgsTab() => BuildEgsFields();

    /// <summary>同期ボタンの説明（前回の同期の日時と、続けて押せない残り時間）。メイン画面が一覧の上の同期ボタンと合わせて書き換える</summary>
    internal void SetSyncTooltip(string tip) => SyncButtonTab.ToolTip = tip;

    private void ImportAll_Click(object sender, RoutedEventArgs e) => _host.ImportAll();

    private void Sync_Click(object sender, RoutedEventArgs e) => _host.Sync();

    /// <summary>批評空間との連携の詳細設定（見出し → 設定の項目名）</summary>
    private static readonly (string Label, string Property)[] EgsFields =
    {
        ("批評空間のURL", nameof(EgsConfig.BaseUrl)),
        ("ページの文字コード", nameof(EgsConfig.CharSet)),
        ("SQLページ", nameof(EgsConfig.SqlPage)),
        ("SQLの入力欄の名前", nameof(EgsConfig.SqlFieldName)),
        ("プレイ済の列名", nameof(EgsConfig.PlayedColumn)),
        ("クリア日の列名", nameof(EgsConfig.ClearDateColumn)),
        ("得点の列名", nameof(EgsConfig.ScoreColumn)),
        ("ログインページ", nameof(EgsConfig.LoginPage)),
        ("ログイン成功の目印", nameof(EgsConfig.LoggedInMarker)),
        ("得点登録ページ", nameof(EgsConfig.ReviewFormUrl)),
        ("プレイ済の欄の名前", nameof(EgsConfig.PlayedField)),
        ("プレイ済の値", nameof(EgsConfig.PlayedValue)),
        ("クリア日の欄の名前", nameof(EgsConfig.ClearDateField)),
        ("クリア日の書式", nameof(EgsConfig.ClearDateFormat)),
        ("得点の欄の名前", nameof(EgsConfig.ScoreField)),
        ("作品ページ", nameof(EgsConfig.GamePageUrl)),
    };

    /// <summary>連携の詳細設定の入力欄を作る（入力欄から離れたときに保存）</summary>
    private void BuildEgsFields()
    {
        EgsFieldsGrid.Children.Clear();
        EgsFieldsGrid.RowDefinitions.Clear();
        var row = 0;
        foreach (var (label, propName) in EgsFields)
        {
            var prop = typeof(EgsConfig).GetProperty(propName);
            if (prop == null) continue;
            EgsFieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            Grid.SetRow(text, row);
            EgsFieldsGrid.Children.Add(text);

            var box = new TextBox { Text = prop.GetValue(_cfg)?.ToString() ?? "", Margin = new Thickness(0, 2, 0, 2) };
            box.LostFocus += (_, _) =>
            {
                try
                {
                    var value = Convert.ChangeType(box.Text.Trim(), prop.PropertyType);
                    if (Equals(value, prop.GetValue(_cfg))) return;
                    prop.SetValue(_cfg, value);
                    _cfg.Save();
                    _host.SetStatus($"「{label}」を保存しました（反映されないときはアプリを再起動してください）");
                }
                catch
                {
                    box.Text = prop.GetValue(_cfg)?.ToString() ?? "";
                    MessageBox.Show($"「{label}」の値が正しくありません。", "入力エラー");
                }
            };
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            EgsFieldsGrid.Children.Add(box);
            row++;
        }
    }
}
