using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 任意評価基準：設定（ScoreCriteriaWindow）の反映、詳細欄の項目別の採点、一覧の項目の列。
/// 基準は最大 3 つで、作品ごとに使う基準を詳細欄で選ぶ（Review.ScoreTemplate）。
/// 批評空間に送るのは、これまでどおり「自分の得点」（項目の合計）だけ。項目別の点数はツールにだけ保存する
/// </summary>
public partial class MainWindow
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B));

    // ================================================================
    // 起動時と、設定の反映
    // ================================================================

    /// <summary>設定の内容を、表示から参照する置き場（ScoreItemSettings）に写す</summary>
    private void ApplyScoreItemSettings()
    {
        // 検査に通らない基準は、使えない（空）として扱う
        var templates = Enumerable.Range(0, ScoreItemSettings.TemplateCount)
            .Select(i => (IReadOnlyList<ScoreItem>)(ScoreItemRules.ValidateTemplate(_cfg.ScoreItemsOf(i)) == null
                ? _cfg.ScoreItemsOf(i).ToList()
                : new List<ScoreItem>()))
            .ToList();
        ScoreItemSettings.Templates = templates;
        ScoreItemSettings.Names = Enumerable.Range(0, ScoreItemSettings.TemplateCount).Select(_cfg.ScoreTemplateName).ToList();
        ScoreItemSettings.Enabled = _cfg.ScoreItemsEnabled && templates.Any(t => t.Count > 0);
    }

    private void InitScoreItems()
    {
        ApplyScoreItemSettings();
        SyncScoreItemsCheck();
    }

    private void SyncScoreItemsCheck() => SettingsView.SetScoreItemsCheck(ScoreItemSettings.Enabled);

    // ================================================================
    // 設定：評価基準のウィンドウと、オン・オフ
    // ================================================================

    /// <summary>評価基準（最大3つ）の設定ウィンドウを開く（詳細設定タブと、詳細欄のボタンから）</summary>
    private void ScoreCriteria_Click(object sender, RoutedEventArgs e)
    {
        var win = new ScoreCriteriaWindow(this, _cfg, () => _db.GetAll(), ConfirmDiscardDetailForCriteria);
        win.ShowDialog();
        if (!win.Changed) return;
        ApplyScoreItemSettings();
        SyncScoreItemsCheck();
        RefreshAfterScoreItemsChanged();
        StatusText.Text = "任意評価基準を保存しました";
    }

    /// <summary>基準を保存すると詳細欄を読み直すので、未保存の変更があれば破棄してよいか聞く</summary>
    private bool ConfirmDiscardDetailForCriteria() =>
        !(_detailDirty && ScoreItemSettings.Enabled) ||
        MessageBox.Show(this, "詳細欄に、保存していない変更があります。\n基準を保存すると、その変更は破棄されます。続けますか？",
            "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>「項目ごとの採点を使う」を切り替える（詳細設定タブの☑から）。切り替えた後の状態を返す（やめたときは元の状態）</summary>
    private bool SetScoreItemsEnabled(bool on)
    {
        if (on && Enumerable.Range(0, ScoreItemSettings.TemplateCount).All(i => ScoreItemRules.ValidateTemplate(_cfg.ScoreItemsOf(i)) != null))
        {
            MessageBox.Show(this, "先に「評価基準を設定…」で、項目を設定して保存してください。", "任意評価基準");
            return false;
        }
        if (_detailDirty &&
            MessageBox.Show(this, "詳細欄に、保存していない変更があります。\n切り替えると、その変更は破棄されます。続けますか？",
                "未保存の変更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return !on;
        }
        _cfg.ScoreItemsEnabled = on;
        _cfg.Save();
        ApplyScoreItemSettings();
        RefreshAfterScoreItemsChanged();
        StatusText.Text = on ? "項目ごとの採点を使います" : "項目ごとの採点をオフにしました（項目の設定と点数は残してあります）";
        return on;
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
    private bool _loadingTemplateBox;
    /// <summary>詳細欄に出している作品の、採点に使う基準の番号</summary>
    private int _detailTemplate;

    /// <summary>詳細欄で採点に使っている基準の項目</summary>
    private IReadOnlyList<ScoreItem> DetailScoreItems => ScoreItemSettings.ItemsFor(_detailTemplate);

    /// <summary>作品を表示するときに、項目別の採点の欄を作る（オフのときは出さない）</summary>
    private void ShowScoreItems(Game? g)
    {
        ClearScoreRows();
        ScoreItemsNote.Visibility = Visibility.Collapsed;
        ScoreItemsClearButton.Visibility = Visibility.Collapsed;
        ScoreItemsBaseButton.Visibility = Visibility.Collapsed;
        var on = ScoreItemSettings.Enabled && g != null;
        ScoreItemsPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ScoreItemsOpenButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible; // オフのときも、設定の入口は出す
        ScoreBox.IsReadOnly = false;
        if (!on) return;

        var review = g!.Review;
        _detailTemplate = ScoreItemSettings.TemplateOf(review);
        FillTemplateBox();
        BuildScoreRows(review, carry: null);

        // 項目別の採点がある作品は、得点欄を合計の表示専用にする
        var items = DetailScoreItems;
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
        UpdateBaseButton();
    }

    private void ClearScoreRows()
    {
        ScoreItemRowsDetail.Children.Clear();
        _detailScoreBoxes.Clear();
        _detailScoreErrors.Clear();
        _detailScoreTouched.Clear();
    }

    /// <summary>基準の選択欄を作る（使える基準が 2 つ以上のときだけ出す）</summary>
    private void FillTemplateBox()
    {
        _loadingTemplateBox = true;
        ScoreTemplateBox.Items.Clear();
        var usable = ScoreItemSettings.UsableTemplates();
        foreach (var i in usable)
            ScoreTemplateBox.Items.Add(new ComboBoxItem { Content = ScoreItemSettings.NameOf(i), Tag = i });
        ScoreTemplateBox.SelectedIndex = usable.IndexOf(_detailTemplate);
        ScoreTemplateRow.Visibility = usable.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        _loadingTemplateBox = false;
    }

    /// <summary>
    /// 採点の欄を作る。carry（項目の名前 → 入力した文字）があれば、同じ名前の項目に引き継ぐ（基準を切り替えたとき）。
    /// なければ、作品に保存してある点数を入れる
    /// </summary>
    private void BuildScoreRows(Review review, Dictionary<string, string>? carry)
    {
        var items = DetailScoreItems;
        var ownTemplate = _detailTemplate == ScoreItemSettings.TemplateOf(review);
        var stale = ownTemplate ? ScoreItemRules.StaleItems(items, review) : new HashSet<string>();
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
            var text = carry != null
                ? carry.GetValueOrDefault(item.Name) ?? ""
                : ownTemplate && review.ItemScores != null && review.ItemScores.TryGetValue(item.Id, out var v) ? v.ToString() : "";
            var box = new TextBox { Width = 100, Text = text, MaxLength = 3 };
            if (isStale) box.BorderBrush = ErrorBrush;
            var max = new TextBlock { Text = $"/ {item.Max}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Opacity = 0.7 };
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            Grid.SetColumn(box, 1);
            Grid.SetColumn(max, 2);
            grid.Children.Add(name);
            grid.Children.Add(box);
            grid.Children.Add(max);
            var error = new TextBlock { Foreground = ErrorBrush, FontSize = 12, TextAlignment = TextAlignment.Right, Visibility = Visibility.Collapsed };

            var id = item.Id;
            if (carry != null && text != "") _detailScoreTouched.Add(id);
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
    }

    /// <summary>この作品に使う基準を切り替える：同じ名前の項目の点数は引き継ぎ、ほかは空欄にする</summary>
    private void ScoreTemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTemplateBox || Selected is not { } g) return;
        if (ScoreTemplateBox.SelectedItem is not ComboBoxItem { Tag: int target } || target == _detailTemplate) return;

        var current = DetailScoreItems;
        var carry = new Dictionary<string, string>();
        foreach (var item in current)
            if (_detailScoreBoxes.TryGetValue(item.Id, out var box) && box.Text.Trim() != "") carry[item.Name] = box.Text.Trim();
        if (carry.Count > 0)
        {
            var lost = current.Count(i => carry.ContainsKey(i.Name) && ScoreItemSettings.ItemsFor(target).All(t => t.Name != i.Name));
            var msg = "基準を切り替えます。同じ名前の項目の点数は引き継ぎ、ほかの項目は空欄になります。" +
                      (lost > 0 ? $"\n（引き継げない点数が {lost} 件あります）" : "") + "\n\n切り替えますか？";
            if (MessageBox.Show(this, msg, "評価基準の切り替え", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            {
                _loadingTemplateBox = true;
                ScoreTemplateBox.SelectedIndex = ScoreItemSettings.UsableTemplates().IndexOf(_detailTemplate);
                _loadingTemplateBox = false;
                return;
            }
        }

        _detailTemplate = target;
        ClearScoreRows();
        BuildScoreRows(g.Review, carry);
        ScoreItemsNote.Visibility = Visibility.Collapsed;
        ScoreItemsClearButton.Visibility = Visibility.Collapsed;
        UpdateBaseButton();
        if (!_loadingDetail) _detailDirty = true;
        UpdateDetailScoreTotal(showMissing: false);
    }

    private ScoreCheck ReadScoreCheck() =>
        ScoreItemRules.Check(DetailScoreItems, _detailScoreBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text));

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

    /// <summary>基準点がある基準を使っているときだけ、「基準点を入力」を出す</summary>
    private void UpdateBaseButton() =>
        ScoreItemsBaseButton.Visibility = ScoreItemSettings.Enabled && ScoreItemRules.HasBase(DetailScoreItems) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>空欄の項目に基準点を入れる（入力済みの項目は変えない）。保存するまで確定しない</summary>
    private void ScoreItemsBase_Click(object sender, RoutedEventArgs e)
    {
        var fill = ScoreItemRules.BaseFill(DetailScoreItems, _detailScoreBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text));
        if (fill.Count == 0)
        {
            StatusText.Text = "空欄の項目がないので、基準点は入れませんでした";
            return;
        }
        _buildingScoreRows = true;
        foreach (var (id, point) in fill) _detailScoreBoxes[id].Text = point.ToString();
        _buildingScoreRows = false;
        foreach (var id in fill.Keys) _detailScoreTouched.Add(id);
        _detailDirty = true;
        UpdateDetailScoreTotal(showMissing: false);
        StatusText.Text = $"{fill.Count} 項目に基準点を入れました。必要なら手動で直して、保存してください";
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
        ScoreItemRules.Apply(review, DetailScoreItems, check, _detailScoreTouched);
        review.ScoreTemplate = _detailTemplate;
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
