using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 任意評価基準の設定ウィンドウ：基準を最大 3 つ（タブ 1〜3）まで設定し、項目ごとの採点を使うかを切り替える。
/// ジャンルなどで基準を使い分けるため、基準ごとに名前と項目（名前と最高点。合計はちょうど 100）を持つ。
/// 検査の規則は ScoreItemRules（画面に触れない）に任せ、ここは画面だけを受け持つ
/// </summary>
public class ScoreCriteriaWindow : Window
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));

    private readonly EgsConfig _cfg;
    private readonly Func<List<Game>> _getGames;
    private readonly Func<bool> _confirmDiscard;
    private readonly CheckBox _enabled = new() { Content = "項目ごとの採点を使う" };
    private readonly TabControl _tabs = new();
    private readonly List<Editor> _editors = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _save = new() { Content = "保存", Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };

    /// <summary>保存したか（呼び出し側が、画面を更新するために見る）</summary>
    public bool Changed { get; private set; }

    /// <param name="confirmDiscard">保存の直前に呼ぶ。詳細欄に未保存の変更があるとき、破棄してよいかを聞く（false なら保存しない）</param>
    public ScoreCriteriaWindow(Window owner, EgsConfig cfg, Func<List<Game>> getGames, Func<bool> confirmDiscard)
    {
        _cfg = cfg; _getGames = getGames; _confirmDiscard = confirmDiscard;
        Owner = owner;
        Title = "任意評価基準";
        Width = 620; Height = 640; MinWidth = 560; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        Refresh();
    }

    private sealed class Row
    {
        public required string Id { get; init; }
        public required TextBox NameBox { get; init; }
        public required TextBox MaxBox { get; init; }
        public required TextBox BaseBox { get; init; }
        public required UIElement Panel { get; init; }
    }

    private sealed class Editor
    {
        public required int Index { get; init; }
        public required TabItem Tab { get; init; }
        public required TextBox NameBox { get; init; }
        public required StackPanel RowsHost { get; init; }
        public required TextBlock Sum { get; init; }
        public required TextBlock Error { get; init; }
        public List<Row> Rows { get; } = new();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock
        {
            Text = "ジャンルなどで評価基準を使い分けたいときは、基準を最大 3 つ（タブ 1〜3）まで設定できます。作品の詳細欄で、その作品に使う基準を選びます。" +
                   "批評空間に送るのは合計の得点だけです（項目別の点数はツールにだけ残します）。オフにすると、これまでどおり得点欄に直接入力します（設定と点数は消えません）。",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 6),
        });
        head.Children.Add(_enabled);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new WrapPanel();
        _save.Click += Save_Click;
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(18, 4, 18, 4), IsCancel = true };
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        for (var i = 0; i < ScoreItemSettings.TemplateCount; i++) _editors.Add(BuildEditor(i));
        foreach (var e in _editors) _tabs.Items.Add(e.Tab);
        root.Children.Add(_tabs);

        _enabled.Click += (_, _) => UpdateState();
        return root;
    }

    private Editor BuildEditor(int index)
    {
        var host = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var nameBox = new TextBox { Width = 180, MaxLength = 12, VerticalContentAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        nameRow.Children.Add(new TextBlock { Text = "基準の名前　", VerticalAlignment = VerticalAlignment.Center });
        nameRow.Children.Add(nameBox);

        var rows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var sum = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.Bold };
        var error = new TextBlock { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        var tab = new TabItem { Header = ScoreItemSettings.DefaultName(index) };
        var editor = new Editor { Index = index, Tab = tab, NameBox = nameBox, RowsHost = rows, Sum = sum, Error = error };

        var add = new Button { Content = "＋ 項目を追加", Padding = new Thickness(10, 3, 10, 3) };
        add.Click += (_, _) =>
        {
            if (editor.Rows.Count >= ScoreItemRules.MaxItems)
            {
                error.Text = $"項目は {ScoreItemRules.MaxItems} 個までです。";
                return;
            }
            AddRow(editor, Guid.NewGuid().ToString("N")[..8], "", "", "");
            Update(editor);
            editor.Rows[^1].NameBox.Focus();
        };
        var distribute = new Button { Content = "最高点を均等に振り分ける", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        distribute.Click += (_, _) =>
        {
            var parts = ScoreItemRules.Distribute(editor.Rows.Count);
            for (var i = 0; i < parts.Length; i++) editor.Rows[i].MaxBox.Text = parts[i].ToString();
            Update(editor);
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(distribute);

        host.Children.Add(nameRow);
        host.Children.Add(new TextBlock { Text = "項目（名前と最高点。最高点の合計をちょうど 100 にします）。項目がなければ、この基準は使いません。", TextWrapping = TextWrapping.Wrap });
        host.Children.Add(rows);
        host.Children.Add(buttons);
        host.Children.Add(sum);
        host.Children.Add(error);
        tab.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = host, Padding = new Thickness(8) };

        nameBox.TextChanged += (_, _) =>
            tab.Header = string.IsNullOrWhiteSpace(nameBox.Text) ? ScoreItemSettings.DefaultName(index) : nameBox.Text.Trim();
        return editor;
    }

    private void AddRow(Editor editor, string id, string name, string max, string basePoint)
    {
        var nameBox = new TextBox { Width = 170, Text = name, MaxLength = 20, VerticalContentAlignment = VerticalAlignment.Center };
        var maxBox = new TextBox { Width = 50, Text = max, MaxLength = 3, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var baseBox = new TextBox { Width = 56, Text = basePoint, MaxLength = 3, Margin = new Thickness(4, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "基準点（任意）。詳細欄の「基準点を入力」で、空欄の項目に入る点数です。0〜最高点" };
        var remove = new Button { Content = "✕", Margin = new Thickness(22, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), ToolTip = "この項目を削除" };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        panel.Children.Add(nameBox);
        panel.Children.Add(maxBox);
        panel.Children.Add(new TextBlock { Text = "点満点", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Opacity = 0.7 });
        panel.Children.Add(new TextBlock { Text = "基準点", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.7 });
        panel.Children.Add(baseBox);
        panel.Children.Add(remove);
        var row = new Row { Id = id, NameBox = nameBox, MaxBox = maxBox, BaseBox = baseBox, Panel = panel };
        nameBox.TextChanged += (_, _) => Update(editor);
        maxBox.TextChanged += (_, _) => Update(editor);
        baseBox.TextChanged += (_, _) => Update(editor);
        remove.Click += (_, _) =>
        {
            editor.Rows.Remove(row);
            editor.RowsHost.Children.Remove(panel);
            Update(editor);
        };
        editor.RowsHost.Children.Add(panel);
        editor.Rows.Add(row);
    }

    /// <summary>設定の内容を画面に写す</summary>
    private void Refresh()
    {
        _enabled.IsChecked = _cfg.ScoreItemsEnabled;
        foreach (var e in _editors)
        {
            e.NameBox.Text = e.Index < _cfg.ScoreTemplateNames.Count ? _cfg.ScoreTemplateNames[e.Index] : "";
            foreach (var item in _cfg.ScoreItemsOf(e.Index)) AddRow(e, item.Id, item.Name, item.Max.ToString(), item.Base?.ToString() ?? "");
            Update(e);
        }
    }

    /// <summary>編集中の項目（最高点が数字でないものは 0 として扱い、検査でエラーにする）</summary>
    private static List<ScoreItem> Draft(Editor e) =>
        e.Rows.Select(r => new ScoreItem
        {
            Id = r.Id,
            Name = r.NameBox.Text.Trim(),
            Max = int.TryParse(r.MaxBox.Text.Trim(), out var m) ? m : 0,
            Base = r.BaseBox.Text.Trim() == "" ? null : int.TryParse(r.BaseBox.Text.Trim(), out var b) ? b : -1, // 数でなければ -1 にして、検査でエラーにする
        }).ToList();

    /// <summary>項目が 1 つもない基準は「使わない」ので、問題なしとして扱う</summary>
    private static string? ErrorOf(Editor e) => e.Rows.Count == 0 ? null : ScoreItemRules.ValidateTemplate(Draft(e));

    private void Update(Editor e)
    {
        var draft = Draft(e);
        if (e.Rows.Count == 0)
        {
            e.Sum.Text = "未使用（項目がありません）";
            e.Sum.Foreground = Brushes.Gray;
            e.Error.Text = "";
        }
        else
        {
            var sum = ScoreItemRules.Sum(draft);
            var remain = ScoreItemRules.TotalMax - sum;
            var error = ScoreItemRules.ValidateTemplate(draft);
            e.Sum.Text = $"最高点の合計：{sum} / {ScoreItemRules.TotalMax}" + (remain > 0 ? $"（あと {remain}）" : remain < 0 ? $"（{-remain} 超過）" : "")
                         + (ScoreItemRules.HasBase(draft) && draft.All(i => i.Base is >= 0) ? $"　／　基準点の合計：{ScoreItemRules.BaseSum(draft)}" : "");
            e.Sum.Foreground = error == null ? Brushes.ForestGreen : ErrorBrush;
            e.Error.Text = error ?? "";
        }
        UpdateState();
    }

    /// <summary>保存できるかを確かめて、できない理由を下に出す</summary>
    private void UpdateState()
    {
        var bad = _editors.FirstOrDefault(e => ErrorOf(e) != null);
        if (bad != null)
        {
            _summary.Text = $"「{bad.Tab.Header}」の設定にエラーがあります。直すと保存できます。";
            _save.IsEnabled = false;
        }
        else if (_enabled.IsChecked == true && _editors.All(e => e.Rows.Count == 0))
        {
            _summary.Text = "項目ごとの採点を使うには、1 つ以上の基準に項目を設定してください。";
            _save.IsEnabled = false;
        }
        else
        {
            _summary.Text = "";
            _save.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var drafts = _editors.Select(Draft).ToList();

        // 基準を変えると、採点済みの作品のうち何本が「採点し直し」の対象になるかを知らせる（得点は変えない）
        var affected = _getGames().Count(g => ScoreItemRules.NeedsRescore(
            drafts[Math.Clamp(g.Review.ScoreTemplate, 0, drafts.Count - 1)], g.Review));
        if (affected > 0 &&
            MessageBox.Show(this, $"この設定では、項目別に採点済みの {affected} 本が「採点し直し」の対象になります。\n\n" +
                                  "・各作品の得点は変わりません（批評空間にも送りません）\n" +
                                  "・一覧の項目の列で、変わった項目が赤く表示されます\n" +
                                  "・作品を開いて採点し直して保存すると、赤い表示が消えます\n\n保存しますか？",
                "任意評価基準", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!_confirmDiscard()) return;

        for (var i = 0; i < drafts.Count; i++) _cfg.SetScoreItems(i, drafts[i]);
        _cfg.ScoreTemplateNames = _editors.Select(x => x.NameBox.Text.Trim()).ToList();
        _cfg.ScoreItemsEnabled = _enabled.IsChecked == true;
        _cfg.Save();
        Changed = true;
        DialogResult = true;
    }
}
