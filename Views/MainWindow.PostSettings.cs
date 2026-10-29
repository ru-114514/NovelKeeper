using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>投稿設定タブ：パネルごとの設定欄</summary>
public partial class MainWindow
{

    // ---- 設定パネル ----

    private void OpenPostSettings(PostPanel panel)
    {
        PostDesignPanel.Visibility = Visibility.Collapsed; // 右の欄は、どちらか一方だけ
        _postEditing = panel;
        _postLoading = true;
        var layout = Layout;
        PostSetKindBox.ItemsSource = PostLayout.AllKinds.Select(PostLayout.DefaultTitle).ToList();
        PostSetKindBox.SelectedIndex = (int)panel.Kind;
        PostSetTitleBox.Text = panel.Title ?? "";
        PostSetRowBox.Text = (panel.Row + 1).ToString();
        PostSetColBox.ItemsSource = Enumerable.Range(1, layout.Columns).Select(i => $"{i} 列目").ToList();
        PostSetColBox.SelectedIndex = Math.Clamp(panel.Col, 0, layout.Columns - 1);
        PostSetSpanBox.ItemsSource = Enumerable.Range(1, layout.Columns).Select(i => $"横 {i} カラム").ToList();
        PostSetSpanBox.SelectedIndex = Math.Clamp(panel.Span - 1, 0, layout.Columns - 1);
        PostSetRowSpanBox.ItemsSource = Enumerable.Range(1, PostLayout.MaxRowSpan).Select(i => $"縦 {i} 段").ToList();
        PostSetRowSpanBox.SelectedIndex = Math.Clamp(panel.RowSpan, 1, PostLayout.MaxRowSpan) - 1;
        PostSetCountBox.Text = panel.ItemCount.ToString();
        PostSetItemColsBox.ItemsSource = new[] { "自動", "1", "2", "3", "4" };
        PostSetItemColsBox.SelectedIndex = Math.Clamp(panel.ItemColumns, 0, 4);
        PostSetViewColsBox.ItemsSource = new[] { "自動", "1", "2", "3", "4" };
        PostSetViewColsBox.SelectedIndex = Math.Clamp(panel.ViewColumns, 0, 4);
        PostSetAlignBox.ItemsSource = new[] { "左寄せ", "中央寄せ", "右寄せ" };
        PostSetAlignBox.SelectedIndex = Math.Clamp(panel.Align, 0, 2);
        PostSetTextBox.Text = panel.Text ?? "";
        PostSetFontBox.ItemsSource = PostFontNames();
        var fontIndex = string.IsNullOrWhiteSpace(panel.FontFamily) ? -1 : PostFontNames().IndexOf(panel.FontFamily);
        PostSetFontBox.SelectedIndex = Math.Max(0, fontIndex);
        PostSetColorBox.Text = panel.FontColor ?? "";
        PostSetFontSizeBox.Text = panel.FontSize > 0 ? panel.FontSize.ToString() : "";
        PostSetPeriodCheck.IsChecked = panel.ShowPeriod;
        PostSetBleedCheck.IsChecked = panel.FullBleed;
        PostSetTransFollow.IsChecked = panel.Transparency == null;
        PostSetTrans.Value = panel.Transparency ?? _cfg.PostPanelTransparency;
        PostSetTrans.IsEnabled = panel.Transparency != null;
        PostSetTransText.Text = $"{(int)PostSetTrans.Value}%";
        FillPostPicks(panel);
        ShowPostSettingsError(null);
        _postLoading = false;
        UpdatePostSettingsGroups();
        PostSettingsPanel.Visibility = Visibility.Visible;
    }

