using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 任意評価基準の設定ウィンドウ：基準を最大 3 つ（タブ 1〜3）まで設定し、項目ごとの採点を使うかを切り替える。
/// ジャンルなどで基準を使い分けるため、基準ごとに名前と項目（名前と最高点。合計はちょうど 100）を持つ。
/// 項目には小項目を付けられ（2階層まで）、そのときの最高点は小項目の合計になる。調整点（加点・減点の欄）は基準ごとに1つまで。
/// 検査の規則は ScoreItemRules（画面に触れない）に任せ、ここは画面だけを受け持つ
/// </summary>
public class ScoreCriteriaWindow : Window
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));
    /// <summary>入力欄の文字が数でないときの値（検査でエラーにするため、範囲の外の値にする）</summary>
    private const int NotANumber = int.MinValue;

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
        Width = 660; Height = 700; MinWidth = 600; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = BuildUi();
        Refresh();
    }

    /// <summary>項目1行分。大項目の行は、下に小項目の行を持つ</summary>
    private sealed class Row
    {
        public required string Id { get; init; }
        public required bool Adjust { get; init; }
        public required TextBox NameBox { get; init; }
        public required TextBox MaxBox { get; init; }
        public required TextBox BaseBox { get; init; }
        /// <summary>調整点の下限（調整点の行だけ）</summary>
        public TextBox? MinBox { get; init; }
        /// <summary>大項目のときに、最高点（小項目の合計）を出す欄</summary>
        public TextBlock? GroupMaxText { get; init; }
        public required UIElement[] BaseParts { get; init; }
        /// <summary>行全体（大項目なら、小項目の行も含む）</summary>
        public required StackPanel Panel { get; init; }
        /// <summary>小項目の行を置く場所（大項目になれる行だけ）</summary>
        public StackPanel? ChildHost { get; init; }
        public Row? Parent { get; init; }
        public List<Row> Children { get; } = new();
        public bool IsGroup => Children.Count > 0;
    }

    private sealed class Editor
    {
        public required int Index { get; init; }
        public required TabItem Tab { get; init; }
        public required TextBox NameBox { get; init; }
        public required StackPanel RowsHost { get; init; }
        public required TextBlock Sum { get; init; }
        public required TextBlock Error { get; init; }
        public required Button AddAdjust { get; init; }
        /// <summary>大項目と、小項目のない項目と、調整点の行（設定の並び）</summary>
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
        var sum = new TextBlock { Margin = new Thickness(0, 6, 0, 0), FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { Foreground = ErrorBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };

        var tab = new TabItem { Header = ScoreItemSettings.DefaultName(index) };
        var addAdjust = new Button
        {
            Content = "＋ 調整点を追加", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "加点・減点の欄を足します（100点の内訳には入れません。基準ごとに1つまで）",
        };
        var editor = new Editor { Index = index, Tab = tab, NameBox = nameBox, RowsHost = rows, Sum = sum, Error = error, AddAdjust = addAdjust };

        var add = new Button { Content = "＋ 項目を追加", Padding = new Thickness(10, 3, 10, 3) };
        add.Click += (_, _) =>
        {
            var row = AddTopRow(editor, NewId(), "", "", "");
            Update(editor);
            row.NameBox.Focus();
        };
        addAdjust.Click += (_, _) =>
        {
            if (editor.Rows.Any(r => r.Adjust)) return;
            var row = AddAdjustRow(editor, NewId(), "調整点", "-10", "10", "");
            Update(editor);
            row.MinBox!.Focus();
        };
        var distribute = new Button
        {
            Content = "最高点を均等に振り分ける", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "点数を入れる欄（小項目と、小項目のない項目）に、100点を均等に振り分けます",
        };
        distribute.Click += (_, _) =>
        {
            var leaves = LeafRows(editor);
            var parts = ScoreItemRules.Distribute(leaves.Count);
            for (var i = 0; i < parts.Length; i++) leaves[i].MaxBox.Text = parts[i].ToString();
            Update(editor);
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(addAdjust);
        buttons.Children.Add(distribute);

        host.Children.Add(nameRow);
        host.Children.Add(new TextBlock
        {
            Text = "項目（名前と最高点。最高点の合計をちょうど 100 にします）。項目がなければ、この基準は使いません。\n" +
                   "「＋小項目」で項目の下に小項目を付けると、点数は小項目ごとに入れ、項目の最高点は小項目の合計になります。",
            TextWrapping = TextWrapping.Wrap,
        });
        host.Children.Add(rows);
        host.Children.Add(buttons);
        host.Children.Add(sum);
        host.Children.Add(error);
        tab.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = host, Padding = new Thickness(8) };

        nameBox.TextChanged += (_, _) =>
            tab.Header = string.IsNullOrWhiteSpace(nameBox.Text) ? ScoreItemSettings.DefaultName(index) : nameBox.Text.Trim();
        return editor;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static TextBox NumberBox(string text, int width = 50) =>
        new() { Width = width, Text = text, MaxLength = 4, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };

    private static TextBlock Label(string text, double left = 4) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 0, 0), Opacity = 0.7 };

    private static Button RemoveButton(string tip) =>
        new() { Content = "✕", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), ToolTip = tip };

    /// <summary>点数を入れる行（小項目と、小項目のない項目）。設定の並び</summary>
    private static List<Row> LeafRows(Editor e) =>
        e.Rows.Where(r => !r.Adjust).SelectMany(r => r.IsGroup ? r.Children : [r]).ToList();

    /// <summary>項目の行を足す（調整点の行があれば、その前に置く）</summary>
    private Row AddTopRow(Editor editor, string id, string name, string max, string basePoint)
    {
        var nameBox = new TextBox { Width = 170, Text = name, MaxLength = 20, VerticalContentAlignment = VerticalAlignment.Center };
        var maxBox = NumberBox(max);
        var groupMax = new TextBlock { Width = 50, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Visibility = Visibility.Collapsed, ToolTip = "小項目の最高点の合計" };
        var baseLabel = Label("基準点", 10);
        var baseBox = NumberBox(basePoint, 56);
        baseBox.ToolTip = "基準点（任意）。詳細欄の「基準点を入力」で、空欄の項目に入る点数です。0〜最高点";
        var addChild = new Button { Content = "＋小項目", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(6, 0, 6, 0), ToolTip = "この項目の下に小項目を付けます" };
        var remove = RemoveButton("この項目を削除（小項目も消えます）");
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        line.Children.Add(nameBox);
        line.Children.Add(maxBox);
        line.Children.Add(groupMax);
        line.Children.Add(Label("点満点"));
        line.Children.Add(baseLabel);
        line.Children.Add(baseBox);
        line.Children.Add(addChild);
        line.Children.Add(remove);
        var childHost = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(line);
        panel.Children.Add(childHost);
        var row = new Row
        {
            Id = id, Adjust = false, NameBox = nameBox, MaxBox = maxBox, BaseBox = baseBox, GroupMaxText = groupMax,
            BaseParts = [baseLabel, baseBox], Panel = panel, ChildHost = childHost,
        };
        nameBox.TextChanged += (_, _) => Update(editor);
        maxBox.TextChanged += (_, _) => Update(editor);
        baseBox.TextChanged += (_, _) => Update(editor);
        addChild.Click += (_, _) =>
        {
            // 初めての小項目には、項目の最高点を引き継ぐ（合計 100 が崩れないように）
            var first = row.Children.Count == 0;
            var child = AddChildRow(editor, row, NewId(), "", first ? maxBox.Text : "", "");
            Update(editor);
            child.NameBox.Focus();
        };
        remove.Click += (_, _) =>
        {
            editor.Rows.Remove(row);
            editor.RowsHost.Children.Remove(panel);
            Update(editor);
        };
        var adjustAt = editor.Rows.FindIndex(r => r.Adjust);
        if (adjustAt >= 0)
        {
            editor.Rows.Insert(adjustAt, row);
            editor.RowsHost.Children.Insert(editor.RowsHost.Children.IndexOf(editor.Rows[adjustAt + 1].Panel), panel);
        }
        else
        {
            editor.Rows.Add(row);
            editor.RowsHost.Children.Add(panel);
        }
        return row;
    }

    private Row AddChildRow(Editor editor, Row parent, string id, string name, string max, string basePoint)
    {
        var nameBox = new TextBox { Width = 146, Text = name, MaxLength = 20, VerticalContentAlignment = VerticalAlignment.Center };
        var maxBox = NumberBox(max);
        var baseLabel = Label("基準点", 10);
        var baseBox = NumberBox(basePoint, 56);
        baseBox.ToolTip = "基準点（任意）。詳細欄の「基準点を入力」で、空欄の小項目に入る点数です。0〜最高点";
        var remove = RemoveButton("この小項目を削除");
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        line.Children.Add(new TextBlock { Text = "└", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.5, Width = 14 });
        line.Children.Add(nameBox);
        line.Children.Add(maxBox);
        line.Children.Add(Label("点満点"));
        line.Children.Add(baseLabel);
        line.Children.Add(baseBox);
        line.Children.Add(remove);
        var panel = new StackPanel();
        panel.Children.Add(line);
        var row = new Row { Id = id, Adjust = false, NameBox = nameBox, MaxBox = maxBox, BaseBox = baseBox, BaseParts = [baseLabel, baseBox], Panel = panel, Parent = parent };
        nameBox.TextChanged += (_, _) => Update(editor);
        maxBox.TextChanged += (_, _) => Update(editor);
        baseBox.TextChanged += (_, _) => Update(editor);
        remove.Click += (_, _) =>
        {
            parent.Children.Remove(row);
            parent.ChildHost!.Children.Remove(panel);
            // 最後の小項目を消したら、ふつうの項目に戻す（最高点はその小項目のものを引き継ぐ）
            if (parent.Children.Count == 0) parent.MaxBox.Text = maxBox.Text;
            Update(editor);
        };
        parent.Children.Add(row);
        parent.ChildHost!.Children.Add(panel);
        return row;
    }

    private Row AddAdjustRow(Editor editor, string id, string name, string min, string max, string basePoint)
    {
        var nameBox = new TextBox { Width = 170, Text = name, MaxLength = 20, VerticalContentAlignment = VerticalAlignment.Center };
        var minBox = NumberBox(min);
        minBox.ToolTip = $"下限（-{ScoreItemRules.AdjustLimit}～0。マイナスにすると減点できます）";
        var maxBox = NumberBox(max);
        maxBox.ToolTip = $"上限（0～{ScoreItemRules.AdjustLimit}）";
        var baseLabel = Label("基準点", 10);
        var baseBox = NumberBox(basePoint, 56);
        baseBox.ToolTip = "基準点（任意）。詳細欄の「基準点を入力」で、空欄の調整点に入る点数です。範囲の中の整数";
        var remove = RemoveButton("調整点を削除");
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 3) };
        line.Children.Add(nameBox);
        line.Children.Add(minBox);
        line.Children.Add(Label("～", 2));
        line.Children.Add(maxBox);
        line.Children.Add(Label("点（調整点）"));
        line.Children.Add(baseLabel);
        line.Children.Add(baseBox);
        line.Children.Add(remove);
        var panel = new StackPanel();
        panel.Children.Add(line);
        var row = new Row { Id = id, Adjust = true, NameBox = nameBox, MaxBox = maxBox, MinBox = minBox, BaseBox = baseBox, BaseParts = [baseLabel, baseBox], Panel = panel };
        nameBox.TextChanged += (_, _) => Update(editor);
        minBox.TextChanged += (_, _) => Update(editor);
        maxBox.TextChanged += (_, _) => Update(editor);
        baseBox.TextChanged += (_, _) => Update(editor);
        remove.Click += (_, _) =>
        {
            editor.Rows.Remove(row);
            editor.RowsHost.Children.Remove(panel);
            Update(editor);
        };
        editor.Rows.Add(row);
        editor.RowsHost.Children.Add(panel);
        return row;
    }

    /// <summary>設定の内容を画面に写す</summary>
    private void Refresh()
    {
        _enabled.IsChecked = _cfg.ScoreItemsEnabled;
        foreach (var e in _editors)
        {
            e.NameBox.Text = e.Index < _cfg.ScoreTemplateNames.Count ? _cfg.ScoreTemplateNames[e.Index] : "";
            foreach (var item in _cfg.ScoreItemsOf(e.Index))
            {
                if (item == null) continue;
                if (item.Adjust)
                {
                    AddAdjustRow(e, item.Id, item.Name, item.Min.ToString(), item.Max.ToString(), item.Base?.ToString() ?? "");
                    continue;
                }
                var row = AddTopRow(e, item.Id, item.Name, item.Max.ToString(), item.Base?.ToString() ?? "");
                foreach (var child in item.Children ?? [])
                    if (child != null) AddChildRow(e, row, child.Id, child.Name, child.Max.ToString(), child.Base?.ToString() ?? "");
            }
            Update(e);
        }
    }

    private static int ParseNumber(TextBox box) => int.TryParse(box.Text.Trim(), out var v) ? v : NotANumber;

    private static int? ParseBase(TextBox box) => box.Text.Trim() == "" ? null : ParseNumber(box);

    /// <summary>編集中の項目（数でない欄は範囲の外の値にして、検査でエラーにする）。大項目の最高点は小項目の合計にする</summary>
    private static List<ScoreItem> Draft(Editor e) =>
        e.Rows.Select(r =>
        {
            if (r.Adjust)
                return new ScoreItem { Id = r.Id, Name = r.NameBox.Text.Trim(), Adjust = true, Min = ParseNumber(r.MinBox!), Max = ParseNumber(r.MaxBox), Base = ParseBase(r.BaseBox) };
            if (r.IsGroup)
            {
                var children = r.Children.Select(c => new ScoreItem { Id = c.Id, Name = c.NameBox.Text.Trim(), Max = ParseNumber(c.MaxBox), Base = ParseBase(c.BaseBox) }).ToList();
                var group = new ScoreItem { Id = r.Id, Name = r.NameBox.Text.Trim(), Children = children };
                group.Max = children.All(c => c.Max != NotANumber) ? ScoreItemRules.GroupMax(group) : 0;
                return group;
            }
            return new ScoreItem { Id = r.Id, Name = r.NameBox.Text.Trim(), Max = ParseNumber(r.MaxBox), Base = ParseBase(r.BaseBox) };
        }).ToList();

    /// <summary>項目が 1 つもない基準は「使わない」ので、問題なしとして扱う</summary>
    private static string? ErrorOf(Editor e) => e.Rows.Count == 0 ? null : ScoreItemRules.ValidateTemplate(Draft(e));

    private void Update(Editor e)
    {
        var draft = Draft(e);
        // 大項目の行は、最高点を小項目の合計の表示にして、基準点の欄を隠す
        foreach (var r in e.Rows.Where(r => !r.Adjust))
        {
            r.MaxBox.Visibility = r.IsGroup ? Visibility.Collapsed : Visibility.Visible;
            r.GroupMaxText!.Visibility = r.IsGroup ? Visibility.Visible : Visibility.Collapsed;
            foreach (var part in r.BaseParts) part.Visibility = r.IsGroup ? Visibility.Hidden : Visibility.Visible;
            if (r.IsGroup)
            {
                var g = draft.First(d => d.Id == r.Id);
                r.GroupMaxText.Text = g.Max > 0 ? g.Max.ToString() : "?";
            }
        }
        e.AddAdjust.IsEnabled = !e.Rows.Any(r => r.Adjust);

        if (e.Rows.Count == 0)
        {
            e.Sum.Text = "未使用（項目がありません）";
            e.Sum.Foreground = Brushes.Gray;
            e.Error.Text = "";
        }
        else
        {
            var sum = ScoreItemRules.Sum(draft.Where(d => !d.Adjust && (d.IsGroup ? d.Children!.All(c => c.Max != NotANumber) : d.Max != NotANumber)));
            var remain = ScoreItemRules.TotalMax - sum;
            var error = ScoreItemRules.ValidateTemplate(draft);
            var fields = ScoreItemRules.Fields(draft);
            var adjust = ScoreItemRules.AdjustField(draft);
            e.Sum.Text = $"最高点の合計：{sum} / {ScoreItemRules.TotalMax}" + (remain > 0 ? $"（あと {remain}）" : remain < 0 ? $"（{-remain} 超過）" : "")
                         + (adjust != null && adjust.Min != NotANumber && adjust.Max != NotANumber ? $"　／　調整点：{adjust.Min}～+{adjust.Max}" : "")
                         + (ScoreItemRules.HasBase(draft) && fields.All(f => f.Item.Base != NotANumber) ? $"　／　基準点の合計：{ScoreItemRules.BaseSum(draft)}" : "");
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
                                  "・一覧の「得点」が赤く囲まれ、詳細欄では変わった項目が赤く表示されます\n" +
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
