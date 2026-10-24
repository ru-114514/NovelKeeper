using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// メイン画面のうち、「設定」タブ・上部ボタンの ▼・一覧の列の表示切り替えを担当する部分
/// </summary>
public partial class MainWindow
{
    private bool _loadingSettings; // 設定タブに値を読み込んでいる間は、変更イベントを無視する

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
    // 設定タブ
    // ================================================================

    /// <summary>批評空間との連携の詳細設定（見出し → 設定の項目名）</summary>
    private static readonly (string Label, string Property)[] EgsFields =
    {
        ("批評空間のURL", nameof(EgsConfig.BaseUrl)),
        ("ページの文字コード", nameof(EgsConfig.CharSet)),
        ("問い合わせの間隔（ミリ秒）", nameof(EgsConfig.RequestIntervalMs)),
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

    private void InitSettingsTab()
    {
        _loadingSettings = true;
        try
        {
            ExcludeIdleCheck.IsChecked = _cfg.ExcludeIdle;
            IdleMinutesBox.Text = _cfg.IdleMinutes.ToString();
            SubtractIdleCheck.IsChecked = _cfg.SubtractIdleTime;
            ForegroundOnlyCheck.IsChecked = _cfg.ForegroundOnly;
            CloseToTrayCheck.IsChecked = _cfg.CloseToTray;
            AutoStartCheck.IsChecked = _cfg.AutoStart;
            NotifyCheck.IsChecked = _cfg.NotifyOnStart;
            AlwaysAdminCheck.IsChecked = _cfg.AlwaysRunAsAdmin;
            Game.AlwaysRunAsAdmin = _cfg.AlwaysRunAsAdmin;
            ZoomText.Text = $"{_cfg.ListZoom * 100:0}%";
            ShowImagesCheck.IsChecked = _cfg.ShowImages;
            SyncDetailPositionBox();
        }
        finally
        {
            _loadingSettings = false;
        }

        BuildEgsFields();
        InitLoginTab();
        InitLaunchTab();
        InitScoreItems();
        InitAutoBackup();
        InitColumnList();
        UpdateAdminCount();

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"NovelKeeper {version?.ToString(3)}";
    }

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
                    StatusText.Text = $"「{label}」を保存しました（反映されないときはアプリを再起動してください）";
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

    private void UpdateLoginState()
    {
        var cred = CredentialStore.Load();
        LoginStateText.Text = cred == null
            ? "ログイン情報：未設定"
            : $"ログイン情報：登録済み（ID：{cred.Value.User}）";
    }

    /// <summary>プレイ時間・常駐の設定が変わったら保存する</summary>
    private void PlaySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;

        if (!int.TryParse(IdleMinutesBox.Text.Trim(), out var minutes) || minutes < 1 || minutes > 600)
        {
            // 分数の欄が正しくないときは、その欄だけ元に戻す（ほかの設定は保存する）
            if (ReferenceEquals(sender, IdleMinutesBox)) MessageBox.Show("分数は 1～600 の整数で入力してください。", "入力エラー");
            minutes = _cfg.IdleMinutes;
            IdleMinutesBox.Text = minutes.ToString();
        }

        var autoStartChanged = _cfg.AutoStart != (AutoStartCheck.IsChecked == true);
        _cfg.ExcludeIdle = ExcludeIdleCheck.IsChecked == true;
        _cfg.IdleMinutes = minutes;
        _cfg.SubtractIdleTime = SubtractIdleCheck.IsChecked == true;
        _cfg.ForegroundOnly = ForegroundOnlyCheck.IsChecked == true;
        _cfg.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _cfg.AutoStart = AutoStartCheck.IsChecked == true;
        _cfg.NotifyOnStart = NotifyCheck.IsChecked == true;
        _cfg.AlwaysRunAsAdmin = AlwaysAdminCheck.IsChecked == true;
        if (Game.AlwaysRunAsAdmin != _cfg.AlwaysRunAsAdmin)
        {
            Game.AlwaysRunAsAdmin = _cfg.AlwaysRunAsAdmin;
            GameGrid.Items.Refresh(); // 一覧の紫を付け直す
            UpdateAdminBadge();
        }
        _cfg.Save();

        if (autoStartChanged)
        {
            try { AutoStart.Set(_cfg.AutoStart); }
            catch (Exception ex) { MessageBox.Show("自動起動の設定に失敗しました：" + ex.Message); }
        }
        StatusText.Text = "設定を保存しました";
    }

