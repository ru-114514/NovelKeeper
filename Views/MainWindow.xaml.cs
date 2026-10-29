using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

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
        InitInputHelpers();
        // タイトルにバージョンを表示（build.bat で指定したバージョン）
        Title = $"NovelKeeper {AppVersion.Text} ― ノベルゲーム ランチャー";
        _cfg = App.Config;
        AutoBackup.RunScheduled(_cfg);
        _db = new Database(AppPaths.Database);
        // 常駐したまま日をまたいでも、間隔が来ていればバックアップを取る（設定は、そのときの内容を使う）
        var backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        backupTimer.Tick += (_, _) => Task.Run(() => AutoBackup.RunScheduled(_cfg));
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
            SummaryView.CloseSummaryFloats();
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
        ApplyCategoryColors();
        FilterBox.SelectedIndex = 0; // ここで一覧が読み込まれる

        ShowBeforePurchaseCheck.IsChecked = _cfg.ShowBeforePurchase;
        // タブの画面（UserControl）とつなぐ。各タブの読み込みより先に
        ExportView.Init(this);
        SummaryView.Init(this);
        PostView.Init(this);
        InitSettingsTab();
        SummaryView.InitSummaryTab();
        MainTabs.SelectionChanged += (s, e) =>
        {
            // タブの中の一覧などの選択変更でも届くので、タブ自身の切り替えだけ見る
            if (e.OriginalSource != MainTabs) return;
            SummaryView.ApplySummaryLayout(); // 別ウィンドウの欄は、レポートのタブを開いている間だけ出す
            if (ReferenceEquals(MainTabs.SelectedItem, SummaryTab)) SummaryView.RefreshSummary();
        };
        PostView.InitPostTab();
        MainTabs.SelectionChanged += (_, e) =>
        {
            // タブの中の選択変更でも届くので、タブ自身の切り替えだけ見る。開くたびに、集計し直す
            if (e.OriginalSource == MainTabs && ReferenceEquals(MainTabs.SelectedItem, PostTab)) PostView.RefreshPost();
        };
        ExportView.InitExportTab();
        MainTabs.SelectionChanged += (_, e) =>
        {
            // タブの中の選択変更でも届くので、タブ自身の切り替えだけ見る。開くたびに、対象の件数を数え直す
            if (e.OriginalSource == MainTabs && ReferenceEquals(MainTabs.SelectedItem, ExportTab)) ExportView.RefreshExportPanel();
        };

        // 保存されているテーマを選択欄に表示（App で適用済み）
        _loadingTheme = true;
        ThemeBox.SelectedIndex = _cfg.Theme switch { "Light" => 1, "System" => 2, _ => 0 };
        _loadingTheme = false;
    }

    private Game? Selected => GameGrid.SelectedItem as Game;

    /// <summary>
    /// 発売前（橙）・購入前（青）の作品は、行の背景色を変える。テーマの行は背景色を使わないので、セルに付ける。
    /// テーマのセルのスタイルを土台にするため、テーマを切り替えたあとにも呼ぶ。選択中の行は、いつもの選択色のまま
    /// </summary>
    private void ApplyCategoryColors()
    {
        var baseStyle = GameGrid.TryFindResource(typeof(DataGridCell)) as Style;

        Style RowColorStyle()
        {
            var style = new Style(typeof(DataGridCell), baseStyle);
            void Add(string category, Color color)
            {
                var trigger = new MultiDataTrigger();
                trigger.Conditions.Add(new Condition(new Binding(nameof(Game.Category)), category));
                trigger.Conditions.Add(new Condition(new Binding(nameof(DataGridCell.IsSelected)) { RelativeSource = RelativeSource.Self }, false));
                trigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(color)));
                style.Triggers.Add(trigger);
            }
            Add("発売前", Color.FromArgb(0x55, 0xF2, 0xA3, 0x3A));
            Add("購入前", Color.FromArgb(0x55, 0x4A, 0x8F, 0xE2));
            return style;
        }

        GameGrid.CellStyle = RowColorStyle();

        // 「得点」列の赤い枠は、列のテンプレート（MainWindow.xaml）で表示する

        // 「購入確認」列：購入確認が必要な作品のセルを赤く塗る（選択中の行でも赤のまま）
        var check = GameGrid.Columns.FirstOrDefault(c => ColumnKey(c) == "購入確認");
        if (check != null)
        {
            var style = RowColorStyle();
            var red = new DataTrigger { Binding = new Binding(nameof(Game.NeedsPurchaseCheck)), Value = true };
            red.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F))));
            red.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            red.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            red.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "発売済みですが、所持が付いていません。買ったら「所持」を付けて保存してください"));
            style.Triggers.Add(red);
            check.CellStyle = style;
        }
    }

    // ================================================================
    // 一覧
    // ================================================================

    private void Reload()
    {
        var selectedId = Selected?.Id;
        Game.BacklogExcludedIds = _cfg.BacklogExcludedIds.ToHashSet();
        Game.PriorityIds = _cfg.PostLayout.PriorityIds();
        var every = _db.GetAll();
        if (BacklogHistory.Record(_cfg.BacklogSnapshots, SummaryExtras.BacklogCount(every, _cfg.BacklogExcludedIds.ToHashSet()), DateTime.Today)) _cfg.Save();
        // 購入前の作品は、設定がオフなら省く（「発売前」「購入前」の絞り込みで選んだときは、設定に関係なく出す）
        // ただし、発売済みで購入確認が必要な作品は、見落とさないよう、設定がオフでも出す
        var all = _cfg.ShowBeforePurchase ? every : every.Where(g => !g.IsBeforePurchase || g.NeedsPurchaseCheck).ToList();
        var list = FilterBox.SelectedIndex switch
        {
            6 => every.Where(g => g.IsUnreleased),
            7 => all.Where(g => g.IsReleased),
            8 => every.Where(g => g.IsBeforePurchase),
            9 => every.Where(g => g.NeedsPurchaseCheck),
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
            {
                // 購入額は、未入力を昇順・降順どちらでも最後にする（先に「未入力か」で分ける）
                if (k.Property == "Price") view.SortDescriptions.Add(new SortDescription(nameof(Game.PriceMissing), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(k.Property,
                    k.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            }
        }

        // 見出しの▲▼も合わせる
        foreach (var col in GameGrid.Columns)
        {
            var key = _cfg.SortKeys.FirstOrDefault(k => k.Property == col.SortMemberPath);
            col.SortDirection = key == null
                ? null
                : key.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        }
        RefreshSortPresetButtons();
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
                .Where(d => d.PropertyName != nameof(Game.PriceMissing)) // 補助の条件は保存しない（適用のたびに付け直す）
                .Select(d => new SortKey { Property = d.PropertyName, Descending = d.Direction == ListSortDirection.Descending })
                .ToList();
            _cfg.Save();
            ApplySort(); // 見出しクリックで購入額が加わった場合も、補助の条件を付ける
        }, DispatcherPriority.Background);
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.ApplyTheme(theme);
        ApplyCategoryColors();
        _cfg.Theme = theme;
        _cfg.Save();
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SortWindow(_cfg.SortKeys) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _cfg.SortKeys = dlg.Result;
        _cfg.NormalizeSortPresets();
        if (dlg.SaveToPreset is int save)
            _cfg.SortPresets[save] = dlg.Result.Select(k => new SortKey { Property = k.Property, Descending = k.Descending }).ToList();
        _cfg.Save();
        ApplySort();
        if (dlg.SaveToPreset is int saved) StatusText.Text = $"設定{saved + 1}に保存しました：{DescribeSort(dlg.Result)}";
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
        ShowDetailPriority(g);
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
        DiscardPendingButton.Visibility = g?.PendingReview.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!keepEdits) ShowReview(g);
        _loadingDetail = false;
        if (!keepEdits) ShowImage(g);
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
        if (ReferenceEquals(MainTabs.SelectedItem, SummaryTab)) SummaryView.RefreshSummary();
    }

    /// <summary>いま計測中の作品（補正画面で、計測中の記録を書き換えないために使う）</summary>
    private HashSet<long> _activeGameIds = new();

    private void OnTracking(IReadOnlyList<ActiveSessionInfo> list)
    {
        _activeGameIds = list.Select(x => x.GameId).ToHashSet();
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
        SummaryView.HideSummaryFloats();
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
        SummaryView.ApplySummaryLayout();
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

    // ================================================================

    /// <summary>ブラウザで開く（http / https のときだけ）</summary>
    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}
