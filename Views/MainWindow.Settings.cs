using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// メイン画面のうち、上部ボタンの ▼・一覧の列の表示と並び順・一覧の右クリックのメニュー・
/// 詳細設定タブ（SettingsTabView）の読み込みと一覧への反映を担当する部分
/// </summary>
public partial class MainWindow
{
    // ================================================================
    // 上部のボタン：入りきらない分は ▼ にまとめる
    // ================================================================

    private void ToolBarRoot_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateToolbarOverflow();

    /// <summary>ボタンが横幅に入りきるか調べて、はみ出す分を隠し、▼ を出す</summary>
    private void UpdateToolbarOverflow()
    {
        var available = ToolBarRoot.ActualWidth;
        if (available <= 0) return;

        var items = ToolItems.Children.OfType<FrameworkElement>().ToList();
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var widths = new List<double>();
        foreach (var item in items)
        {
            item.Visibility = Visibility.Visible; // 隠したままだと幅が測れない
            item.Measure(inf);
            widths.Add(item.DesiredSize.Width);
        }

        if (widths.Sum() <= available)
        {
            MoreButton.Visibility = Visibility.Collapsed;
            return;
        }

        MoreButton.Visibility = Visibility.Visible;
        MoreButton.Measure(inf);
        available -= MoreButton.DesiredSize.Width;

        var used = 0.0;
        var overflow = false;
        for (var i = 0; i < items.Count; i++)
        {
            used += widths[i];
            if (overflow || used > available) overflow = true;
            items[i].Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>▼：隠れているボタンをメニューで表示する</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        foreach (var item in ToolItems.Children.OfType<FrameworkElement>().Where(x => x.Visibility != Visibility.Visible))
        {
            if (item is Button button)
            {
                var mi = new MenuItem { Header = button.Content?.ToString() };
                mi.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                menu.Items.Add(mi);
            }
            else if (item is StackPanel { Tag: "preset" } presetPanel)
            {
                // 並び替えの設定：設定ボタンと、設定済みのときの「解除」
                var apply = presetPanel.Children.OfType<Button>().First();
                var clear = presetPanel.Children.OfType<Button>().Last();
                var mi = new MenuItem { Header = apply.Content?.ToString() };
                mi.Click += (_, _) => apply.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, apply));
                menu.Items.Add(mi);
                if (clear.Visibility == Visibility.Visible)
                {
                    var del = new MenuItem { Header = $"{apply.Content}を解除" };
                    del.Click += (_, _) => clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clear));
                    menu.Items.Add(del);
                }
            }
            else if (ReferenceEquals(item, FilterPanel))
            {
                // 「表示:」の絞り込みは、選択肢をそのままメニューに並べる
                var parent = new MenuItem { Header = "表示の絞り込み" };
                for (var i = 0; i < FilterBox.Items.Count; i++)
                {
                    var index = i;
                    var text = (FilterBox.Items[i] as ComboBoxItem)?.Content?.ToString() ?? "";
                    var mi = new MenuItem { Header = text, IsCheckable = true, IsChecked = FilterBox.SelectedIndex == i };
                    mi.Click += (_, _) => FilterBox.SelectedIndex = index;
                    parent.Items.Add(mi);
                }
                menu.Items.Add(parent);
            }
        }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    // ================================================================
    // 詳細設定タブ（画面と保存は SettingsTabView。ここは起動時の読み込みと、一覧・詳細欄への反映）
    // ================================================================

    /// <summary>設定の各タブに値を読み込む（起動時に1回）</summary>
    private void InitSettingsTab()
    {
        Game.AlwaysRunAsAdmin = _cfg.AlwaysRunAsAdmin;
        SettingsView.InitSettingsTab();
        EgsView.InitEgsTab();
        UpdateSyncTooltip();
        LoginView.InitLoginTab();
        LaunchView.InitLaunchTab();
        InitScoreItems();
        ExportView.InitAutoBackup();
        UpdateAdminCount();
    }

    /// <summary>詳細欄の「管理者権限で起動します」の表示を、選んでいる作品に合わせる</summary>
    private void UpdateAdminBadge(Game? g = null)
    {
        g ??= Selected;
        AdminBadge.Visibility = g?.RunsAsAdmin == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>管理者権限で起動する印がついた作品の件数（詳細設定タブ）と、詳細欄の表示を合わせる</summary>
    private void UpdateAdminCount()
    {
        SettingsView.UpdateAdminCount();
        UpdateAdminBadge();
    }

    // ================================================================
    // 一覧の列（表示・非表示と並び順）
    // ================================================================

    /// <summary>常に表示する列（隠せない）</summary>
    private static readonly HashSet<string> LockedColumns = new() { "タイトル", "ブランド", "状態", "購入予定", "購入確認", "プレイ時間", "起動設定" };

    /// <summary>初期状態では隠しておく列（批評空間から追加できる項目）</summary>
    private static readonly HashSet<string> DefaultHiddenColumns = new()
    {
        "平均値", "批評空間ID",
        "総プレイ時間", "面白さが分かるまで", "おかず", "購入前得点", "開始日",
        "platform", "動作状況", "詳細状況", "一言感想", "長文感想", "ネタバレ",
    };

    private static bool IsDefaultHidden(string key) => DefaultHiddenColumns.Contains(key);

    private static string ColumnKey(DataGridColumn col) => col.Header?.ToString() ?? "";

    /// <summary>保存されている列の設定を、今ある列に合わせて整える（新しく増えた列は最後に足す）</summary>
    private void EnsureColumnSettings()
    {
        var fresh = _cfg.Columns.Count == 0; // 初期状態（列の初期化を含む）
        var keys = GameGrid.Columns.Select(ColumnKey).Where(k => k != "").ToList();
        var list = _cfg.Columns.Where(c => keys.Contains(c.Key)).GroupBy(c => c.Key).Select(g => g.First()).ToList();
        foreach (var key in keys.Where(k => list.All(c => c.Key != k)))
        {
            var setting = new ColumnSetting { Key = key, Visible = !IsDefaultHidden(key) };
            // 「特例」は「状態」の隣、「登録日」は「発売日」の隣に置く
            var anchor = key == "特例" ? "状態" : key == "登録日" ? "発売日" : null;
            if (anchor != null)
            {
                var at = list.FindIndex(c => c.Key == anchor);
                if (at >= 0) { list.Insert(at + 1, setting); continue; }
            }
            list.Add(setting);
        }

        // 初期の並び（先頭の列）に整える。保存済みの並びは、新しい初期の並びに1回だけ合わせ直す（そのあとは、ドラッグした並びを保つ）
        var reorder = fresh || _cfg.ColumnOrderVersion < ColumnOrderVersionNow;
        if (reorder)
        {
            var lead = DefaultLeadingColumns.Select(k => list.FirstOrDefault(c => c.Key == k)).OfType<ColumnSetting>().ToList();
            list = lead.Concat(list.Where(c => !DefaultLeadingColumns.Contains(c.Key))).ToList();
        }

        // 「購入確認」は、いつも「購入予定」の隣に置く（固定）
        var will = list.FindIndex(c => c.Key == "購入予定");
        var check = list.FindIndex(c => c.Key == "購入確認");
        if (will >= 0 && check >= 0 && check != will + 1)
        {
            var item = list[check];
            list.RemoveAt(check);
            list.Insert(list.FindIndex(c => c.Key == "購入予定") + 1, item);
        }
        foreach (var c in list.Where(c => LockedColumns.Contains(c.Key))) c.Visible = true;
        _cfg.Columns = list;

        if (reorder)
        {
            _cfg.ColumnOrderVersion = ColumnOrderVersionNow;
            _cfg.Save();
        }
    }

    private const int ColumnOrderVersionNow = 2;

    /// <summary>一覧の初期の並び（先頭から）。続きの列は、画面の作りの順</summary>
    private static readonly string[] DefaultLeadingColumns =
    {
        "タイトル", "ブランド", "起動設定", "状態", "特例", "プレイ時間", "クリア日", "発売日", "登録日", "所持", "購入予定", "購入確認", "得点", "中央値", "データ数", "購入額",
    };

    /// <summary>列の設定を一覧に反映する</summary>
    private void ApplyColumnLayout()
    {
        EnsureColumnSettings();
        var index = 0;
        foreach (var setting in _cfg.Columns)
        {
            var col = GameGrid.Columns.FirstOrDefault(c => ColumnKey(c) == setting.Key);
            if (col == null) continue;
            col.DisplayIndex = index++;
            col.Visibility = setting.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>列の設定（今ある列に合わせて整えたもの）。詳細設定タブの一覧に並べる</summary>
    private IReadOnlyList<ColumnSetting> ColumnSettings()
    {
        EnsureColumnSettings();
        return _cfg.Columns;
    }

    /// <summary>詳細設定タブで変えた列の設定を保存して、一覧に反映する</summary>
    private void SaveColumns(List<ColumnSetting> columns)
    {
        _cfg.Columns = columns;
        _cfg.Save();
        ApplyColumnLayout();
    }

    /// <summary>列の表示と並び順を初期状態に戻す</summary>
    private void ResetColumns()
    {
        _cfg.Columns = new List<ColumnSetting>();
        // 並び順を画面の作り（初期の順）に戻す
        var original = GameGrid.Columns.ToList();
        for (var i = 0; i < original.Count; i++) original[i].DisplayIndex = i;
        EnsureColumnSettings();
        _cfg.Save();
        ApplyColumnLayout();
    }

    /// <summary>一覧の見出しをドラッグして並べ替えたら保存する</summary>
    private void SaveColumnOrderFromGrid()
    {
        var visibleOf = _cfg.Columns.ToDictionary(c => c.Key, c => c.Visible);
        _cfg.Columns = GameGrid.Columns.OrderBy(c => c.DisplayIndex)
            .Select(c => new ColumnSetting { Key = ColumnKey(c), Visible = visibleOf.GetValueOrDefault(ColumnKey(c), true) })
            .Where(c => c.Key != "")
            .ToList();
        _cfg.Save();
        Dispatcher.InvokeAsync(SettingsView.InitColumnList, DispatcherPriority.Background);
    }

    /// <summary>右クリックのメニューに、管理者権限で起動する項目を足す（Steam など URL 形式の起動には使えない）</summary>
    private void AddAdminMenuItems(ContextMenu menu, Game g)
    {
        var isUrl = ShortcutResolver.IsUrl(g.ExePath ?? "");
        var launch = new MenuItem { Header = "管理者権限で起動", IsEnabled = !isUrl };
        launch.Click += (_, _) => LaunchAsAdmin(g);
        var always = new MenuItem
        {
            Header = "この作品は常に管理者権限で起動する",
            IsCheckable = true,
            IsChecked = g.RunAsAdmin,
            IsEnabled = !isUrl,
            StaysOpenOnClick = false,
        };
        always.Click += (_, _) =>
        {
            g.RunAsAdmin = always.IsChecked;
            _db.Update(g);
            GameGrid.Items.Refresh();
            UpdateAdminCount();
            StatusText.Text = g.RunAsAdmin
                ? $"この作品は管理者権限で起動します：{g.Title}"
                : $"管理者権限での起動を外しました：{g.Title}";
        };
        menu.Items.Add(launch);
        menu.Items.Add(always);
        if (isUrl) menu.Items.Add(new MenuItem { Header = "（Steam など URL 形式の起動には使えません）", IsEnabled = false });
        menu.Items.Add(new Separator());
    }

    /// <summary>一覧を右クリック：列の表示・非表示をその場で切り替える</summary>
    private void GameGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = GameGrid.ContextMenu;
        if (menu == null) return;
        menu.Items.Clear();

        // 「起動設定」欄の上で右クリックしたら、起動するもの（収録作など）の選び替えを先頭に出す
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.Column?.Header?.ToString() == "起動設定" && cell.DataContext is Game g)
        {
            GameGrid.SelectedItem = g;
            if (g.Targets.Count > 0)
            {
                menu.Items.Add(new MenuItem { Header = "起動ファイル一覧", IsEnabled = false });
                foreach (var mi in TargetMenuItems(g)) menu.Items.Add(mi);
                menu.Items.Add(new Separator());
            }
            if (!g.IsLaunchUnset) AddAdminMenuItems(menu, g);
        }

        // 作品の行の上で右クリックしたら、優先の付け外しを出す
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is Game rowGame) AddPriorityMenuItem(menu, rowGame);

        menu.Items.Add(new MenuItem { Header = "表示する列", IsEnabled = false });
        menu.Items.Add(new Separator());
        foreach (var c in _cfg.Columns)
        {
            var locked = LockedColumns.Contains(c.Key);
            var key = c.Key;
            var mi = new MenuItem
            {
                Header = locked ? key + "（常に表示）" : key,
                IsCheckable = !locked,
                IsChecked = c.Visible || locked,
                IsEnabled = !locked,
                StaysOpenOnClick = true,
            };
            mi.Click += (_, _) =>
            {
                var setting = _cfg.Columns.FirstOrDefault(x => x.Key == key);
                if (setting == null) return;
                setting.Visible = mi.IsChecked;
                _cfg.Save();
                ApplyColumnLayout();
                SettingsView.InitColumnList();
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "列の並び順を変える…（詳細設定タブ）" };
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
    }
}
