using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Models;
using NovelKeeper.Services;
using static NovelKeeper.Views.ViewHelpers;

namespace NovelKeeper.Views;

/// <summary>
/// 「詳細設定」タブ：プレイ時間の数え方・常駐と起動・表示・任意評価基準・一覧の列・保存場所。
/// 設定の保存はここで行い、一覧や詳細欄への反映は ISettingsHost を通してメイン画面に頼む
/// </summary>
public partial class SettingsTabView : UserControl
{
    private ISettingsHost _host = null!;
    private EgsConfig _cfg = null!;
    private bool _loading = true; // 値を読み込んでいる間（と Init の前）は、変更イベントを無視する

    public SettingsTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（InitSettingsTab より先に、1回だけ呼ぶ）</summary>
    internal void Init(ISettingsHost host)
    {
        _host = host;
        _cfg = host.Config;
    }

    internal void InitSettingsTab()
    {
        _loading = true;
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
            SetZoomText(_cfg.ListZoom);
            ShowImagesCheck.IsChecked = _cfg.ShowImages;
            ShowBeforePurchaseCheck.IsChecked = _cfg.ShowBeforePurchase;
            ThemeBox.SelectedIndex = _cfg.Theme switch { "Light" => 1, "System" => 2, _ => 0 }; // テーマは App で適用済み
        }
        finally
        {
            _loading = false;
        }
        SyncDetailPositionBox();
        InitColumnList();

        VersionText.Text = $"NovelKeeper {AppVersion.Text}";
    }

    // ================================================================
    // プレイ時間の数え方・常駐と起動
    // ================================================================

    /// <summary>プレイ時間・常駐の設定が変わったら保存する</summary>
    private void PlaySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

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
            _host.RefreshListRows(); // 一覧の紫を付け直す
            _host.UpdateAdminBadge();
        }
        _cfg.Save();

        if (autoStartChanged)
        {
            try { AutoStart.Set(_cfg.AutoStart); }
            catch (Exception ex) { MessageBox.Show("自動起動の設定に失敗しました：" + ex.Message); }
        }
        _host.SetStatus("設定を保存しました");
    }

    /// <summary>管理者権限で起動する印がついた作品の件数を表示する（一覧の右クリックで印を変えたときにも、メイン画面から呼ぶ）</summary>
    internal void UpdateAdminCount()
    {
        var n = _host.Db.GetAll().Count(x => x.RunAsAdmin);
        AdminCountText.Text = n == 0 ? "（印がついた作品はありません）" : $"（現在 {n} 件）";
        ClearAdminButton.IsEnabled = n > 0;
    }

    private void ClearAdmin_Click(object sender, RoutedEventArgs e)
    {
        var n = _host.Db.GetAll().Count(x => x.RunAsAdmin);
        if (n == 0) { UpdateAdminCount(); return; }
        if (MessageBox.Show($"管理者権限で起動する設定を、{n} 件の作品から外します。\n（「すべての作品を常に管理者権限で起動する」の☑は、そのままです）",
                "確認", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var cleared = _host.Db.ClearAllRunAsAdmin();
        _host.ReloadList();
        UpdateAdminCount();
        _host.UpdateAdminBadge();
        _host.SetStatus($"管理者権限の設定を {cleared} 件の作品から外しました");
    }

    // ================================================================
    // 表示
    // ================================================================

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        App.ApplyTheme(theme);
        _host.ThemeChanged();
        _cfg.Theme = theme;
        _cfg.Save();
    }

    /// <summary>詳細欄の位置の選択欄を、今の状態に合わせる（詳細欄をドラッグで動かしたときにも、メイン画面から呼ぶ）</summary>
    internal void SyncDetailPositionBox()
    {
        if (_cfg == null) return; // つなぐ前（起動の途中）は、InitSettingsTab で合わせる
        var tag = _cfg.DetailVisible ? _cfg.DetailPosition : "Hidden";
        var item = DetailPositionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == tag);
        if (ReferenceEquals(DetailPositionBox.SelectedItem, item)) return;
        var old = _loading;
        _loading = true;
        DetailPositionBox.SelectedItem = item;
        _loading = old;
    }

    private void DetailPositionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DetailPositionBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
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
        _host.ApplyDetailLayout();
    }

    private void ShowBeforePurchase_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.ShowBeforePurchase = ShowBeforePurchaseCheck.IsChecked == true;
        _cfg.Save();
        _host.ReloadList();
    }

    private void CleanupBeforePurchase_Click(object sender, RoutedEventArgs e) => _host.CleanupBeforePurchase();

    /// <summary>一覧の表示倍率の表示（Ctrl＋マウスホイールなどで変えたときにも、メイン画面から呼ぶ）</summary>
    internal void SetZoomText(double zoom) => ZoomText.Text = $"{zoom * 100:0}%";

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => _host.ResetZoom();

    private void ShowImages_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.ShowImages = ShowImagesCheck.IsChecked == true;
        _cfg.Save();
        _host.RefreshImage();
    }

    private void ClearImages_Click(object sender, RoutedEventArgs e) => _host.ClearImages();

    // ================================================================
    // 任意評価基準
    // ================================================================

    /// <summary>「項目ごとの採点を使う」の☑を、今の状態に合わせる（評価基準を保存したときにも、メイン画面から呼ぶ）</summary>
    internal void SetScoreItemsCheck(bool on)
    {
        var old = _loading;
        _loading = true;
        ScoreItemsCheck.IsChecked = on;
        _loading = old;
    }

    private void ScoreItemsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SetScoreItemsCheck(_host.SetScoreItemsEnabled(ScoreItemsCheck.IsChecked == true));
    }

    private void ScoreCriteria_Click(object sender, RoutedEventArgs e) => _host.OpenScoreCriteria();

    // ================================================================
    // 一覧の列（表示・非表示と並び順）
    // ================================================================

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

    /// <summary>列の設定を読み込み直す（一覧の見出しのドラッグや、右クリックで列を切り替えたときにも、メイン画面から呼ぶ）</summary>
    internal void InitColumnList()
    {
        var selectedKey = (ColumnList.SelectedItem as ColumnItem)?.Key;
        _columnItems.Clear();
        foreach (var c in _host.ColumnSettings())
            _columnItems.Add(new ColumnItem { Key = c.Key, Visible = c.Visible, CanHide = !_host.IsLockedColumn(c.Key) });
        ColumnList.ItemsSource = _columnItems;
        ColumnList.SelectedItem = _columnItems.FirstOrDefault(x => x.Key == selectedKey);
    }

    /// <summary>この一覧の内容を保存して、一覧に反映する</summary>
    private void SaveColumnsFromList() =>
        _host.SaveColumns(_columnItems.Select(x => new ColumnSetting { Key = x.Key, Visible = x.Visible || !x.CanHide }).ToList());

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
        _host.ResetColumns();
        InitColumnList();
    }

    // ================================================================
    // 保存場所
    // ================================================================

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenInExplorer(AppPaths.Dir);
}