    /// <summary>詳細欄の「管理者権限で起動します」の表示を、選んでいる作品に合わせる</summary>
    private void UpdateAdminBadge(Game? g = null)
    {
        g ??= Selected;
        AdminBadge.Visibility = g?.RunsAsAdmin == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>管理者権限で起動する印がついた作品の件数を、設定画面に表示する</summary>
    private void UpdateAdminCount()
    {
        var n = _db.GetAll().Count(x => x.RunAsAdmin);
        AdminCountText.Text = n == 0 ? "（印がついた作品はありません）" : $"（現在 {n} 件）";
        ClearAdminButton.IsEnabled = n > 0;
        UpdateAdminBadge();
    }

    private void ClearAdmin_Click(object sender, RoutedEventArgs e)
    {
        var n = _db.GetAll().Count(x => x.RunAsAdmin);
        if (n == 0) { UpdateAdminCount(); return; }
        if (MessageBox.Show($"管理者権限で起動する設定を、{n} 件の作品から外します。\n（「すべての作品を常に管理者権限で起動する」の☑は、そのままです）",
                "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var cleared = _db.ClearAllRunAsAdmin();
        Reload();
        UpdateAdminCount();
        StatusText.Text = $"管理者権限の設定を {cleared} 件の作品から外しました";
    }

    /// <summary>詳細欄の位置の選択欄を、今の状態に合わせる</summary>
    private void SyncDetailPositionBox()
    {
        var tag = _cfg.DetailVisible ? _cfg.DetailPosition : "Hidden";
        var item = DetailPositionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == tag);
        if (ReferenceEquals(DetailPositionBox.SelectedItem, item)) return;
        var old = _loadingSettings;
        _loadingSettings = true;
        DetailPositionBox.SelectedItem = item;
        _loadingSettings = old;
    }

    private void DetailPositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || _cfg == null || DetailPositionBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == "Hidden")
        {
            _cfg.DetailVisible = false;
        }
        else
        {
            _cfg.DetailPosition = tag;
            _cfg.DetailVisible = true;
        }
        _cfg.Save();
        ApplyDetailLayout();
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => SetListZoom(1.0);

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"\"{AppPaths.Dir}\"") { UseShellExecute = true });
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

    private readonly ObservableCollection<ColumnItem> _columnItems = new();

    /// <summary>設定タブの一覧に並べる列1つ分</summary>
    public class ColumnItem : INotifyPropertyChanged
    {
        private bool _visible;
        public string Key { get; init; } = "";
        public bool CanHide { get; init; }
        public string Name => CanHide ? Key : Key + "（常に表示）";
        public bool Visible
        {
            get => _visible;
            set { _visible = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

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

    private void InitColumnList()
    {
        EnsureColumnSettings();
        var selectedKey = (ColumnList.SelectedItem as ColumnItem)?.Key;
        _columnItems.Clear();
        foreach (var c in _cfg.Columns)
            _columnItems.Add(new ColumnItem { Key = c.Key, Visible = c.Visible, CanHide = !LockedColumns.Contains(c.Key) });
        ColumnList.ItemsSource = _columnItems;
        ColumnList.SelectedItem = _columnItems.FirstOrDefault(x => x.Key == selectedKey);
    }

    /// <summary>設定タブの一覧の内容を保存して、一覧に反映する</summary>
    private void SaveColumnsFromList()
    {
        _cfg.Columns = _columnItems.Select(x => new ColumnSetting { Key = x.Key, Visible = x.Visible || !x.CanHide }).ToList();
        _cfg.Save();
        ApplyColumnLayout();
    }

    private void ColumnCheck_Click(object sender, RoutedEventArgs e) => SaveColumnsFromList();

    private void ColumnUp_Click(object sender, RoutedEventArgs e) => MoveColumnItem(-1);

    private void ColumnDown_Click(object sender, RoutedEventArgs e) => MoveColumnItem(+1);

    private void MoveColumnItem(int delta)
    {
        if (ColumnList.SelectedItem is not ColumnItem item) return;
        var i = _columnItems.IndexOf(item);
        var j = i + delta;
        if (j < 0 || j >= _columnItems.Count) return;
        _columnItems.Move(i, j);
        ColumnList.SelectedItem = item;
        ColumnList.ScrollIntoView(item);
        SaveColumnsFromList();
    }

    private void ColumnReset_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Columns = new List<ColumnSetting>();
        // 並び順を画面の作り（初期の順）に戻す
        var original = GameGrid.Columns.ToList();
        for (var i = 0; i < original.Count; i++) original[i].DisplayIndex = i;
        EnsureColumnSettings();
        _cfg.Save();
        ApplyColumnLayout();
        InitColumnList();
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
        Dispatcher.InvokeAsync(InitColumnList, DispatcherPriority.Background);
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
                InitColumnList();
            };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "列の並び順を変える…（詳細設定タブ）" };
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
    }
}
