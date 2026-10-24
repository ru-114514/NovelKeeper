using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// 任意評価基準：設定タブの項目の編集、詳細欄の項目別の採点、一覧の項目の列。
/// 批評空間に送るのは、これまでどおり「自分の得点」（項目の合計）だけ。項目別の点数はツールにだけ保存する
/// </summary>
public partial class MainWindow
{
    private const string ScoreColumnPrefix = "採点:";
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));

    // ================================================================
    // 起動時と、設定の反映
    // ================================================================

    /// <summary>設定の内容を、表示から参照する置き場（ScoreItemSettings）に写す</summary>
    private void ApplyScoreItemSettings()
    {
        ScoreItemSettings.Enabled = _cfg.ScoreItemsEnabled && ScoreItemRules.ValidateTemplate(_cfg.ScoreItems) == null;
        ScoreItemSettings.Items = _cfg.ScoreItems.ToList();
    }

    private void InitScoreItems()
    {
        ApplyScoreItemSettings();
        _loadingSettings = true;
        ScoreItemsCheck.IsChecked = ScoreItemSettings.Enabled;
        _loadingSettings = false;
        LoadScoreItemEditor();
        RebuildScoreItemColumns();
    }

    // ================================================================
    // 設定タブ：項目の編集
    // ================================================================

    private sealed class ScoreItemRow
    {
        public required string Id { get; init; }
        public required TextBox NameBox { get; init; }
        public required TextBox MaxBox { get; init; }
    }

    private readonly List<ScoreItemRow> _scoreItemRows = new();
    private bool _scoreEditorLoading;

    private void LoadScoreItemEditor()
    {
        _scoreEditorLoading = true;
        ScoreItemRows.Children.Clear();
        _scoreItemRows.Clear();
        foreach (var item in _cfg.ScoreItems) AddScoreItemRow(item.Id, item.Name, item.Max.ToString());
        _scoreEditorLoading = false;
        UpdateScoreItemSum();
    }

    private void AddScoreItemRow(string id, string name, string max)
    {
        var nameBox = new TextBox { Width = 200, Text = name, MaxLength = 20, VerticalContentAlignment = VerticalAlignment.Center };
        var maxBox = new TextBox { Width = 50, Text = max, MaxLength = 3, Margin = new Thickness(6, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = "✕", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 0, 8, 0), ToolTip = "この項目を削除" };
        var row = new ScoreItemRow { Id = id, NameBox = nameBox, MaxBox = maxBox };
        nameBox.TextChanged += (_, _) => UpdateScoreItemSum();
        maxBox.TextChanged += (_, _) => UpdateScoreItemSum();
        remove.Click += (_, _) =>
        {
            _scoreItemRows.Remove(row);
            ScoreItemRows.Children.Remove((UIElement)remove.Parent);
            UpdateScoreItemSum();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        panel.Children.Add(nameBox);
        panel.Children.Add(maxBox);
        panel.Children.Add(new TextBlock { Text = "点満点", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Opacity = 0.7 });
        panel.Children.Add(remove);
        ScoreItemRows.Children.Add(panel);
        _scoreItemRows.Add(row);
    }

    /// <summary>編集中の項目（最高点が数字でないものは 0 として扱い、検査でエラーにする）</summary>
    private List<ScoreItem> ReadScoreItemDraft() =>
        _scoreItemRows.Select(r => new ScoreItem
        {
            Id = r.Id,
            Name = r.NameBox.Text.Trim(),
            Max = int.TryParse(r.MaxBox.Text.Trim(), out var m) ? m : 0,
        }).ToList();

    private void UpdateScoreItemSum()
    {
        if (_scoreEditorLoading) return;
        var draft = ReadScoreItemDraft();
        var sum = ScoreItemRules.Sum(draft);
        var remain = ScoreItemRules.TotalMax - sum;
        ScoreItemSum.Text = $"最高点の合計：{sum} / {ScoreItemRules.TotalMax}" + (remain > 0 ? $"（あと {remain}）" : remain < 0 ? $"（{-remain} 超過）" : "");
        var error = ScoreItemRules.ValidateTemplate(draft);
        ScoreItemError.Text = error ?? "";
        ScoreItemSum.Foreground = error == null ? Brushes.ForestGreen : ErrorBrush;
        ScoreItemSaveButton.IsEnabled = error == null;
    }

    private void ScoreItemAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_scoreItemRows.Count >= ScoreItemRules.MaxItems)
        {
            ScoreItemError.Text = $"項目は {ScoreItemRules.MaxItems} 個までです。";
            return;
        }
        AddScoreItemRow(Guid.NewGuid().ToString("N")[..8], "", "");
        UpdateScoreItemSum();
        _scoreItemRows[^1].NameBox.Focus();
    }

    private void ScoreItemDistribute_Click(object sender, RoutedEventArgs e)
    {
        var parts = ScoreItemRules.Distribute(_scoreItemRows.Count);
        _scoreEditorLoading = true;
        for (var i = 0; i < parts.Length; i++) _scoreItemRows[i].MaxBox.Text = parts[i].ToString();
        _scoreEditorLoading = false;
        UpdateScoreItemSum();
    }

    private void ScoreItemRevert_Click(object sender, RoutedEventArgs e) => LoadScoreItemEditor();

    private void ScoreItemSave_Click(object sender, RoutedEventArgs e)
    {
        var draft = ReadScoreItemDraft();
        if (ScoreItemRules.ValidateTemplate(draft) is { } error)
        {
            ScoreItemError.Text = error;
            return;
        }
        if (_detailDirty && ScoreItemSettings.Enabled &&
            MessageBox.Show(this, "詳細欄に、保存していない変更があります。\n基準を保存すると、その変更は破棄されます。続けますか？",
                "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // 基準を変えると、採点済みの作品のうち何本が「採点し直し」の対象になるかを知らせる（得点は変えない）
        var affected = _db.GetAll().Where(g => ScoreItemRules.NeedsRescore(draft, g.Review)).ToList();
        if (affected.Count > 0 &&
            MessageBox.Show(this, $"この基準では、項目別に採点済みの {affected.Count} 本が「採点し直し」の対象になります。\n\n" +
                                  "・各作品の得点は変わりません（批評空間にも送りません）\n" +
                                  "・一覧の項目の列で、変わった項目が赤く表示されます\n" +
                                  "・作品を開いて採点し直して保存すると、赤い表示が消えます\n\n保存しますか？",
                "任意評価基準", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _cfg.ScoreItems = draft;
        _cfg.Save();
        ApplyScoreItemSettings();
        LoadScoreItemEditor();
        RebuildScoreItemColumns();
        RefreshAfterScoreItemsChanged();
        StatusText.Text = "任意評価基準を保存しました";
    }

    private void ScoreItemsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var on = ScoreItemsCheck.IsChecked == true;
        if (on && ScoreItemRules.ValidateTemplate(_cfg.ScoreItems) is { } error)
        {
            ScoreItemsCheck.IsChecked = false;
            MessageBox.Show(this, "先に、下の項目を設定して「基準を保存」を押してください。\n\n" + error, "任意評価基準");
            return;
        }
        if (_detailDirty &&
            MessageBox.Show(this, "詳細欄に、保存していない変更があります。\n切り替えると、その変更は破棄されます。続けますか？",
                "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            ScoreItemsCheck.IsChecked = !on;
            return;
        }
        _cfg.ScoreItemsEnabled = on;
        _cfg.Save();
        ApplyScoreItemSettings();
        RebuildScoreItemColumns();
        RefreshAfterScoreItemsChanged();
        StatusText.Text = on ? "項目ごとの採点を使います" : "項目ごとの採点をオフにしました（項目の設定と点数は残してあります）";
    }

    private void RefreshAfterScoreItemsChanged()
    {
        _detailDirty = false;
        ShowDetail(Selected);
        GameGrid.Items.Refresh();
    }

    // ================================================================
    // 詳細欄：項目別の採点
    // ================================================================

    private readonly Dictionary<string, TextBox> _detailScoreBoxes = new();
    private readonly Dictionary<string, TextBlock> _detailScoreErrors = new();
    private readonly HashSet<string> _detailScoreTouched = new();
    private bool _buildingScoreRows;

    /// <summary>作品を表示するときに、項目別の採点の欄を作る（オフのときは出さない）</summary>
    private void ShowScoreItems(Game? g)
    {
        ScoreItemRowsDetail.Children.Clear();
        _detailScoreBoxes.Clear();
        _detailScoreErrors.Clear();
        _detailScoreTouched.Clear();
        ScoreItemsNote.Visibility = Visibility.Collapsed;
        ScoreItemsClearButton.Visibility = Visibility.Collapsed;
        var on = ScoreItemSettings.Enabled && g != null;
        ScoreItemsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ScoreBox.IsReadOnly = false;
        if (!on) return;

        var items = ScoreItemSettings.Items;
        var review = g!.Review;
        var stale = ScoreItemRules.StaleItems(items, review);
        _buildingScoreRows = true;
        foreach (var item in items)
        {
            var isStale = stale.Contains(item.Id);
            var name = new TextBlock
            {
                Text = item.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = isStale ? "採点基準が変わりました。採点し直してください" : item.Name,
            };
            if (isStale) { name.Foreground = ErrorBrush; name.FontWeight = FontWeights.Bold; }
            var box = new TextBox { Width = 100, Text = review.ItemScores != null && review.ItemScores.TryGetValue(item.Id, out var v) ? v.ToString() : "", MaxLength = 3 };
            if (isStale) box.BorderBrush = ErrorBrush;
            var max = new TextBlock { Text = $"/ {item.Max}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.7 };
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(box, 1);
            Grid.SetColumn(max, 2);
            grid.Children.Add(name);
            grid.Children.Add(box);
            grid.Children.Add(max);
            var error = new TextBlock { Foreground = ErrorBrush, FontSize = 12, TextAlignment = TextAlignment.Right, Visibility = Visibility.Collapsed };

            var id = item.Id;
            box.TextChanged += (_, _) =>
            {
                if (_buildingScoreRows) return;
                _detailScoreTouched.Add(id);
                if (!_loadingDetail) _detailDirty = true;
                UpdateDetailScoreTotal(showMissing: false);
            };
            ScoreItemRowsDetail.Children.Add(grid);
            ScoreItemRowsDetail.Children.Add(error);
            _detailScoreBoxes[item.Id] = box;
            _detailScoreErrors[item.Id] = error;
        }
        _buildingScoreRows = false;

        // 項目別の採点がある作品は、得点欄を合計の表示専用にする
        var has = ScoreItemRules.HasItemScores(review);
        ScoreBox.IsReadOnly = has;
        var notes = new List<string>();
        if (has && ScoreItemRules.TotalMismatch(review, g.MyScore))
            notes.Add($"項目の合計（{review.ItemScores!.Values.Sum()}）と得点（{g.MyScore?.ToString() ?? "未入力"}）が一致しません。" +
                      "項目を採点し直して保存すると、合計が得点になります。");
        if (has && ScoreItemRules.HasRemovedItems(items, review))
            notes.Add("設定から削除された項目の点数が残っています。保存すると、その点数は外れます。");
        if (notes.Count > 0)
        {
            ScoreItemsNote.Text = string.Join("\n", notes);
            ScoreItemsNote.Foreground = ErrorBrush;
            ScoreItemsNote.Visibility = Visibility.Visible;
        }
        ScoreItemsClearButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private ScoreCheck ReadScoreCheck() =>
        ScoreItemRules.Check(ScoreItemSettings.Items, _detailScoreBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text));

    /// <summary>入力に合わせて、合計を得点欄に入れ、エラーを赤字で出す</summary>
    private void UpdateDetailScoreTotal(bool showMissing)
    {
        if (!ScoreItemSettings.Enabled) return;
        var check = ReadScoreCheck();
        foreach (var (id, label) in _detailScoreErrors)
        {
            var msg = check.Errors.GetValueOrDefault(id);
            // 入力の途中で空欄のままの項目には、保存を押すまでエラーを出さない
            var show = msg != null && (showMissing || _detailScoreBoxes[id].Text.Trim() != "");
            label.Text = show ? msg! : "";
            label.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _detailScoreBoxes[id].BorderBrush = show ? ErrorBrush : SystemColors.ControlDarkBrush;
        }
        if (check.AnyEntered)
        {
            ScoreBox.IsReadOnly = true;
            ScoreBox.Text = check.Scores.Values.Sum().ToString();
        }
        else
        {
            ScoreBox.IsReadOnly = false;
        }
    }

    private void ScoreItemsClear_Click(object sender, RoutedEventArgs e)
    {
        _buildingScoreRows = true;
        foreach (var box in _detailScoreBoxes.Values) box.Text = "";
        _buildingScoreRows = false;
        foreach (var id in _detailScoreBoxes.Keys) _detailScoreTouched.Add(id);
        _detailDirty = true;
        ScoreItemsNote.Visibility = Visibility.Collapsed;
        ScoreItemsClearButton.Visibility = Visibility.Collapsed;
        UpdateDetailScoreTotal(showMissing: false);
        ScoreBox.IsReadOnly = false; // 得点欄に直接入力する状態に戻す（得点はそのまま）
    }

    /// <summary>
    /// 保存の前に、項目別の採点を検査する。エラーがあれば赤字で出して null を返す。
    /// オフのとき、または項目に何も入力していないときは、入力なし（ScoreCheck.AnyEntered が false）を返す
    /// </summary>
    private ScoreCheck? CheckScoreItemsForSave()
    {
        if (!ScoreItemSettings.Enabled) return new ScoreCheck();
        var check = ReadScoreCheck();
        UpdateDetailScoreTotal(showMissing: true);
        if (check.Ok) return check;
        StatusText.Text = "項目の採点にエラーがあります。赤字の項目を直してから保存してください";
        return null;
    }

    private void ApplyScoreItemsToReview(Review review, ScoreCheck check)
    {
        if (!ScoreItemSettings.Enabled) return; // オフの間は、項目別の点数をそのまま残す
        ScoreItemRules.Apply(review, ScoreItemSettings.Items, check, _detailScoreTouched);
    }

    // ================================================================
    // 一覧：項目の列と赤い表示
    // ================================================================
    // 実行中に列を足したり消したりすると、一覧の表示が壊れて例外になる（DataGrid の制限）。
    // そのため、項目の列は起動時に最大数ぶんを作っておき、使わない列は隠して、見出しと中身だけを入れ替える。

    private sealed class ScoreCellConverter : IValueConverter
    {
        public string ItemId { get; set; } = "";
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Game g && ScoreItemSettings.Items.FirstOrDefault(i => i.Id == ItemId) is { } item
                ? ScoreItemRules.CellText(item, g.Review) : "";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class ScoreStaleConverter : IValueConverter
    {
        public string ItemId { get; set; } = "";
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            ItemId != "" && value is Game g && ScoreItemSettings.Enabled &&
            ScoreItemRules.StaleItems(ScoreItemSettings.Items, g.Review).Contains(ItemId);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed record ScoreSlot(DataGridTextColumn Column, ScoreCellConverter Cell, ScoreStaleConverter Stale);

    private readonly List<ScoreSlot> _scoreSlots = new();

    /// <summary>項目の列（最大数ぶん）を、「得点」の列のすぐ後ろに作っておく（画面が出る前に1回だけ）</summary>
    private void EnsureScoreSlots()
    {
        if (_scoreSlots.Count > 0) return;
        var at = GameGrid.Columns.ToList().FindIndex(c => ColumnKey(c) == "得点") + 1;
        for (var i = 0; i < ScoreItemRules.MaxItems; i++)
        {
            var cell = new ScoreCellConverter();
            var stale = new ScoreStaleConverter();
            var column = new DataGridTextColumn
            {
                Header = "", Width = 80, CanUserSort = false, Visibility = Visibility.Collapsed,
                Binding = new Binding(".") { Converter = cell },
            };
            GameGrid.Columns.Insert(Math.Min(at + i, GameGrid.Columns.Count), column);
            _scoreSlots.Add(new ScoreSlot(column, cell, stale));
        }
    }

    /// <summary>オン／項目の変更に合わせて、一覧の「採点:項目名」の列の見出しと中身を入れ替える（初期は非表示。「一覧の列」で表示する）</summary>
    private void RebuildScoreItemColumns()
    {
        EnsureScoreSlots();
        var items = ScoreItemSettings.Enabled ? ScoreItemSettings.Items : Array.Empty<ScoreItem>();
        for (var i = 0; i < _scoreSlots.Count; i++)
        {
            var slot = _scoreSlots[i];
            var used = i < items.Count;
            slot.Cell.ItemId = used ? items[i].Id : "";
            slot.Stale.ItemId = used ? items[i].Id : "";
            slot.Column.Header = used ? ScoreColumnPrefix + items[i].Name : "";
            if (!used) slot.Column.Visibility = Visibility.Collapsed;
        }
        ApplyCategoryColors();
        ApplyColumnLayout();
        InitColumnList();
        GameGrid.Items.Refresh();
    }

    /// <summary>項目の列の赤い表示（基準が変わったのに、採点し直していない項目）</summary>
    private void ApplyScoreColumnStyles(Func<Style> rowStyle)
    {
        foreach (var slot in _scoreSlots)
        {
            var style = rowStyle();
            var red = new DataTrigger { Binding = new Binding(".") { Converter = slot.Stale }, Value = true };
            // 「得点」「起動設定」の欄と同じ、赤い枠と薄い赤の塗り
            red.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xE5, 0x53, 0x4B))));
            red.Setters.Add(new Setter(Control.BorderBrushProperty, ErrorBrush));
            red.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            red.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            red.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "登録後に基準を変更しています。採点し直してください。"));
            style.Triggers.Add(red);
            slot.Column.CellStyle = style;
        }
    }

    // ================================================================
    // 同期のあと：項目の合計と得点が食い違った作品を知らせる
    // ================================================================

    /// <summary>同期の前に、項目別の採点がある作品の得点を控える</summary>
    private Dictionary<long, int?> SnapshotScoredGames() =>
        ScoreItemSettings.Enabled
            ? _db.GetAll().Where(g => ScoreItemRules.HasItemScores(g.Review)).ToDictionary(g => g.Id, g => g.MyScore)
            : new Dictionary<long, int?>();

    /// <summary>同期で得点が変わり、項目の合計と食い違った作品を、一覧にして知らせる</summary>
    private void ReportScoreMismatches(Dictionary<long, int?> before)
    {
        if (before.Count == 0) return;
        var changed = _db.GetAll()
            .Where(g => before.TryGetValue(g.Id, out var old) && old != g.MyScore && ScoreItemRules.TotalMismatch(g.Review, g.MyScore))
            .ToList();
        if (changed.Count == 0) return;
        var lines = changed.Take(15).Select(g =>
            $"・{g.Title}（項目の合計 {g.Review.ItemScores!.Values.Sum()} → 得点 {g.MyScore?.ToString() ?? "未入力"}）");
        MessageBox.Show(this,
            $"批評空間の得点を取り込んだため、項目別の点数の合計と得点が食い違った作品が {changed.Count} 本あります。\n\n" +
            string.Join("\n", lines) + (changed.Count > 15 ? "\n…" : "") +
            "\n\n一覧の「得点」が赤く表示されます。作品を開いて、項目を採点し直して保存するか、「項目別の点数を消す」を押してください。",
            "任意評価基準", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
