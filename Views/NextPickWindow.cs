using System.Windows;
using System.Windows.Controls;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// 「次の1本」：批評空間の中央値・データ数・POV（第一・第二）・ジャンル・総プレイ時間で、未プレイ・プレイ中の作品を絞り込み、
/// 一覧から選ぶか、ランダムに1本選ぶ。よく使う条件は3つまで保存できる。
/// 絞り込みの判定は TraitMatcher（画面に触れない）に任せ、ここは画面だけを受け持つ
/// </summary>
public class NextPickWindow : Window
{
    private sealed record Row(Game Game, string Text);

    private sealed class PovBox
    {
        public readonly TextBox Search = new();
        public readonly ListBox List = new() { SelectionMode = SelectionMode.Single };
        public string? Selected;
        public List<(string Name, int Count)> Choices = new();
    }

    private readonly EgsClient _egs;
    private readonly EgsConfig _cfg;
    private readonly Func<List<Game>> _loadGames;
    private readonly Action<List<Game>> _addToPriority;
    private Dictionary<int, GameTraits> _traits = TraitStore.Load();
    private List<Game> _games = new();
    private bool _loading = true;
    private bool _fetching;

    private readonly TextBox _minMedian = Num(46);
    private readonly TextBox _minCount = Num(46);
    private readonly TextBox _maxHours = Num(46);
    private readonly CheckBox _unplayed = new() { Content = "未プレイ", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _playing = new() { Content = "プレイ中", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _genre = new() { Width = 160 };
    private readonly PovBox _pov1 = new();
    private readonly PovBox _pov2 = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _results = new() { DisplayMemberPath = nameof(Row.Text), SelectionMode = SelectionMode.Extended };
    private readonly Button _fetchButton = new() { Content = "特徴を取得・更新", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 4) };
    private readonly ComboBox _slot = new() { Width = 120 };

    private static TextBox Num(double width) => new() { Width = width, HorizontalContentAlignment = HorizontalAlignment.Center };

    public NextPickWindow(Window owner, EgsClient egs, EgsConfig cfg, Func<List<Game>> loadGames, Action<List<Game>> addToPriority)
    {
        _egs = egs; _cfg = cfg; _loadGames = loadGames; _addToPriority = addToPriority;
        Owner = owner;
        Title = "次の1本を選ぶ";
        Width = 860; Height = 740; MinWidth = 640; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _cfg.NormalizeSortPresets();
        Content = BuildUi();
        _games = _loadGames();
        Apply(_cfg.NextPickFilter);
        _loading = false;
        Closing += (_, _) => { _cfg.NextPickFilter = ReadFilter(); _cfg.Save(); };
        Refresh();
    }

    private UIElement BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        var head = new TextBlock
        {
            Text = "批評空間の情報で絞り込んで、次に遊ぶ作品を選びます。POV・ジャンル・総プレイ時間は、初回に「特徴を取得・更新」で読み込みます（作品が多いと少し時間がかかります）。",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ---- 条件（1行目：中央値・データ数・状態、2行目：総プレイ時間・ジャンル） ----
        var row1 = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        row1.Children.Add(Label("中央値"));
        row1.Children.Add(_minMedian);
        row1.Children.Add(Label("以上　データ数"));
        row1.Children.Add(_minCount);
        row1.Children.Add(Label("以上"));
        row1.Children.Add(_unplayed);
        row1.Children.Add(_playing);
        var row2 = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        row2.Children.Add(Label("総プレイ時間"));
        row2.Children.Add(_maxHours);
        row2.Children.Add(Label("時間以下　　ジャンルに含む文字"));
        row2.Children.Add(_genre);
        var reset = new Button { Content = "すべてリセット", Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), ToolTip = "条件をすべて初期値（中央値80以上・未プレイとプレイ中）に戻します" };
        reset.Click += (_, _) => { Apply(new TraitFilter()); Refresh(); };
        row2.Children.Add(reset);
        var top = new StackPanel();
        top.Children.Add(row1);
        top.Children.Add(row2);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ---- 下：操作ボタンと保存した条件 ----
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var actions = new WrapPanel();
        var random = new Button { Content = "🎲 ランダムに1本", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 4) };
        var addPriority = new Button
        {
            Content = "優先したい作品に追加", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 4),
            ToolTip = "選んだ作品（複数選択できます）を、投稿設定の「今後優先したい作品」に入れます",
        };
        actions.Children.Add(random);
        actions.Children.Add(addPriority);
        actions.Children.Add(_fetchButton);

        var presets = new WrapPanel();
        presets.Children.Add(new TextBlock { Text = "保存した条件：", VerticalAlignment = VerticalAlignment.Center });
        presets.Children.Add(_slot);
        var load = new Button { Content = "呼び出す", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        var save = new Button { Content = "いまの条件を保存", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        var clear = new Button { Content = "解除", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
        presets.Children.Add(load);
        presets.Children.Add(save);
        presets.Children.Add(clear);
        bottom.Children.Add(actions);
        bottom.Children.Add(presets);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // ---- 真ん中：POV（第一・第二）と、結果 ----
        var middle = new Grid();
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star), MinHeight = 120 });
        middle.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        middle.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 120 });
        var names = new Grid();
        names.ColumnDefinitions.Add(new ColumnDefinition());
        names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        names.ColumnDefinitions.Add(new ColumnDefinition());
        var p1 = PovPanel("第一条件のPOV", _pov1);
        var p2 = PovPanel("第二条件のPOV（第一と「かつ」）", _pov2);
        Grid.SetColumn(p2, 2);
        names.Children.Add(p1);
        names.Children.Add(p2);
        middle.Children.Add(names);
        Grid.SetRow(_status, 1);
        middle.Children.Add(_status);
        Grid.SetRow(_results, 2);
        middle.Children.Add(_results);
        root.Children.Add(middle);

        // ---- 動き ----
        void Changed(object? s, EventArgs e) { if (!_loading) Refresh(); }
        _minMedian.TextChanged += Changed;
        _minCount.TextChanged += Changed;
        _maxHours.TextChanged += Changed;
        _genre.TextChanged += Changed;
        _unplayed.Click += Changed;
        _playing.Click += Changed;
        _fetchButton.Click += async (_, _) => await FetchAsync();
        random.Click += (_, _) => PickRandom();
        addPriority.Click += (_, _) => AddToPriority();
        load.Click += (_, _) => LoadSlot();
        save.Click += (_, _) => SaveSlot();
        clear.Click += (_, _) => ClearSlot();
        RefreshSlots(select: 0);
        return root;
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    /// <summary>POV を1つ選ぶ欄：見出しと「クリア」、検索、一覧。選んでいるものをもう一度クリックしても外せる</summary>
    private UIElement PovPanel(string title, PovBox box)
    {
        var dock = new DockPanel();
        var headRow = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var clear = new Button { Content = "クリア", Padding = new Thickness(10, 1, 10, 1), ToolTip = "この欄の選択を外します" };
        DockPanel.SetDock(clear, Dock.Right);
        headRow.Children.Add(clear);
        headRow.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        DockPanel.SetDock(headRow, Dock.Top);
        box.Search.ToolTip = "名前で絞り込み（表示だけ。選択は保たれます）";
        DockPanel.SetDock(box.Search, Dock.Top);
        dock.Children.Add(headRow);
        dock.Children.Add(box.Search);
        box.List.Margin = new Thickness(0, 4, 0, 0);
        dock.Children.Add(box.List);

        clear.Click += (_, _) => { box.Selected = null; Refresh(); };
        box.Search.TextChanged += (_, _) => { if (!_loading) FillPovList(box); };
        box.List.SelectionChanged += (_, _) =>
        {
            if (_loading || box.List.SelectedItem is not ListBoxItem { Tag: string name }) return;
            box.Selected = name;
            Refresh();
        };
        // 選んでいるものをもう一度クリックすると外れる
        box.List.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(box.List, (DependencyObject)e.OriginalSource) is ListBoxItem { Tag: string name } && name == box.Selected)
            {
                box.Selected = null;
                e.Handled = true;
                Refresh();
            }
        };
        return dock;
    }

    // ================================================================
    // 条件 ⇄ 画面
    // ================================================================

    private void Apply(TraitFilter f)
    {
        var was = _loading;
        _loading = true;
        _minMedian.Text = f.MinMedian?.ToString() ?? "";
        _minCount.Text = f.MinDataCount?.ToString() ?? "";
        _maxHours.Text = f.MaxPlayHours?.ToString() ?? "";
        _unplayed.IsChecked = f.IncludeUnplayed;
        _playing.IsChecked = f.IncludePlaying;
        _genre.Text = f.Genre;
        _pov1.Selected = f.Pov1;
        _pov2.Selected = f.Pov2;
        _pov1.Search.Text = _pov2.Search.Text = "";
        _loading = was;
    }

    private TraitFilter ReadFilter()
    {
        int? Number(TextBox t) => int.TryParse(t.Text.Trim(), out var v) && v >= 0 ? v : null;
        return new TraitFilter
        {
            MinMedian = Number(_minMedian),
            MinDataCount = Number(_minCount),
            MaxPlayHours = Number(_maxHours),
            IncludeUnplayed = _unplayed.IsChecked == true,
            IncludePlaying = _playing.IsChecked == true,
            Genre = _genre.Text.Trim(),
            Pov1 = _pov1.Selected,
            Pov2 = _pov2.Selected,
        };
    }

    private void FillPovList(PovBox box)
    {
        var text = box.Search.Text.Trim();
        var was = _loading;
        _loading = true;
        box.List.Items.Clear();
        foreach (var (name, count) in box.Choices)
        {
            if (text.Length > 0 && name != box.Selected && !name.Contains(text, StringComparison.CurrentCultureIgnoreCase)) continue;
            var item = new ListBoxItem { Content = $"{name}（{count}）", Tag = name, Opacity = count == 0 ? 0.5 : 1 };
            box.List.Items.Add(item);
            if (name == box.Selected) { item.IsSelected = true; }
        }
        _loading = was;
    }

    private void Refresh()
    {
        var f = ReadFilter();
        // 一方を選ぶと、もう一方の選択肢の本数が変わる（0本のものは薄く表示する）
        _pov1.Choices = TraitMatcher.PovChoices(_games, _traits, f, second: false);
        _pov2.Choices = TraitMatcher.PovChoices(_games, _traits, f, second: true);
        FillPovList(_pov1);
        FillPovList(_pov2);

        var list = TraitMatcher.Select(_games, _traits, f);
        _results.ItemsSource = list.Select(g => new Row(g, Describe(g, f))).ToList();

        var baseGames = _games.Where(g => TraitMatcher.IsBase(g, f)).ToList();
        var missing = baseGames.Count(g => !_traits.ContainsKey(g.EgsId!.Value));
        _status.Text = $"条件に合う作品：{list.Count} 本（中央値などの条件に合う未プレイ・プレイ中：{baseGames.Count} 本）"
            + (f.NeedsTraits && missing > 0 ? $"　※特徴を取得していない作品が {missing} 本あります。「特徴を取得・更新」を押してください" : "");
        _fetchButton.IsEnabled = !_fetching;
    }

    private string Describe(Game g, TraitFilter f)
    {
        var t = _traits.GetValueOrDefault(g.EgsId!.Value);
        var hits = string.Join("・", new[] { f.Pov1, f.Pov2 }.Where(p => p != null));
        var hours = t?.PlayHours is int h ? $"　約{h}時間" : "";
        return $"[{(g.Median is int m ? m.ToString() : "-")}] {g.Title}{(g.Status == GameStatus.Playing ? "（プレイ中）" : "")}{hours}{(hits == "" ? "" : "　" + hits)}";
    }

    // ================================================================
    // 取得・ランダム・優先リスト
    // ================================================================

    private async Task FetchAsync()
    {
        if (_fetching) return;
        var f = ReadFilter();
        var need = TraitStore.NeedFetch(_games.Where(g => TraitMatcher.IsBase(g, f)), _traits, DateTime.Now);
        if (need.Count == 0) { _status.Text = "特徴は取得済みです（" + TraitStore.StaleDays + "日より古いものだけ更新します）"; return; }
        _fetching = true;
        _fetchButton.IsEnabled = false;
        try
        {
            var got = await _egs.GetTraitsAsync(need, (done, all) => Dispatcher.Invoke(() => _status.Text = $"特徴を取得しています… {done} / {all} 本"));
            foreach (var t in got) _traits[t.EgsId] = t;
            TraitStore.Save(_traits.Values);
            _fetching = false;
            Refresh();
            _status.Text = $"{got.Count} 本の特徴を取得しました。　" + _status.Text;
        }
        catch (Exception ex)
        {
            Log.Warn("作品の特徴の取得に失敗しました", ex);
            _fetching = false;
            _fetchButton.IsEnabled = true;
            MessageBox.Show(this, "特徴を取得できませんでした。\n" + ex.Message, "次の1本", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PickRandom()
    {
        if (_results.ItemsSource is not List<Row> rows || rows.Count == 0)
        {
            MessageBox.Show(this, "条件に合う作品がありません。条件をゆるめてください。", "次の1本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var pick = rows[Random.Shared.Next(rows.Count)];
        _results.SelectedItem = pick;
        _results.ScrollIntoView(pick);
        _status.Text = $"次の1本：{pick.Game.Title}（{rows.Count} 本の中から選びました）";
    }

    private void AddToPriority()
    {
        var picked = _results.SelectedItems.OfType<Row>().Select(r => r.Game).ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "一覧で作品を選んでください（複数選択できます）。", "次の1本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _addToPriority(picked);
        _status.Text = $"{picked.Count} 本を「今後優先したい作品」に入れました（最大{PostLayout.MaxPriorityGames}件）";
    }

    // ================================================================
    // 保存した条件（1〜3）
    // ================================================================

    private void RefreshSlots(int select)
    {
        _slot.ItemsSource = Enumerable.Range(0, EgsConfig.SortPresetCount)
            .Select(i => $"条件{i + 1}" + (_cfg.NextPickPresets[i] == null ? "（未設定）" : "")).ToList();
        _slot.SelectedIndex = select;
    }

    private void SaveSlot()
    {
        var i = Math.Max(0, _slot.SelectedIndex);
        _cfg.NextPickPresets[i] = ReadFilter();
        _cfg.Save();
        RefreshSlots(i);
        _status.Text = $"いまの条件を「条件{i + 1}」に保存しました";
    }

    private void LoadSlot()
    {
        var i = Math.Max(0, _slot.SelectedIndex);
        if (_cfg.NextPickPresets[i] is not { } f) { _status.Text = $"条件{i + 1}は未設定です"; return; }
        Apply(f.Clone());
        Refresh();
    }

    private void ClearSlot()
    {
        var i = Math.Max(0, _slot.SelectedIndex);
        if (_cfg.NextPickPresets[i] == null) return;
        if (MessageBox.Show(this, $"条件{i + 1}を解除しますか？", "次の1本", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _cfg.NextPickPresets[i] = null;
        _cfg.Save();
        RefreshSlots(i);
    }
}
