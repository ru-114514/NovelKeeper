using System.IO;
using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;
using static NovelKeeper.Views.ViewHelpers;

namespace NovelKeeper.Views;

/// <summary>詳細設定タブの「自動バックアップ」：設定・今すぐ・一覧から戻す・データの検査</summary>
public partial class MainWindow
{
    private void InitAutoBackup()
    {
        _loadingSettings = true;
        try
        {
            if (AutoBackupKeepBox.Items.Count == 0)
                for (var n = AutoBackup.MinKeep; n <= AutoBackup.MaxKeep; n++)
                    AutoBackupKeepBox.Items.Add(new ComboBoxItem { Content = $"{n} 世代", Tag = n });
            AutoBackup.UseFolder(_cfg.AutoBackupDir);
            var s = AutoBackup.SettingsOf(_cfg);
            AutoBackupCheck.IsChecked = s.Enabled;
            AutoBackupIntervalBox.SelectedIndex = s.IntervalDays >= 7 ? 1 : 0;
            AutoBackupKeepBox.SelectedIndex = s.Keep - AutoBackup.MinKeep;
            AutoBackupIntervalBox.IsEnabled = AutoBackupKeepBox.IsEnabled = s.Enabled;
        }
        finally
        {
            _loadingSettings = false;
        }
        RefreshAutoBackupInfo();
    }

