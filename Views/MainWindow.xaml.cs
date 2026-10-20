using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Win32;

namespace ErogeLauncher.Views;

public partial class MainWindow : Window
{
    private readonly EgsConfig _cfg;
    private readonly Database _db;
    private readonly EgsClient _egs;
    private readonly PlayTimeTracker _tracker;
    private readonly TrayIcon _tray;
    private bool _loadingDetail; // 詳細欄の読み込み中はイベント処理をしない
    private bool _loadingTheme;
    private bool _exiting;       // 本当に終了するとき true（閉じるボタンでは常駐する）
    private bool _trayNotified;  // 「常駐しています」の通知は1回だけ

    public MainWindow()
    {
        InitializeComponent();
        // タイトルにバージョンを表示（build.bat で指定したバージョン）
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null) Title = $"NovelKeeper {version.ToString(3)} ― ノベルゲーム ランチャー";
        _cfg = App.Config;
        DbBackup.RunDaily();
        _db = new Database(AppPaths.Database);
        // 常駐したまま日をまたいでも、1日1回バックアップを取る
        var backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        backupTimer.Tick += (_, _) => Task.Run(DbBackup.RunDaily);
        backupTimer.Start();
        _egs = new EgsClient(_cfg);

        // ---- プレイ時間の計測（常駐して監視） ----
        _tracker = new PlayTimeTracker(_db, _cfg);
        _tracker.SessionStarted += (id, title) => Dispatcher.InvokeAsync(() => OnSessionStarted(title));
        _tracker.SessionEnded += (id, title, sec) => Dispatcher.InvokeAsync(() => OnSessionEnded(title, sec));
        _tracker.Ticked += list => Dispatcher.InvokeAsync(() => OnTracking(list));
        _tracker.Start();