    private void PostSetClose_Click(object sender, RoutedEventArgs e)
    {
        _postEditing = null;
        PostSettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void PostSetKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_postLoading) UpdatePostSettingsGroups();
    }

    private List<string>? _postFontNames;

    /// <summary>フォントの選択肢（先頭は「標準」）。入れてある一覧を、名前順に</summary>
    private List<string> PostFontNames() => _postFontNames ??= new[] { "（標準）" }
        .Concat(Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCulture))
        .ToList();

    /// <summary>選んだ項目に合わせて、作品の表示・優先作品・文章・文字の設定を出し分ける。タイトルは、場所と横幅を決まった値にする</summary>
    private void UpdatePostSettingsGroups()
    {
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        PostSetItemsGroup.Visibility = PostLayout.UsesItems(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetItemColsLabel.Visibility = PostSetItemColsBox.Visibility = PostLayout.UsesItemColumns(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetViewColsGroup.Visibility = PostLayout.UsesViewColumns(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetAlignGroup.Visibility = PostLayout.UsesAlign(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetPriorityGroup.Visibility = kind == PostPanelKind.Priority ? Visibility.Visible : Visibility.Collapsed;
        PostSetTextGroup.Visibility = kind == PostPanelKind.Memo ? Visibility.Visible : Visibility.Collapsed;
        PostSetFontGroup.Visibility = PostLayout.UsesFont(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetPeriodCheck.Visibility = kind == PostPanelKind.Title ? Visibility.Visible : Visibility.Collapsed;
        PostSetTransGroup.Visibility = kind == PostPanelKind.Title ? Visibility.Visible : Visibility.Collapsed;
        var full = PostLayout.IsFullWidth(kind); // タイトルは、いつも全カラムの幅で左端
        PostSetColBox.IsEnabled = PostSetSpanBox.IsEnabled = !full;
        PostSetRowBox.IsEnabled = !PostLayout.IsLocked(kind); // 移動できないパネルは、段も変えられない
        if (full)
        {
            PostSetColBox.SelectedIndex = 0;
            PostSetSpanBox.SelectedIndex = Layout.Columns - 1;
        }
    }

    private void PostSetTransFollow_Click(object sender, RoutedEventArgs e) =>
        PostSetTrans.IsEnabled = PostSetTransFollow.IsChecked != true;

    private void PostSetTrans_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PostSetTransText != null) PostSetTransText.Text = $"{(int)e.NewValue}%";
    }

    private void PostSetSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) PostSetColorBox.Text = hex;
    }

    /// <summary>設定欄の入力から、測るための仮のパネルを作る（読み取れない欄は、いまの値のまま）</summary>
    private PostPanel PanelFromSettings(PostPanel current)
    {
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        var tmp = new PostPanel
        {
            Kind = kind,
            Title = PostSetTitleBox.Text.Trim(),
            Span = PostLayout.IsFullWidth(kind) ? Layout.Columns : Math.Max(1, PostSetSpanBox.SelectedIndex + 1),
            ItemCount = int.TryParse(PostSetCountBox.Text.Trim(), out var c) ? Math.Clamp(c, 1, PostLayout.MaxItems(kind)) : current.ItemCount,
            ItemColumns = Math.Max(0, PostSetItemColsBox.SelectedIndex),
            Text = PostSetTextBox.Text,
            ViewColumns = Math.Max(0, PostSetViewColsBox.SelectedIndex),
            Align = Math.Max(0, PostSetAlignBox.SelectedIndex),
            FontFamily = PostSetFontBox.SelectedIndex > 0 ? PostSetFontBox.SelectedItem as string : null,
            FontColor = PostSetColorBox.Text.Trim(),
            FontSize = int.TryParse(PostSetFontSizeBox.Text.Trim(), out var fs) ? Math.Clamp(fs, 8, 96) : 0,
            ShowPeriod = PostSetPeriodCheck.IsChecked == true,
        };
        tmp.GameIds = _postChosen.Select(p => p.Id).Take(PostLayout.MaxPriorityGames).ToList();
        return tmp;
    }

    private void PostSetFit_Click(object sender, RoutedEventArgs e)
    {
        if (_postEditing is not { } panel) return;
        var rows = MeasurePostRowSpan(PanelFromSettings(panel));
        PostSetRowSpanBox.SelectedIndex = rows - 1;
        ShowPostSettingsError(null);
        PostStatus.Text = $"中身に合わせて、縦 {rows} 段にしました（「適用」で反映されます）";
    }

    private void ShowPostSettingsError(string? message)
    {
        PostSetError.Text = message ?? "";
        PostSetError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 「再プレイを追加…」：起点の日より前にクリア済みで、期間内に遊んだ作品を一覧にして、
    /// 再プレイとして集計に含める作品を選ぶ（選んだ作品は、作品ごとに保存される）
    /// </summary>
    private void PostReplay_Click(object sender, RoutedEventArgs e)
    {
        var start = (PostStartPicker.SelectedDate ?? DateTime.Today).Date;
        var end = PostReportCalculator.PeriodEnd(start, PostYearRadio.IsChecked == true);
        var included = _cfg.PostReplayGameIds.ToHashSet();
        var candidates = PostReportCalculator.ReplayCandidates(_db.GetAll(), _db.GetSessions(start, end.AddDays(1)), start, end, included);

        var picked = ShowPostPickDialog("再プレイとして含める作品",
            "起点の日より前にクリアした作品（終了日が空欄のプレイ済を含む）は、ふつう集計に入れません。再プレイした作品だけ、☑で集計に含めます（作品ごとに保存されます）。",
            candidates.Select(c => (c.GameId, PostPickText(c.Title, c.Seconds), c.Included)).ToList(),
            "この期間に、起点の日より前にクリアした作品のプレイ記録はありません。");
        if (picked == null) return;
        _cfg.PostReplayGameIds = _cfg.PostReplayGameIds.Where(id => !picked.All.Contains(id))
            .Concat(picked.Checked).Distinct().ToList();
        _cfg.Save();
        RefreshPost();
    }

    private static string PostPickText(string title, double seconds) =>
        seconds >= 1 ? $"{title}　（この期間 {SummaryCalculator.FormatDuration(seconds)}）" : $"{title}　（この期間のプレイ記録なし）";

    /// <summary>
    /// 「作品を隠す…」：期間内に遊んだ作品を一覧にして、☑した作品をプレイ記録の集計から外す
    /// （クリア済みでない作品でも隠せる。作品ごとに保存される）
    /// </summary>
    private void PostHide_Click(object sender, RoutedEventArgs e)
    {
        var start = (PostStartPicker.SelectedDate ?? DateTime.Today).Date;
        var end = PostReportCalculator.PeriodEnd(start, PostYearRadio.IsChecked == true);
        var candidates = PostReportCalculator.HideCandidates(_db.GetAll(), _db.GetSessions(start, end.AddDays(1)), start, end,
            _cfg.PostHiddenGameIds.ToHashSet(), PostExcludeClearedCheck.IsChecked == true, _cfg.PostReplayGameIds.ToHashSet());
        var picked = ShowPostPickDialog("集計から隠す作品",
            "☑した作品は、クリア済みでなくても、プレイ時間・日数・グラフ・作品の欄など、プレイ記録の集計から外します（作品ごとに保存されます。☑を外すと戻ります）。",
            candidates.Select(c => (c.GameId, PostPickText(c.Title, c.Seconds), c.Hidden)).ToList(),
            "この期間のプレイ記録はありません。");
        if (picked == null) return;
        _cfg.PostHiddenGameIds = _cfg.PostHiddenGameIds.Where(id => !picked.All.Contains(id))
            .Concat(picked.Checked).Distinct().ToList();
        _cfg.Save();
        RefreshPost();
    }

    private record PostPickResult(HashSet<long> All, List<long> Checked);

    /// <summary>作品のチェック一覧ダイアログ（すべて選択／すべてクリアは左下）。OK なら一覧の全 ID と、☑の ID を返す</summary>
    private PostPickResult? ShowPostPickDialog(string title, string header, IReadOnlyList<(long Id, string Text, bool Checked)> items, string emptyText)
    {
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, long Id)>();
        foreach (var c in items)
        {
            var box = new CheckBox
            {
                IsChecked = c.Checked, Margin = new Thickness(0, 0, 0, 4),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = c.Text }, // 長い作品名は折り返す
            };
            boxes.Add((box, c.Id));
            list.Children.Add(box);
        }
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = emptyText, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        var ok = new Button { Content = "OK", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        var selectAll = new Button { Content = "すべて選択", Padding = new Thickness(12, 3, 12, 3) };
        var clearAll = new Button { Content = "すべてクリア", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
        selectAll.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = true);
        clearAll.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = false);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(selectAll);
        left.Children.Add(clearAll);
        var buttons = new Grid();
        buttons.Children.Add(left); // 一括選択・一括クリアは左下、OK・キャンセルは右下
        buttons.Children.Add(right);
        var head = new TextBlock
        {
            Text = header,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) });
        var win = new Window
        {
            Title = title, Width = 560, Height = 420, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return null;
        return new PostPickResult(boxes.Select(b => b.Id).ToHashSet(), boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList());
    }

    /// <summary>優先したい作品の選択欄：未プレイ・プレイ中の作品（すでに選んでいるものは、状態が変わっていても残す）</summary>
    private void FillPostPicks(PostPanel panel)
    {
        var byId = _postGames.ToDictionary(g => g.Id);
        _postChosen.Clear();
        foreach (var id in panel.GameIds.Where(byId.ContainsKey).Distinct())
            _postChosen.Add(new PostPick { Id = id, Title = byId[id].Title });
        _postPicks = _postGames
            .Where(g => g.Status is GameStatus.Unplayed or GameStatus.Playing)
            .OrderBy(g => g.Title, StringComparer.CurrentCulture)
            .Select(g => new PostPick { Id = g.Id, Title = g.Title })
            .ToList();
        PostSetSearchBox.Text = "";
        PostSetGamesList.ItemsSource = CollectionViewSource.GetDefaultView(_postPicks);
        PostSetChosenList.ItemsSource = _postChosen;
        RefreshPostCandidates();
    }

    /// <summary>候補の一覧：選んだ作品を除き、検索語で絞り込む</summary>
    private void RefreshPostCandidates()
    {
        var text = PostSetSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_postPicks);
        view.Filter = o => o is PostPick p && _postChosen.All(c => c.Id != p.Id)
            && (text.Length == 0 || p.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        view.Refresh();
        PostSetPickLabel.Text = $"優先したい作品（未プレイ・プレイ中から。ダブルクリックか「追加」で下の一覧へ）";
        PostSetChosenLabel.Text = $"選んだ作品（{_postChosen.Count} / {PostLayout.MaxPriorityGames} 件。ドラッグか ▲▼ で並び替え、× で外す）";
    }

    private void PostSetSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_postLoading) return;
        RefreshPostCandidates();
    }

    private void PostPickAdd(PostPick pick)
    {
        if (_postChosen.Count >= PostLayout.MaxPriorityGames)
        { ShowPostSettingsError($"優先したい作品は、{PostLayout.MaxPriorityGames} 件までです。"); return; }
        _postChosen.Add(new PostPick { Id = pick.Id, Title = pick.Title });
        RefreshPostCandidates();
    }

    private void PostPickAdd_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in PostSetGamesList.SelectedItems.Cast<PostPick>().ToList()) PostPickAdd(p);
    }

    private void PostPickCandidate_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PostSetGamesList.SelectedItem is PostPick p && e.OriginalSource is DependencyObject) PostPickAdd(p);
    }

    private PostPick? PostPickOf(object sender) => (sender as FrameworkElement)?.DataContext as PostPick;

    private void PostPickRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PostPickOf(sender) is { } p) { _postChosen.Remove(p); RefreshPostCandidates(); }
    }

    private void PostPickUp_Click(object sender, RoutedEventArgs e) => PostPickShift(PostPickOf(sender), -1);
    private void PostPickDown_Click(object sender, RoutedEventArgs e) => PostPickShift(PostPickOf(sender), 1);

    private void PostPickShift(PostPick? p, int delta)
    {
        if (p == null) return;
        var i = _postChosen.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _postChosen.Count) return;
        _postChosen.Move(i, j);
    }

    private PostPick? _postPickDrag;
    private Point _postPickDragStart;

    private void PostChosen_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _postPickDrag = null;
        if (IsOnButton(e.OriginalSource, PostSetChosenList)) return;
        var item = ItemsControl.ContainerFromElement(PostSetChosenList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _postPickDrag = item?.DataContext as PostPick;
        _postPickDragStart = e.GetPosition(null);
    }

    private void PostChosen_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_postPickDrag == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _postPickDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var p = _postPickDrag;
        _postPickDrag = null;
        DragDrop.DoDragDrop(PostSetChosenList, new DataObject("NovelKeeper.PostPick", p), DragDropEffects.Move);
    }

    private void PostChosen_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("NovelKeeper.PostPick") is not PostPick p) return;
        var from = _postChosen.IndexOf(p);
        if (from < 0) return;
        var target = ItemsControl.ContainerFromElement(PostSetChosenList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        var to = target?.DataContext is PostPick t ? _postChosen.IndexOf(t) : _postChosen.Count - 1;
        if (to >= 0 && to != from) _postChosen.Move(from, to);
        e.Handled = true;
    }

    private void PostSetApply_Click(object sender, RoutedEventArgs e)
    {
        if (_postEditing is not { } panel) return;
        var layout = Layout;
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        var kindChanged = kind != panel.Kind;

        if (!int.TryParse(PostSetRowBox.Text.Trim(), out var row1) || row1 < 1 || row1 > PostLayout.MaxRow + 1)
        { ShowPostSettingsError($"段は 1〜{PostLayout.MaxRow + 1} の数字で入れてください。"); return; }

        var fontSize = 0;
        if (PostLayout.UsesFont(kind) && PostSetFontSizeBox.Text.Trim().Length > 0 &&
            (!int.TryParse(PostSetFontSizeBox.Text.Trim(), out fontSize) || fontSize < 8 || fontSize > 96))
        { ShowPostSettingsError("文字の大きさは、8〜96 の数字で入れてください。"); return; }
        var color = PostSetColorBox.Text.Trim();
        if (PostLayout.UsesFont(kind) && color.Length > 0 && (!color.StartsWith('#') || ParsePostColor(color) == null))
        { ShowPostSettingsError("文字の色は、#RRGGBB の形（例：#FF8800）で入れてください。"); return; }

        var usesItems = PostLayout.UsesItems(kind);
        var max = PostLayout.MaxItems(kind);
        var count = panel.ItemCount;
        if (usesItems)
        {
            if (!int.TryParse(PostSetCountBox.Text.Trim(), out count) || count < 1 || count > max)
            { ShowPostSettingsError($"件数は 1〜{max} の数字で入れてください。"); return; }
        }
        var picks = _postChosen.Select(p => p.Id).ToList();
        if (kind == PostPanelKind.Priority && picks.Count > PostLayout.MaxPriorityGames)
        { ShowPostSettingsError($"優先したい作品は、{PostLayout.MaxPriorityGames} 件までです（いま {picks.Count} 件）。"); return; }

        // 種類を変えると、タイトルの「全カラム幅」の決まりが変わるので、先に種類を入れて試す
        var oldKind = panel.Kind;
        panel.Kind = kind;
        var error = layout.TryPlace(panel, row1 - 1, Math.Max(0, PostSetColBox.SelectedIndex), PostSetSpanBox.SelectedIndex + 1,
            PostSetRowSpanBox.SelectedIndex + 1);
        if (error != null) { panel.Kind = oldKind; ShowPostSettingsError(error); return; }

        var title = PostSetTitleBox.Text.Trim();
        panel.Title = title.Length == 0 || title == PostLayout.DefaultTitle(kind) ? null : title;
        if (PostLayout.UsesFont(kind))
        {
            panel.FontFamily = PostSetFontBox.SelectedIndex > 0 ? PostSetFontBox.SelectedItem as string : null;
            panel.FontColor = color.Length == 0 ? null : color;
            panel.FontSize = fontSize;
        }
        if (kind == PostPanelKind.Title)
        {
            panel.ShowPeriod = PostSetPeriodCheck.IsChecked == true;
            panel.FullBleed = PostSetBleedCheck.IsChecked == true;
            panel.Transparency = PostSetTransFollow.IsChecked == true ? null : (int)PostSetTrans.Value;
        }
        if (usesItems)
        {
            panel.ItemCount = count;
            panel.ItemColumns = Math.Max(0, PostSetItemColsBox.SelectedIndex);
        }
        else if (kindChanged) panel.ItemCount = PostLayout.NewPanel(kind).ItemCount;
        if (kind == PostPanelKind.Memo) panel.Text = PostSetTextBox.Text;
        panel.ViewColumns = PostLayout.UsesViewColumns(kind) ? Math.Max(0, PostSetViewColsBox.SelectedIndex) : 0;
        panel.Align = PostLayout.UsesAlign(kind) ? Math.Max(0, PostSetAlignBox.SelectedIndex) : 0;
        if (kind == PostPanelKind.Priority)
        {
            panel.GameIds = picks; // 下の「選んだ作品」の並びのまま
        }
        _cfg.Save();
        RebuildPostReport();
        OpenPostSettings(panel); // 反映後の値で表示し直す
        PostStatus.Text = "パネルの設定を反映しました";
    }
}