    private void AutoBackupSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || _cfg == null || AutoBackupCheck == null) return;
        _cfg.AutoBackupEnabled = AutoBackupCheck.IsChecked == true;
        _cfg.AutoBackupIntervalDays = AutoBackupIntervalBox.SelectedItem is ComboBoxItem { Tag: string d } && d == "7" ? 7 : 1;
        _cfg.AutoBackupKeep = AutoBackupKeepBox.SelectedItem is ComboBoxItem { Tag: int k } ? k : AutoBackup.DefaultKeep;
        _cfg.Save();
        AutoBackupIntervalBox.IsEnabled = AutoBackupKeepBox.IsEnabled = _cfg.AutoBackupEnabled;
        // 世代数を減らしたときは、すぐに古いものを整理する
        var keep = AutoBackup.SettingsOf(_cfg).Keep;
        Task.Run(() => AutoBackup.Prune(AutoBackup.BackupDir, keep, AutoBackup.MaxBefore, AutoBackup.MaxTotalBytes))
            .ContinueWith(_ => Dispatcher.InvokeAsync(RefreshAutoBackupInfo));
    }

    private void RefreshAutoBackupInfo()
    {
        var dir = AutoBackup.BackupDir;
        AutoBackupDirText.Text = AutoBackup.CustomDir ?? $"{AutoBackup.DefaultDir}（初期の場所）";
        AutoBackupDirText.ToolTip = AutoBackupDirText.Text;
        var gens = AutoBackup.List(dir);
        var total = AutoBackup.TotalSize(dir);
        AutoBackupInfo.Text = gens.Count == 0
            ? "まだバックアップはありません。"
            : $"最後のバックアップ：{gens[0].Date:yyyy/MM/dd}（{gens.Count} 世代・このアプリのファイル合計 {Size(total)}／上限 {Size(AutoBackup.MaxTotalBytes)}）";
        // 指定した保存先が使えないとき（外付けドライブが外れている など）は、作れないことを知らせる
        if (AutoBackup.CustomDir != null && !Directory.Exists(AutoBackup.CustomDir))
            AutoBackupInfo.Text += "\n指定した保存先が見つかりません。ドライブがつながっているかを確認してください（このままだと、バックアップは作られません）。";
    }

    /// <summary>保存先を別のフォルダにする。いまあるバックアップは移さない（元のフォルダに残る）</summary>
    private void AutoBackupDirChange_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "自動バックアップの保存先フォルダを選んでください" };
        if (AutoBackup.CustomDir != null && Directory.Exists(AutoBackup.CustomDir)) dlg.InitialDirectory = AutoBackup.CustomDir;
        if (dlg.ShowDialog(this) != true) return;

        var problem = AutoBackup.CheckFolder(dlg.FolderName);
        if (problem != null)
        {
            MessageBox.Show(this, problem, "バックアップの保存先", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetAutoBackupFolder(dlg.FolderName);
    }

    private void AutoBackupDirReset_Click(object sender, RoutedEventArgs e)
    {
        if (AutoBackup.CustomDir == null) return;
        SetAutoBackupFolder("");
    }

    private void SetAutoBackupFolder(string path)
    {
        _cfg.AutoBackupDir = path;
        _cfg.Save();
        AutoBackup.UseFolder(path);
        RefreshAutoBackupInfo();
        AutoBackupResultText.Text = "保存先を変えました。これまでのバックアップは、元のフォルダに残っています（移していません）。次のバックアップから新しい保存先に作ります";
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private async void AutoBackupNow_Click(object sender, RoutedEventArgs e)
    {
        var settings = AutoBackup.SettingsOf(_cfg);
        var result = await Task.Run(() => AutoBackup.Run(AppPaths.Database, AppPaths.Config, AutoBackup.BackupDir, settings, force: true));
        AutoBackupResultText.Text = result switch
        {
            AutoBackupResult.Created => $"バックアップを作りました（{DateTime.Now:HH:mm}）",
            AutoBackupResult.NoFreeSpace => "ドライブの空き容量が少ないため、作りませんでした（500MB 以上の空きが必要です）",
            AutoBackupResult.NoDatabase => "バックアップする作品データがありません",
            _ => "バックアップを作れませんでした。詳しい内容は log.txt を確認してください",
        };
        RefreshAutoBackupInfo();
    }

    private void AutoBackupOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AutoBackup.BackupDir);
        OpenInExplorer(AutoBackup.BackupDir);
    }

    private void AutoBackupCheck_Click(object sender, RoutedEventArgs e)
    {
        var check = AutoBackup.CheckIntegrity(AppPaths.Database);
        if (check.Ok)
        {
            AutoBackupResultText.Text = $"作品データに異常は見つかりませんでした（作品 {AutoBackup.CountGames(AppPaths.Database)} 件）";
            return;
        }
        AutoBackupResultText.Text = "作品データに異常が見つかりました：" + check.Message;
        if (MessageBox.Show(this, "作品データに異常が見つかりました。\n\n" + check.Message + "\n\n「一覧から戻す」を開きますか？",
                "データの検査", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            AutoBackupRestore_Click(sender, e);
    }

    /// <summary>世代の一覧から選んで戻す（戻す前に、いまの内容を控えとして残し、アプリが再起動する）</summary>
    private void AutoBackupRestore_Click(object sender, RoutedEventArgs e)
    {
        var gens = AutoBackup.List(AutoBackup.BackupDir);
        if (gens.Count == 0)
        {
            MessageBox.Show(this, "戻せる自動バックアップがありません。", "バックアップから戻す");
            return;
        }

        var list = new ListBox { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var g in gens)
        {
            var count = AutoBackup.CountGames(g.DbPath);
            var text = count == null
                ? $"{g.Date:yyyy/MM/dd}　（壊れています・戻せません）"
                : $"{g.Date:yyyy/MM/dd}　作品 {count} 件　{Size(g.Size)}{(g.ConfigPath != null ? "　設定あり" : "")}";
            list.Items.Add(new ListBoxItem { Content = text, Tag = g, IsEnabled = count != null });
        }
        list.SelectedIndex = list.Items.OfType<ListBoxItem>().ToList().FindIndex(i => i.IsEnabled);

        var withConfig = new CheckBox { Content = "設定も戻す（設定のないバックアップでは、作品の一覧とプレイ時間だけ戻します）", Margin = new Thickness(0, 0, 0, 8) };
        var ok = new Button { Content = "戻す", Padding = new Thickness(18, 3, 18, 3) };
        var close = new Button { Content = "閉じる", Padding = new Thickness(14, 3, 14, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(close);
        var panel = new DockPanel { Margin = new Thickness(14) };
        var head = new TextBlock { Text = "戻すバックアップを選んでください（新しい順）", FontWeight = FontWeights.Bold };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(withConfig, Dock.Bottom);
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(withConfig);
        panel.Children.Add(list);
        var win = new Window
        {
            Title = "バックアップから戻す", Width = 560, Height = 420, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false,
        };
        close.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is not ListBoxItem { Tag: BackupGeneration g }) return;
            var cfg = withConfig.IsChecked == true && g.ConfigPath != null;
            var ask = MessageBox.Show(win,
                $"いまの作品 {_db.GetAll().Count} 件を、{g.Date:yyyy/MM/dd} のバックアップ（作品 {AutoBackup.CountGames(g.DbPath)} 件）に置き換えます{(cfg ? "（設定も戻します）" : "")}。\n\n" +
                "置き換える前に、いまの内容を backups フォルダに控えとして残します。\n反映のため、アプリを再起動します。よろしいですか？",
                "バックアップから戻す", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (ask != MessageBoxResult.OK) return;
            try
            {
                AutoBackup.StageRestore(g, cfg, DataBackup.PendingDir);
                win.Close();
                RestartApp();
            }
            catch (Exception ex)
            {
                Log.Warn("自動バックアップからの復元の準備に失敗", ex);
                try { if (Directory.Exists(DataBackup.PendingDir)) Directory.Delete(DataBackup.PendingDir, true); } catch { }
                MessageBox.Show(win, "バックアップから戻せませんでした。\n\n" + ex.Message, "バックアップから戻す", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        win.ShowDialog();
    }
}