        // ---- 通知領域のアイコン ----
        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.SettingsRequested += () => { ShowFromTray(); OpenSettings(); };
        _tray.ExitRequested += ExitApp;
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _tracker.Dispose();
            _tray.Dispose();
            _floatWindow?.Close(); // 別ウィンドウの詳細欄が残っていると、アプリが終わらないため
        };

        // 設定ファイルが壊れていて初期状態に戻したときは、そのことを知らせる
        if (EgsConfig.ResetNotice is string notice)
        {
            EgsConfig.ResetNotice = null;
            Loaded += (_, _) => MessageBox.Show(this, notice, "NovelKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 初めて使う人向けの案内（ログイン情報がまだない場合だけ）
        if (!App.StartInTray && !_cfg.WelcomeShown && CredentialStore.Load() == null)
            Loaded += (_, _) => ShowWelcome();

        // Windows の起動時（--tray）は、画面を出さずに常駐する
        if (App.StartInTray)
        {
            ShowActivated = false;
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Loaded += (_, _) =>
            {
                Hide();
                ShowInTaskbar = true;
                WindowState = WindowState.Normal;
            };
        }

        ApplyDetailLayout();         // 詳細欄の位置・表示を復元
        SetListZoom(_cfg.ListZoom, showStatus: false); // 一覧の表示倍率を復元
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Loaded += (_, _) => { if (!App.StartInTray) ApplyDetailLayout(); }; // 別ウィンドウは画面が出てから表示
        FilterBox.SelectedIndex = 0; // ここで一覧が読み込まれる

        ShowBeforePurchaseCheck.IsChecked = _cfg.ShowBeforePurchase;
        InitSettingsTab();

        // 保存されているテーマを選択欄に表示（App で適用済み）
        _loadingTheme = true;
        ThemeBox.SelectedIndex = _cfg.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
        _loadingTheme = false;
    }

    private Game? Selected => GameGrid.SelectedItem as Game;

    // ================================================================
    // 一覧
    // ================================================================

    private void Reload()
    {
        var selectedId = Selected?.Id;
        var all = _db.GetAll();
        if (!_cfg.ShowBeforePurchase)
            all = all.Where(g => !g.IsBeforePurchase).ToList(); // 購入前の作品は省く
        var list = FilterBox.SelectedIndex switch
        {
            1 => all.Where(g => g.Status == GameStatus.Unplayed),
            2 => all.Where(g => g.Status == GameStatus.Playing),
            3 => all.Where(g => g.Status == GameStatus.Played),
            4 => all.Where(g => g.Status == GameStatus.Dropped),
            5 => all.Where(g => g.IsLaunchUnset),
            _ => all,
        };
        var items = list.ToList();
        GameGrid.ItemsSource = items;
        _tracker?.InvalidateGames(); // 作品の情報が変わったかもしれないので、計測側にも読み直してもらう
        ApplySort();
        GameGrid.SelectedItem = items.FirstOrDefault(g => g.Id == selectedId);
        if (GameGrid.SelectedItem == null) ShowDetail(null);
    }

    // ================================================================
    // 一覧の左右スライダー（一覧の中の横スクロールと連動させる）
    // ================================================================

    private ScrollViewer? _gridScroll;
    private bool _syncingScroll;

    private bool _gridHooked;

    private void GameGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // タブを切り替えるたびに呼ばれるので、つなぐのは最初の1回だけ
        if (_gridHooked) return;
        _gridHooked = true;
        ApplyColumnLayout();
        RestoreColumnWidths();
        AttachGridScroll();
        // 見た目の部品がまだできていないときに備えて、配置が終わるたびに確認する
        GameGrid.LayoutUpdated += (_, _) => { if (_gridScroll == null) AttachGridScroll(); else SyncHScroll(); };
        GameGrid.SizeChanged += (_, _) => SyncHScroll();
        GameGrid.ColumnReordered += (_, _) => SaveColumnOrderFromGrid();
        GameGrid.ContextMenuOpening += GameGrid_ContextMenuOpening;
        GameGrid.ContextMenu = new ContextMenu(); // 中身は開くたびに作る
    }

    private bool _columnWidthsHooked;

    /// <summary>保存しておいた列の幅を戻し、以後、幅を変えたら保存する</summary>
    private void RestoreColumnWidths()
    {
        if (_columnWidthsHooked) return;
        _columnWidthsHooked = true;

        var widthDescriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        foreach (var col in GameGrid.Columns)
        {
            var key = col.Header?.ToString() ?? "";
            if (_cfg.ColumnWidths.TryGetValue(key, out var w) && w >= col.MinWidth)
                col.Width = new DataGridLength(w);

            widthDescriptor.AddValueChanged(col, (_, _) =>
            {
                if (col.Width.IsAbsolute) _cfg.ColumnWidths[key] = Math.Round(col.Width.Value);
                else if (col.ActualWidth > 0) _cfg.ColumnWidths[key] = Math.Round(col.ActualWidth);
                _saveColumnsTimer ??= CreateSaveTimer();
                _saveColumnsTimer.Stop();
                _saveColumnsTimer.Start(); // ドラッグ中に何度も保存しないよう、止まってから1秒後に保存
            });
        }
    }

    private DispatcherTimer? _saveColumnsTimer;

    private DispatcherTimer CreateSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { t.Stop(); _cfg.Save(); };
        return t;
    }

    /// <summary>一覧の中のスクロール部品を見つけて、スライダーとつなぐ</summary>
    private void AttachGridScroll()
    {
        if (_gridScroll != null) return;
        GameGrid.ApplyTemplate();
        _gridScroll = FindChild<ScrollViewer>(GameGrid);
        if (_gridScroll == null) return;
        _gridScroll.ScrollChanged += (_, _) => SyncHScroll();
        SyncHScroll();
    }

    /// <summary>一覧の横スクロールの状態を、下のスライダーに反映する</summary>
    private void SyncHScroll()
    {
        if (_gridScroll == null) return;
        if (Math.Abs(ListHScroll.Maximum - Math.Max(0, _gridScroll.ScrollableWidth)) < 0.5 &&
            Math.Abs(ListHScroll.Value - _gridScroll.HorizontalOffset) < 0.5 &&
            Math.Abs(ListHScroll.ViewportSize - Math.Max(1, _gridScroll.ViewportWidth)) < 0.5) return; // 変化なし
        _syncingScroll = true;
        ListHScroll.Maximum = Math.Max(0, _gridScroll.ScrollableWidth);
        ListHScroll.ViewportSize = Math.Max(1, _gridScroll.ViewportWidth);
        ListHScroll.Value = _gridScroll.HorizontalOffset;
        // Excel のように常に表示・操作できる状態にしておく（全部の列が見えているときは、つまみが端から端までになる）
        _syncingScroll = false;
    }

    private void ListHScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingScroll) _gridScroll?.ScrollToHorizontalOffset(e.NewValue);
    }

    /// <summary>Shift＋ホイールで左右に動かす。Ctrl＋ホイールで拡大縮小</summary>
    private void GameGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetListZoom(_cfg.ListZoom + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
            return;
        }
        if (_gridScroll == null || (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        _gridScroll.ScrollToHorizontalOffset(_gridScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private const double MinZoom = 0.6;
    private const double MaxZoom = 2.0;

    /// <summary>一覧の表示倍率を変える（60%～200%）。倍率は保存して次回も使う</summary>
    private void SetListZoom(double zoom, bool showStatus = true)
    {
        zoom = Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 1);
        GameGrid.LayoutTransform = Math.Abs(zoom - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(zoom, zoom);
        if (Math.Abs(zoom - _cfg.ListZoom) > 0.001)
        {
            _cfg.ListZoom = zoom;
            _cfg.Save();
        }
        ZoomText.Text = $"{zoom * 100:0}%";
        if (showStatus) StatusText.Text = $"一覧の表示倍率：{zoom * 100:0}%（Ctrl＋0 で 100% に戻す）";
    }

    /// <summary>Ctrl＋0 で 100% に戻す</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetListZoom(1.0);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }

    // ================================================================
    // 並び替え（複数条件）
    // ================================================================

    /// <summary>保存されている並び順を一覧に適用する</summary>
    private void ApplySort()
    {
        var view = CollectionViewSource.GetDefaultView(GameGrid.ItemsSource);
        if (view == null) return;

        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var k in _cfg.SortKeys.Where(k => k.Property == "Status"))
                k.Property = "StatusOrder"; // 以前の設定を新しい並び順に読み替える
            foreach (var k in _cfg.SortKeys)
                view.SortDescriptions.Add(new SortDescription(k.Property,
                    k.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        }

        // 見出しの▲▼も合わせる
        foreach (var col in GameGrid.Columns)
        {
            var key = _cfg.SortKeys.FirstOrDefault(k => k.Property == col.SortMemberPath);
            col.SortDirection = key == null
                ? null
                : key.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        }
    }

    /// <summary>見出しのクリック（Shift+クリックで条件を追加）で並び替えたとき、その条件を保存する</summary>
    private void GameGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // DataGrid 標準の並び替えが終わってから読み取る
        Dispatcher.InvokeAsync(() =>
        {
            var view = CollectionViewSource.GetDefaultView(GameGrid.ItemsSource);
            if (view == null) return;
            _cfg.SortKeys = view.SortDescriptions
                .Select(d => new SortKey { Property = d.PropertyName, Descending = d.Direction == ListSortDirection.Descending })
                .ToList();
            _cfg.Save();
        }, DispatcherPriority.Background);
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.ApplyTheme(theme);
        _cfg.Theme = theme;
        _cfg.Save();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SortWindow(_cfg.SortKeys) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _cfg.SortKeys = dlg.Result;
        _cfg.Save();
        ApplySort();
    }

    private void ShowBeforePurchase_Changed(object sender, RoutedEventArgs e)
    {
        if (_db == null) return;
        _cfg.ShowBeforePurchase = ShowBeforePurchaseCheck.IsChecked == true;
        _cfg.Save();
        Reload();
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_db != null) Reload();
    }

    private void GameGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail(Selected);

    private void ShowDetail(Game? g)
    {
        // 同じ作品を編集中（未保存）なら、入力欄は書き換えない（一覧の読み直しで、入力中の内容が消えないように）
        var keepEdits = g != null && _detailDirty && _detailGameId == g.Id;
        if (!keepEdits) _detailDirty = false;
        _detailGameId = g?.Id;

        _loadingDetail = true;
        DetailPanel.IsEnabled = g != null && !_detailLocked;
        DetailTitle.Text = g?.Title ?? "作品を選択してください";
        TargetGrid.ItemsSource = g?.Targets;
        if (!keepEdits)
        {
            PlayedCheck.IsChecked = g?.IsPlayed ?? false;
            ClearDatePicker.SelectedDate = g?.ClearDate;
            ScoreBox.Text = g?.MyScore?.ToString() ?? "";
            StatusBox.SelectedIndex = g?.Status switch
            {
                GameStatus.Playing => 1,
                GameStatus.Dropped => 2,
                _ => 0,
            };
            StatusBox.IsEnabled = !(g?.IsPlayed ?? false);
        }
        DetailInfo.Text = g == null
            ? ""
            : $"批評空間ID: {g.EgsId?.ToString() ?? "なし"}\n" +
              $"批評空間側: {g.EgsText}\n" +
              $"批評空間の得点: {g.EgsScore?.ToString() ?? "-"}\n" +
              $"最終同期: {g.EgsSyncedAt?.ToString("yyyy/MM/dd HH:mm") ?? "-"}\n" +
              $"合計プレイ時間: {(g.TotalSeconds == 0 ? "-" : g.PlayTimeText)}" +
              (g.PendingReview.Count > 0
                  ? "\n未反映の項目: " + string.Join("、", g.PendingReview.Select(PendingLabel))
                  : "");
        if (!keepEdits) ShowReview(g);
        _loadingDetail = false;
        if (!keepEdits) ShowImage(g);
    }

    // ================================================================
    // 追加・起動
    // ================================================================

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        new SearchWindow(_egs, _db) { Owner = this }.ShowDialog();
        Reload();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        new AutoDetectWindow(_db, _cfg) { Owner = this }.ShowDialog();
        Reload();
        InitLaunchTab(); // 自動検出の画面で変えたフォルダなどを、タブにも反映する
    }

    /// <summary>
    /// 一覧のダブルクリック：「起動設定」欄（設定済み）をダブルクリックしたときだけ起動する。
    /// ほかの欄のダブルクリックでは起動しない（うっかり起動を防ぐ）
    /// </summary>
    private void GameGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject src) return;

        // ▼ ボタンの上なら起動しない
        for (var d = src; d != null && d is not DataGridCell; d = ParentOf(d))
            if (d is Button) return;

        var cell = FindParent<DataGridCell>(src);
        if (cell?.Column?.Header?.ToString() != "起動設定" || cell.DataContext is not Game g || g.IsLaunchUnset) return;

        GameGrid.SelectedItem = g;
        e.Handled = true;
        Launch();
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = ParentOf(d);
        return d as T;
    }

    /// <summary>親の部品（文字の一部などの場合は、見た目の部品ではないので別の方法でたどる）</summary>
    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>一覧の「起動設定」欄の ▼：起動するもの（収録作など）を選び替える</summary>
    private void RowTargetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Game g } button) return;
        GameGrid.SelectedItem = g;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var mi in TargetMenuItems(g)) menu.Items.Add(mi);
        if (menu.Items.Count == 0) return;
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "追加・編集は、詳細欄の「起動するもの」で行えます", IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>一覧の「起動設定」欄の ▼ と右クリック用：起動するもの（収録作など）を選び替えるメニュー項目</summary>
    private List<MenuItem> TargetMenuItems(Game g)
    {
        var list = new List<MenuItem>();
        foreach (var t in g.Targets)
        {
            var target = t;
            var mi = new MenuItem { Header = "▶ " + t.Display, IsCheckable = true, IsChecked = ReferenceEquals(t, g.CurrentTarget), ToolTip = t.Path };
            mi.Click += (_, _) =>
            {
                if (ReferenceEquals(target, g.CurrentTarget)) return;
                SetCurrentTarget(g, target);
                RefreshTargets(g);
                StatusText.Text = $"起動設定を「{target.Display}」にしました：{g.Title}";
            };
            list.Add(mi);
        }
        return list;
    }

    // ================================================================
    // ドラッグ＆ドロップで実行ファイルを設定
    // ================================================================

    private void GameGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void GameGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // ドロップした位置にある行（作品）を探す
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: Game g })
        {
            MessageBox.Show("作品の行の上にドロップしてください。");
            return;
        }

        // ドロップしたものをすべて「起動するもの」に追加し、最初のものを今使うものにする（進み具合に合わせて切り替え）
        var added = AddTargetsFromFiles(g, files);
        if (added.Count > 0) SetCurrentTarget(g, added[0]);
        RefreshTargets(g);
        StatusText.Text = added.Count switch
        {
            0 => "追加できるファイルがありませんでした",
            1 => $"起動設定を「{added[0].Display}」にしました：{g.Title}",
            _ => $"{added.Count} 件を追加し、「{added[0].Display}」を使うようにしました：{g.Title}",
        };
    }

    // ================================================================
    // 起動するもの（1作品に複数。コンプリートボックスなど）
    // ================================================================

    /// <summary>ファイルを「起動するもの」に追加する（ショートカットはリンク先に変換）</summary>
    private List<LaunchTarget> AddTargetsFromFiles(Game g, IEnumerable<string> files)
    {
        var added = new List<LaunchTarget>();
        foreach (var file in files)
        {
            if (System.IO.Directory.Exists(file)) continue; // フォルダは対象外
            var target = ShortcutResolver.Resolve(file);
            if (string.IsNullOrWhiteSpace(target)) continue;

            // 名前：ショートカットならその名前、exe なら製品名（どちらも作品名になっていることが多い）
            var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            var label = ext is ".lnk" or ".url" ? System.IO.Path.GetFileNameWithoutExtension(file) : ProductNameOf(target) ?? "";
            var monitor = ShortcutResolver.IsUrl(target) ? GameScanner.FindSteamMonitorProcess(target) : null;
            added.Add(_db.AddTarget(g.Id, label, target, monitor));
        }
        return added;
    }

    private static string? ProductNameOf(string path)
    {
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path)) return null;
            var name = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>今使うものを切り替える（一覧の「起動設定」と「▶ 起動」に使われる）</summary>
    private void SetCurrentTarget(Game g, LaunchTarget? t)
    {
        g.ExePath = t?.Path;
        g.MonitorProcess = t?.MonitorProcess;
        // Steam ならインストール先フォルダで判定する。それ以外はファイルのフォルダで判定するので空にする
        g.InstallDir = t != null && t.IsUrl ? GameScanner.FindSteamInstallDir(t.Path) : null;
        _db.Update(g);
    }

    /// <summary>作品の「起動するもの」を読み直して、一覧と詳細欄に反映する（入力中の詳細欄はそのまま）</summary>
    private void RefreshTargets(Game g, long? selectTargetId = null)
    {
        g.Targets = _db.GetTargets(g.Id);
        foreach (var t in g.Targets)
            t.IsCurrent = string.Equals(t.Path, g.ExePath, StringComparison.OrdinalIgnoreCase);

        if (ReferenceEquals(Selected, g))
        {
            TargetGrid.ItemsSource = g.Targets;
            if (selectTargetId != null) TargetGrid.SelectedItem = g.Targets.FirstOrDefault(t => t.Id == selectTargetId);
        }
        GameGrid.Items.Refresh();
        _tracker.InvalidateGames();
    }

    private LaunchTarget? SelectedTarget => TargetGrid.SelectedItem as LaunchTarget;

    private void UseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game g || SelectedTarget is not LaunchTarget t) return;
        SetCurrentTarget(g, t);
        RefreshTargets(g, t.Id);
        StatusText.Text = $"起動設定を「{t.Display}」にしました：{g.Title}";
    }

    private void AddTargetFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game g) return;
        var dlg = new OpenFileDialog
        {
            Filter = "実行ファイル・ショートカット (*.exe;*.lnk;*.url;*.bat)|*.exe;*.lnk;*.url;*.bat|すべてのファイル (*.*)|*.*",
            Multiselect = true,
            DereferenceLinks = false, // .lnk は自分で解決する（名前を使うため）
        };
        if (dlg.ShowDialog(this) != true) return;
        AddFilesToSelected(g, dlg.FileNames);
    }

    private void TargetGrid_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void TargetGrid_PreviewDrop(object sender, DragEventArgs e)
    {
        if (Selected is Game g && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            AddFilesToSelected(g, files);
        e.Handled = true;
    }

    private void AddFilesToSelected(Game g, IEnumerable<string> files)
    {
        var added = AddTargetsFromFiles(g, files);
        if (added.Count == 0) return;
        if (g.IsLaunchUnset) SetCurrentTarget(g, added[0]); // 初めての登録なら、それを使う
        RefreshTargets(g, added[^1].Id);
        StatusText.Text = $"{added.Count} 件を追加しました：{g.Title}";
    }

    /// <summary>名前・ファイル・監視の欄を書き換えたら保存する</summary>
    private void TargetGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not LaunchTarget t || Selected is not Game g) return;
        // 入力内容が反映されてから保存する
        Dispatcher.InvokeAsync(() =>
        {
            t.Path = ShortcutResolver.Resolve(t.Path.Trim().Trim('"'));
            t.Label ??= "";
            _db.UpdateTarget(t);
            if (t.IsCurrent) SetCurrentTarget(g, t); // 今使うものなら、起動設定にも反映
            RefreshTargets(g, t.Id);
        }, DispatcherPriority.Background);
    }

    private void MoveTargetUp_Click(object sender, RoutedEventArgs e) => MoveTarget(-1);
    private void MoveTargetDown_Click(object sender, RoutedEventArgs e) => MoveTarget(+1);

    private void MoveTarget(int delta)
    {
        if (Selected is not Game g || SelectedTarget is not LaunchTarget t) return;
        var list = g.Targets;
        var i = list.IndexOf(t);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        for (var k = 0; k < list.Count; k++)
        {
            list[k].SortOrder = k;
            _db.UpdateTarget(list[k]);
        }
        RefreshTargets(g, t.Id);
    }

    private void DeleteTarget_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not Game g || SelectedTarget is not LaunchTarget t) return;
        if (MessageBox.Show($"「{t.Display}」を起動するものの一覧から外しますか？（ファイル自体は消えません）",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _db.DeleteTarget(t.Id);
        if (t.IsCurrent)
        {
            // 今使うものを外したら、残りの先頭を使う（なければ未設定）
            var next = g.Targets.FirstOrDefault(x => x.Id != t.Id);
            SetCurrentTarget(g, next);
        }
        RefreshTargets(g);
    }

    /// <summary>一覧の「起動設定」のプルダウンで切り替えたとき</summary>
    private void RowTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: Game g } cb || cb.SelectedItem is not LaunchTarget t) return;
        if (ReferenceEquals(t, g.CurrentTarget)) return; // 表示を合わせただけ（切り替えではない）
        SetCurrentTarget(g, t);
        RefreshTargets(g);
        StatusText.Text = $"起動設定を「{t.Display}」にしました：{g.Title}";
    }

    private void Launch_Click(object sender, RoutedEventArgs e) => Launch();

    private void Launch()
    {
        var g = Selected;
        if (g == null) return;
        try
        {
            _tracker.Launch(g);
            StatusText.Text = $"起動しました：{g.Title}（ゲームが動き出すと、自動でプレイ時間の計測を始めます）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "起動できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================
    // プレイ時間の計測（PlayTimeTracker からの知らせ。UI スレッドで呼ばれる）
    // ================================================================

    private void OnSessionStarted(string title)
    {
        StatusText.Text = $"計測を始めました：{title}";
        if (_cfg.NotifyOnStart && !IsActive) _tray.Notify("プレイ時間の計測を開始", title);
        Reload(); // 未プレイ → プレイ中 に変わることがある
    }

    private void OnSessionEnded(string title, double seconds)
    {
        var minutes = (int)(seconds / 60);
        StatusText.Text = seconds < 10
            ? $"終了しました：{title}（短すぎるため記録していません）"
            : $"プレイ時間を記録しました：{title}（{minutes}分）";
        if (_cfg.NotifyOnStart && !IsActive && seconds >= 10)
            _tray.Notify("プレイ時間を記録しました", $"{title}（{minutes}分）");
        _tray.SetText("NovelKeeper");
        Reload();
    }

    private void OnTracking(IReadOnlyList<ActiveSessionInfo> list)
    {
        if (list.Count == 0) return;
        var text = string.Join("、", list.Select(x =>
            $"{x.Title} {(int)(x.Seconds / 60)}分" + (x.Counting ? "" : "（一時停止中）")));
        StatusText.Text = "計測中：" + text;
        _tray.SetText("計測中：" + text);
    }

    // ================================================================
    // 常駐（通知領域）
    // ================================================================

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !_cfg.CloseToTray)
        {
            _exiting = true; // 本当に閉じる（Closed で計測中の記録を保存する）
            _cfg.Save();     // 別ウィンドウの位置なども保存
            return;
        }

        e.Cancel = true;
        _floatWindow?.Hide();
        Hide();
        if (!_trayNotified)
        {
            _tray.Notify("常駐しています",
                "プレイ時間の計測を続けます。終了するときは、このアイコンを右クリック →「終了」を選んでください。");
            _trayNotified = true;
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ApplyDetailLayout(); // 別ウィンドウの詳細欄も出し直す
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
        Application.Current.Shutdown();
    }

    private void ShowWelcome()
    {
        MessageBox.Show(
            "NovelKeeper へようこそ。最初に次の順で設定してください。\n\n" +
            "① 「ログイン設定」タブで、批評空間のユーザーIDとパスワードを登録\n" +
            "② 「批評空間連携」タブの「⇩ 批評空間から一括取り込み」で、自分の登録作品を一覧に追加\n" +
            "③ 「ゲーム起動設定」タブでゲームのフォルダを追加し、「🔍 実行ファイルを自動検出」\n" +
            "④ 残った作品は、一覧の「起動設定」欄（⬇ ここにドロップ）にショートカットをドラッグ\n\n" +
            "プレイ時間は、ゲームを起動すると自動で計測します。\n" +
            "数え方や常駐の設定は「詳細設定」タブで変更できます。",
            "はじめに", MessageBoxButton.OK, MessageBoxImage.Information);
        _cfg.WelcomeShown = true;
        _cfg.Save();
        OpenLoginTab();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ================================================================
    // 詳細欄の配置（右に縦長／下に横長／非表示）
    // ================================================================

    private const string DetailDragFormat = "NovelKeeper.DetailPanel";
    private Point _detailDragStart;
    private bool _detailDragging;

    private Window? _floatWindow;   // 「自由な位置」に置いたときの別ウィンドウ
    private bool _detailDragCanceled;

    /// <summary>設定に合わせて、一覧と詳細欄を並べ直す（右／下／別ウィンドウ／非表示）</summary>
    private void ApplyDetailLayout()
    {
        var visible = _cfg.DetailVisible;
        var position = _cfg.DetailPosition;
        DetailToggleButton.Content = visible ? "詳細欄を隠す" : "詳細欄を表示";
        SyncDetailPositionBox();
        Dispatcher.InvokeAsync(UpdateToolbarOverflow, DispatcherPriority.Loaded); // ボタンの文字の長さが変わるため

        // ---- 詳細欄を、一覧の画面と別ウィンドウのどちらに置くか ----
        if (position == "Float")
        {
            var w = EnsureFloatWindow();
            if (!ReferenceEquals(w.Content, DetailBorder))
            {
                ContentGrid.Children.Remove(DetailBorder);
                w.Content = DetailBorder;
            }
        }
        else if (!ContentGrid.Children.Contains(DetailBorder))
        {
            if (_floatWindow != null) _floatWindow.Content = null;
            ContentGrid.Children.Add(DetailBorder);
        }

        // ---- 一覧の画面の並べ方 ----
        ContentGrid.RowDefinitions.Clear();
        ContentGrid.ColumnDefinitions.Clear();
        foreach (UIElement child in ContentGrid.Children)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
        }

        var docked = visible && position != "Float";
        DetailSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        DetailBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (position == "Float")
        {
            DetailBorder.BorderThickness = new Thickness(0);
            MoveDetailButton.Content = "⇥ 右に戻す";
            if (visible && IsVisible) ShowFloatWindow();
            else _floatWindow?.Hide();
            return;
        }
        _floatWindow?.Hide();
        if (!docked) return;

        if (position == "Bottom")
        {
            // 下に横長：一覧 ／ 境目 ／ 詳細欄
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cfg.DetailHeight), MinHeight = 120 });
            Grid.SetRow(DetailSplitter, 1);
            Grid.SetRow(DetailBorder, 2);

            DetailSplitter.Height = 6;
            DetailSplitter.Width = double.NaN;
            DetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            DetailSplitter.ResizeDirection = GridResizeDirection.Rows;
            DetailSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            DetailSplitter.Cursor = Cursors.SizeNS;
            DetailBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            MoveDetailButton.Content = "⇥ 右へ";
        }
        else
        {
            // 右に縦長：一覧 ｜ 境目 ｜ 詳細欄
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cfg.DetailWidth), MinWidth = 260 });
            Grid.SetColumn(DetailSplitter, 1);
            Grid.SetColumn(DetailBorder, 2);

            DetailSplitter.Width = 6;
            DetailSplitter.Height = double.NaN;
            DetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            DetailSplitter.ResizeDirection = GridResizeDirection.Columns;
            DetailSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            DetailSplitter.Cursor = Cursors.SizeWE;
            DetailBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            MoveDetailButton.Content = "⤓ 下へ";
        }
    }

    /// <summary>別ウィンドウ（自由な位置に置ける詳細欄）を用意する</summary>
    private Window EnsureFloatWindow()
    {
        if (_floatWindow != null) return _floatWindow;

        var w = new Window
        {
            Title = "作品の詳細",
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(280, _cfg.FloatWidth),
            Height = Math.Max(200, _cfg.FloatHeight),
        };
        // ×で閉じたら「非表示」にする（ウィンドウ自体は使い回す）
        w.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            _cfg.DetailVisible = false;
            SaveLayout();
        };
        w.LocationChanged += (_, _) => { if (w.IsVisible) { _cfg.FloatLeft = w.Left; _cfg.FloatTop = w.Top; } };
        w.SizeChanged += (_, _) => { if (w.IsVisible) { _cfg.FloatWidth = w.Width; _cfg.FloatHeight = w.Height; } };
        _floatWindow = w;
        return w;
    }

    private void ShowFloatWindow()
    {
        var w = EnsureFloatWindow();

        // 位置が未設定、または画面の外（モニター構成が変わったなど）なら、一覧の右寄りに出す
        var left = _cfg.FloatLeft ?? double.NaN;
        var top = _cfg.FloatTop ?? double.NaN;
        var onScreen = !double.IsNaN(left) && !double.IsNaN(top) &&
                       left > SystemParameters.VirtualScreenLeft - 50 &&
                       top > SystemParameters.VirtualScreenTop - 50 &&
                       left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                       top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
        if (!onScreen)
        {
            left = Left + Math.Max(0, ActualWidth - w.Width - 40);
            top = Top + 80;
        }
        w.Left = left;
        w.Top = top;
        if (w.Owner == null) w.Owner = this; // 一覧の画面が表示されてからでないと設定できない
        if (!w.IsVisible) w.Show();
    }

    private void SaveLayout()
    {
        _cfg.Save();
        ApplyDetailLayout();
    }

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailVisible = !_cfg.DetailVisible;
        SaveLayout();
    }

    private void HideDetail_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailVisible = false;
        SaveLayout();
    }

    private void ToggleDetailPosition_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailPosition = _cfg.DetailPosition == "Right" ? "Bottom" : "Right"; // 下→右、別ウィンドウ→右
        SaveLayout();
    }

    /// <summary>境目をドラッグして大きさを変えたら、その大きさを覚えておく</summary>
    private void DetailSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_cfg.DetailPosition == "Bottom" && ContentGrid.RowDefinitions.Count == 3)
            _cfg.DetailHeight = Math.Round(ContentGrid.RowDefinitions[2].ActualHeight);
        else if (ContentGrid.ColumnDefinitions.Count == 3)
            _cfg.DetailWidth = Math.Round(ContentGrid.ColumnDefinitions[2].ActualWidth);
        _cfg.Save();
    }

    // ---- 見出しのドラッグで移動 ----

    private void DetailHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _detailDragStart = e.GetPosition(this);
    }

    private void DetailHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_detailDragging || e.LeftButton != MouseButtonState.Pressed) return;

        // 見出しの中のボタンを押しているときはドラッグしない
        for (var d = e.OriginalSource as DependencyObject; d != null && d != DetailHeader; d = ParentOf(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _detailDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _detailDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _detailDragging = true;
        _detailDragCanceled = false;
        DropOverlay.Visibility = Visibility.Visible;
        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(DetailHeader, new DataObject(DetailDragFormat, "detail"), DragDropEffects.Move);
        }
        finally
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            SetZoneHighlight(DropRight, false);
            SetZoneHighlight(DropBottom, false);
            _detailDragging = false;
        }

        // 「右」「下」の置き場所以外で離した → 別ウィンドウにして、その位置に置く（Esc で取り消したときは何もしない）
        if (result == DragDropEffects.None && !_detailDragCanceled)
        {
            var cursor = CursorPositionInDip();
            _cfg.DetailPosition = "Float";
            _cfg.DetailVisible = true;
            _cfg.FloatLeft = cursor.X - 60;
            _cfg.FloatTop = cursor.Y - 15;
            SaveLayout();
        }
    }

    private void DetailHeader_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _detailDragCanceled = true;
    }

    /// <summary>マウスカーソルの位置（画面の拡大率を考慮した WPF の座標）</summary>
    private Point CursorPositionInDip()
    {
        var p = System.Windows.Forms.Cursor.Position; // 実ピクセル
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(new Point(p.X, p.Y));
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border zone && e.Data.GetDataPresent(DetailDragFormat)) SetZoneHighlight(zone, true);
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border zone) SetZoneHighlight(zone, false);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DetailDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string position } || !e.Data.GetDataPresent(DetailDragFormat)) return;
        _cfg.DetailPosition = position;
        _cfg.DetailVisible = true;
        e.Effects = DragDropEffects.Move; // 置き場所に置いた（別ウィンドウにはしない）
        e.Handled = true;
        Dispatcher.InvokeAsync(SaveLayout); // ドラッグが終わってから並べ直す
    }

    private static void SetZoneHighlight(Border zone, bool on)
    {
        zone.Background = new SolidColorBrush(on ? Color.FromArgb(0x70, 0x4A, 0x90, 0xE2) : Color.FromArgb(0x26, 0x4A, 0x90, 0xE2));
    }

    /// <summary>詳細設定タブを開く（通知領域のメニューからも使う）</summary>
    private void OpenSettings()
    {
        MainTabs.SelectedItem = SettingsTab;
    }

    // ================================================================
    // 詳細の編集・保存
    // ================================================================

    private void PlayedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingDetail) return;
        var played = PlayedCheck.IsChecked == true;
        StatusBox.IsEnabled = !played;
        if (played && ClearDatePicker.SelectedDate == null)
            ClearDatePicker.SelectedDate = DateTime.Today; // クリア日の初期値は今日
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;

        // 得点のチェック（空欄は「未入力」）
        int? score = null;
        var scoreText = ScoreBox.Text.Trim();
        if (scoreText != "")
        {
            if (!int.TryParse(scoreText, out var sv) || sv < 0 || sv > 100)
            {
                MessageBox.Show("得点は 0～100 の整数で入力してください。", "入力エラー");
                return;
            }
            score = sv;
        }

        // 得点登録フォームのその他の項目
        var review = ReadReviewFromUi(g);
        if (review == null) return;

        var oldPlayed = g.IsPlayed;
        var oldClear = g.ClearDate;
        var oldScore = g.MyScore;
        var oldDropped = g.Status == GameStatus.Dropped;
        var changedKeys = Review.Diff(g.Review, review);
        g.Review = review;
        g.MyScore = score;

        // 起動するもの（TargetGrid）は、変更したその場で保存しているので、ここでは扱わない
        g.ClearDate = ClearDatePicker.SelectedDate?.Date;
        g.Status = PlayedCheck.IsChecked == true
            ? GameStatus.Played
            : StatusBox.SelectedIndex switch
            {
                1 => GameStatus.Playing,
                2 => GameStatus.Dropped,
                _ => GameStatus.Unplayed,
            };
        if (oldDropped != (g.Status == GameStatus.Dropped)) changedKeys.Add("giveup"); // 中断＝批評空間の「放棄」
        if (oldPlayed != g.IsPlayed) changedKeys.Add("played");
        if (oldClear?.Date != g.ClearDate?.Date) changedKeys.Add("cleardate");
        if (oldScore != g.MyScore && g.MyScore != null) changedKeys.Add("score"); // 得点を空欄にしても、批評空間側は消さない
        if (g.EgsId != null) g.PendingReview.UnionWith(changedKeys); // 反映するまで「未反映」として覚えておく
        _db.Update(g);
        _detailDirty = false;
        Reload();
        StatusText.Text = "保存しました";

        if (g.EgsId == null || g.PendingReview.Count == 0) return;
        if (PushOnSaveCheck.IsChecked != true)
        {
            StatusText.Text = "保存しました（批評空間には未反映です。次の同期で反映します）";
            return;
        }
        if (!BeginEgsWork(lockDetail: false, quiet: true))
        {
            StatusText.Text = "保存しました（批評空間と通信中のため、反映は次の同期で行います）";
            return;
        }
        try
        {
            await PushToEgsAsync(g.Id);
        }
        finally
        {
            EndEgsWork();
        }
    }

    private void OpenEgsPage_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.EgsId is int id) OpenUrl(_egs.GamePageUrl(id));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var g = Selected;
        if (g == null) return;
        var ok = MessageBox.Show($"「{g.Title}」を一覧から削除しますか？\n（プレイ時間の記録も消えます。批評空間側は変わりません）",
            "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        _db.Delete(g.Id);
        Reload();
    }

    // ================================================================
    // 批評空間との連動
    // ================================================================

    /// <summary>
    /// ツール → 批評空間：まだ反映していない項目（PendingReview）だけを送り、読み直して確認する。
    ///  - 送る直前と確認の後に、作品を DB から読み直す（通信中に画面で保存された内容を、古い内容で上書きしないため）
    ///  - 批評空間で実際に変わったことを確かめた項目だけを「反映済み」にする
    /// </summary>
    private async Task<bool> PushToEgsAsync(long gameId)
    {
        var g = _db.Get(gameId);
        if (g?.EgsId is not int egsId || g.PendingReview.Count == 0) return true;

        var keys = g.PendingReview.ToList();
        var pushPlayed = keys.Contains("played") || keys.Contains("cleardate");
        var pushScore = keys.Contains("score") && g.MyScore != null;
        var reviewKeys = keys.Where(k => k is not ("played" or "cleardate" or "score")).ToList();

        // 送った内容を控えておく（確認と、通信中に変更されたかの判定に使う）
        var sentReview = g.Review.Clone();
        var sentPlayed = g.IsPlayed;
        var sentClear = g.ClearDate?.Date;
        var sentScore = g.MyScore;
        var sentDropped = g.Status == GameStatus.Dropped;

        try
        {
            StatusText.Text = $"批評空間に反映中：{g.Title}";
            await _egs.UpdateReviewAsync(egsId,
                pushPlayed ? sentPlayed : null,
                pushPlayed && sentPlayed ? g.ClearDate : null,
                pushScore ? sentScore : null,
                reviewKeys.Count > 0 ? sentReview.ToFormValues(reviewKeys, sentDropped) : null);
        }
        catch (Exception ex)
        {
            Log.Warn("批評空間への反映に失敗", ex);
            StatusText.Text = "批評空間への反映に失敗しました（未反映の項目は、次の同期でまた反映します）";
            var r = MessageBox.Show(
                $"批評空間への反映に失敗しました。\n\n{ex.Message}\n\n得点登録ページをブラウザで開きますか？（手動で登録できます）",
                "反映エラー", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) OpenUrl(_egs.ReviewFormUrl(egsId));
            return false;
        }

        // ---- 批評空間を読み直して、反映されたか確かめる ----
        EgsUserRecord? rec = null;
        Review? remote = null;
        var checkedOk = false;
        try
        {
            var cred = CredentialStore.Load();
            if (cred != null)
            {
                rec = (await _egs.GetUserRecordsAsync(cred.Value.User)).FirstOrDefault(x => x.GameId == egsId);
                if (reviewKeys.Count > 0)
                    remote = (await _egs.GetReviewsAsync(cred.Value.User, egsId)).GetValueOrDefault(egsId);
                checkedOk = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("反映の確認に失敗", ex);
        }

        var fresh = _db.Get(gameId);
        if (fresh == null) return true; // 通信中に一覧から削除された

        var done = new List<string>();
        if (checkedOk)
        {
            foreach (var key in keys)
            {
                var reflected = key switch
                {
                    "played" => (rec?.Played ?? false) == sentPlayed,
                    "cleardate" => !sentPlayed || sentClear == null || rec?.ClearDate?.Date == sentClear,
                    "score" => sentScore == null || rec?.Score == sentScore,
                    "giveup" => (rec?.GiveUp ?? false) == sentDropped,
                    _ => remote != null && !remote.Unreadable.Contains(key) && !Review.Diff(sentReview, remote).Contains(key),
                };
                // 通信中に画面で同じ項目を変更していたら、その変更はまだ未反映のまま残す
                var unchangedLocally = key switch
                {
                    "played" => fresh.IsPlayed == sentPlayed,
                    "cleardate" => fresh.ClearDate?.Date == sentClear,
                    "score" => fresh.MyScore == sentScore,
                    "giveup" => (fresh.Status == GameStatus.Dropped) == sentDropped,
                    _ => !Review.Diff(sentReview, fresh.Review).Contains(key),
                };
                if (reflected && unchangedLocally) done.Add(key);
            }
            SyncEngine.ApplyEgsState(fresh, rec);
            if (remote != null && !fresh.PendingReview.Contains("possession")) fresh.EgsPossession = remote.Possession;
        }

        fresh.PendingReview.ExceptWith(done);
        if (done.Contains("possession")) fresh.EgsPossession = sentReview.Possession;
        _db.UpdateSyncState(fresh); // 同期の状態だけを書く（画面で保存した内容には触れない）
        Reload();

        var notYet = keys.Except(done).ToList();
        StatusText.Text = !checkedOk
            ? $"送信しましたが、反映を確認できませんでした：{g.Title}（次の同期で確認します）"
            : notYet.Count == 0
                ? $"批評空間に反映しました：{g.Title}"
                : $"送信しましたが、反映を確認できなかった項目があります：{string.Join("、", notYet.Select(PendingLabel))}（次の同期でまた反映します）";
        return true;
    }

    /// <summary>未反映の項目の、画面での名前</summary>
    private static string PendingLabel(string key) => key switch
    {
        "played" => "プレイ済",
        "cleardate" => "終了日",
        "score" => "得点",
        _ => Review.Keys.FirstOrDefault(k => k.Key == key).Label ?? key,
    };

    /// <summary>批評空間 → ツール：自分の記録を読み込んで突き合わせる</summary>
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var cred = CredentialStore.Load();
        if (cred == null)
        {
            MessageBox.Show("先に「ログイン設定」タブで、批評空間のIDとパスワードを登録してください。");
            return;
        }
        if (!BeginEgsWork(lockDetail: true)) return;
        try
        {
            await SyncAsync(cred.Value.User);
        }
        finally
        {
            EndEgsWork();
        }
    }

    private async Task SyncAsync(string uid)
    {
        List<EgsUserRecord> records;
        try
        {
            StatusText.Text = "批評空間から読み込み中…";
            records = await _egs.GetUserRecordsAsync(uid);
        }
        catch (Exception ex)
        {
            Log.Warn("批評空間からの読み込みに失敗（同期）", ex);
            StatusText.Text = "読み込みに失敗しました";
            MessageBox.Show(ex.Message, "同期エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SyncEngine.LooksLikeLostRecords(_db.GetAll(), records.Count) &&
            MessageBox.Show("批評空間の記録が0件でした。取得に失敗している可能性があります。\n\n" +
                            "このまま同期すると、プレイ済の作品がすべて「批評空間に未登録」として扱われます。\n続けますか？",
                "同期の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            StatusText.Text = "同期を中止しました（批評空間の記録が0件でした）";
            return;
        }

        var byId = records.GroupBy(r => r.GameId).ToDictionary(x => x.Key, x => x.First());

        // 得点登録フォームのその他の項目（所持・感想など）
        Dictionary<int, Review>? reviews = null;
        try
        {
            StatusText.Text = "感想などを読み込み中…";
            reviews = await _egs.GetReviewsAsync(uid);
        }
        catch (Exception ex)
        {
            Log.Warn("感想などの読み込みに失敗", ex);
            MessageBox.Show("感想などの項目を読み込めませんでした（プレイ済・得点などの同期は続けます）。\n\n" + ex.Message,
                "同期", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 中央値・平均値・データ数・発売日を最新にする（失敗しても同期は続ける）
        var infos = new Dictionary<int, EgsGameInfo>();
        try
        {
            StatusText.Text = "作品の情報（中央値など）を更新中…";
            var ids = _db.GetAll().Where(x => x.EgsId != null).Select(x => x.EgsId!.Value).ToList();
            foreach (var info in await _egs.GetGameInfosAsync(ids)) infos[info.Id] = info;
        }
        catch (Exception ex)
        {
            Log.Warn("作品の情報の更新に失敗", ex);
        }

        // 読み込みが終わってから最新の内容を読む（読み込み中に保存された内容も含める）
        var stats = new SyncStats();
        var resolver = new DialogConflictResolver(this);
        foreach (var g in _db.GetAll().Where(x => x.EgsId != null))
        {
            var egsId = g.EgsId!.Value;
            byId.TryGetValue(egsId, out var rec);
            infos.TryGetValue(egsId, out var info);
            Review? remote = null;
            reviews?.TryGetValue(egsId, out remote);
            SyncEngine.ApplyRecord(g, rec, info, remote, resolver, stats);
            _db.UpdateFromSync(g); // 起動設定など、同期と関係のない項目は書き換えない
        }
        _detailDirty = false; // 同期中は詳細欄を編集できないので、読み直してよい
        Reload();
        StatusText.Text = $"同期しました（プレイ済にした作品：{stats.Imported}件、中断にした作品：{stats.Dropped}件、得点を取り込んだ作品：{stats.ScoreImported}件）";

        // ---- ツールの内容を批評空間に反映する ----
        var toPush = _db.GetAll().Where(x => x.EgsId != null && x.PendingReview.Count > 0).ToList();
        if (toPush.Count == 0) return;
        var ok = MessageBox.Show(
            $"ツール側の内容を批評空間に反映する作品が {toPush.Count} 件あります。\n\n" +
            string.Join("\n", toPush.Take(10).Select(x => "・" + x.Title)) +
            (toPush.Count > 10 ? "\n…" : "") + "\n\n反映しますか？\n（［いいえ］の場合は「未反映」のまま残し、次の同期でまた確認します）",
            "批評空間へ反映", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        foreach (var pushGame in toPush)
        {
            if (!await PushToEgsAsync(pushGame.Id)) break; // 失敗したらそこで止める
        }
    }

    /// <summary>批評空間 → ツール：自分が登録している作品をまとめて一覧に追加する</summary>
    private async void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        var cred = CredentialStore.Load();
        if (cred == null)
        {
            MessageBox.Show("先に「ログイン設定」タブで、批評空間のIDとパスワードを登録してください。");
            return;
        }

        var dlg = new ImportWindow { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var scope = dlg.Scope;
        if (!BeginEgsWork(lockDetail: true)) return;
        try
        {

            List<EgsMyGame> myGames;
            try
            {
                StatusText.Text = "批評空間から読み込み中…";
                myGames = await _egs.GetMyGamesAsync(cred.Value.User, scope);
            }
            catch (Exception ex)
            {
                Log.Warn("批評空間からの読み込みに失敗（一括取り込み）", ex);
                StatusText.Text = "読み込みに失敗しました";
                MessageBox.Show(ex.Message, "取り込みエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 所持・感想など（読めなくても取り込みは続ける）
            var reviews = new Dictionary<int, Review>();
            try
            {
                StatusText.Text = "感想などを読み込み中…";
                reviews = await _egs.GetReviewsAsync(cred.Value.User);
            }
            catch (Exception ex)
            {
                Log.Warn("感想などの読み込みに失敗", ex);
            }

            int added = 0, skipped = 0, scoreFilled = 0;
            var existing = _db.GetAll()
                .Where(x => x.EgsId != null)
                .GroupBy(x => x.EgsId!.Value)
                .ToDictionary(x => x.Key, x => x.First());

            foreach (var item in myGames)
            {
                if (existing.TryGetValue(item.Game.Id, out var old))
                {
                    // すでに一覧にある作品：得点が未入力なら、批評空間の得点を入れる
                    old.EgsScore = item.Score;
                    if (reviews.TryGetValue(item.Game.Id, out var oldRemote))
                        SyncEngine.ApplyRemoteReview(old, oldRemote, null); // 未反映の項目はツールの内容を残す
                    if (old.MyScore == null && item.Score != null)
                    {
                        old.MyScore = item.Score;
                        scoreFilled++;
                    }
                    _db.UpdateFromSync(old);
                    skipped++;
                    continue;
                }

                var g = new Game
                {
                    EgsId = item.Game.Id,
                    Title = item.Game.Title,
                    Brand = item.Game.Brand,
                    SellDay = item.Game.SellDay,
                    Median = item.Game.Median,
                    EgsAverage = item.Game.Average,
                    EgsCount = item.Game.Count,
                    // 批評空間で「放棄」→ 中断（プレイ済の☑より優先）
                    Status = item.GiveUp ? GameStatus.Dropped
                           : item.Played ? GameStatus.Played
                           : GameStatus.Unplayed,
                    ClearDate = item.Played && !item.GiveUp ? item.ClearDate : null,
                    MyScore = item.Score,
                    Review = reviews.TryGetValue(item.Game.Id, out var remote) ? remote.Clone() : new Review { Possession = item.Possession },
                };
                _db.Insert(g);

                // 批評空間側の状態も記録しておく
                g.EgsPlayed = item.Played;
                g.EgsGiveUp = item.GiveUp;
                g.EgsPossession = item.Possession;
                g.EgsScore = item.Score;
                g.EgsClearDate = item.ClearDate;
                g.EgsSyncedAt = DateTime.Now;
                _db.Update(g);
                added++;
            }

            Reload();
            StatusText.Text = $"一括取り込み完了：{added} 件追加、得点を取り込んだ作品 {scoreFilled} 件";
            MessageBox.Show(
                $"{added} 件を追加しました。\n" +
                $"すでに一覧にあった {skipped} 件のうち、得点が未入力だった {scoreFilled} 件に批評空間の得点を入れました。\n" +
                "（プレイ済・クリア日などの状態をそろえたいときは「批評空間と同期」を押してください）\n\n" +
                "起動するには、各作品の実行ファイルを右側の詳細欄で設定してください。",
                "一括取り込み");
        }
        finally
        {
            EndEgsWork();
        }
    }

    // ================================================================
    // 設定
    // ================================================================

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        new LoginWindow(_egs) { Owner = this }.ShowDialog();
        UpdateLoginState();
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), $"\"{AppPaths.Config}\"") { UseShellExecute = true });
        MessageBox.Show("設定を書き換えて保存したら、このアプリを再起動してください。", "連携設定");
    }

    // ================================================================

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>ブラウザで開く（http / https のときだけ）</summary>
    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
