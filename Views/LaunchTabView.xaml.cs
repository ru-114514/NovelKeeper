using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 「ゲーム起動設定」タブ：実行ファイルを自動検出するときに探すフォルダ（ゲーム・ショートカット）と、Steam・スタートメニューを探すか。
/// メイン画面とは IMainHost を通してやりとりする
/// </summary>
public partial class LaunchTabView : UserControl
{
    private IMainHost _host = null!;
    private EgsConfig _cfg = null!;
    private bool _loading = true; // 値を読み込んでいる間（と Init の前）は、変更イベントを無視する

    public LaunchTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（InitLaunchTab より先に、1回だけ呼ぶ）</summary>
    internal void Init(IMainHost host)
    {
        _host = host;
        _cfg = host.Config;
    }

    /// <summary>設定の値を画面に読み込む（自動検出の画面でフォルダなどを変えたあとにも呼ぶ）</summary>
    internal void InitLaunchTab()
    {
        _loading = true;
        try
        {
            GameFolderList.ItemsSource = null;
            GameFolderList.ItemsSource = _cfg.GameFolders.ToList();
            ShortcutFolderList.ItemsSource = null;
            ShortcutFolderList.ItemsSource = _cfg.ShortcutFolders.ToList();
            ScanSteamCheck.IsChecked = _cfg.ScanSteam;
            UseShortcutsSettingCheck.IsChecked = _cfg.UseShortcuts;
            ScanStartMenuCheck.IsChecked = _cfg.ScanStartMenu;
        }
        finally
        {
            _loading = false;
        }
    }

    private void LaunchSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.ScanSteam = ScanSteamCheck.IsChecked == true;
        _cfg.UseShortcuts = UseShortcutsSettingCheck.IsChecked == true;
        _cfg.ScanStartMenu = ScanStartMenuCheck.IsChecked == true;
        _cfg.Save();
    }

    private string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dlg.ShowDialog(_host.Window) == true ? dlg.FolderName : null;
    }

    private void GameFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("ゲームを入れているフォルダを選んでください") is not string f) return;
        if (!_cfg.GameFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) _cfg.GameFolders.Add(f);
        _cfg.Save();
        InitLaunchTab();
    }

    private void GameFolderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GameFolderList.SelectedItem is not string f) return;
        _cfg.GameFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        InitLaunchTab();
    }

    private void ShortcutFolderAdd_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder("ショートカットを置いているフォルダを選んでください") is not string f) return;
        if (!_cfg.ShortcutFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) _cfg.ShortcutFolders.Add(f);
        _cfg.Save();
        InitLaunchTab();
    }

    private void ShortcutFolderRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutFolderList.SelectedItem is not string f) return;
        _cfg.ShortcutFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        InitLaunchTab();
    }

    /// <summary>「実行ファイルを自動検出…」：自動検出の画面を開く（閉じたら、メイン画面がこのタブを読み直す）</summary>
    private void AutoDetect_Click(object sender, RoutedEventArgs e) => _host.AutoDetect();
}
