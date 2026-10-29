using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NovelKeeper.Models;
using NovelKeeper.Services;
using Microsoft.Win32;
using static NovelKeeper.Views.ViewHelpers;

namespace NovelKeeper.Views;

/// <summary>メイン画面のうち、作品の追加・起動、起動するもの（複数登録・ドラッグ＆ドロップ）を担当する部分</summary>
public partial class MainWindow
{
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

    /// <summary>一覧の「起動設定」欄の ▼：起動するもの（収録作など）を選び替える</summary>
    private void RowTargetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Game g } button) return;
        GameGrid.SelectedItem = g;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var mi in TargetMenuItems(g)) menu.Items.Add(mi);
        if (menu.Items.Count == 0) return;
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "追加・編集は詳細欄の起動ファイル設定項目で可能。", IsEnabled = false });
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

    private void Launch_Click(object sender, RoutedEventArgs e) => Launch();

    private void Launch()
    {
        if (Selected is { } g) Launch(g);
    }

    private void Launch(Game g, bool once = false)
    {
        try
        {
            if (!_tracker.Launch(g, once))
            {
                StatusText.Text = $"管理者権限の確認で「いいえ」が選ばれたため、起動しませんでした：{g.Title}";
                return;
            }
            var admin = PlayTimeTracker.NeedsAdmin(_cfg.AlwaysRunAsAdmin, g.RunAsAdmin, once, ShortcutResolver.IsUrl(g.ExePath ?? ""));
            StatusText.Text = $"{(admin ? "管理者権限で" : "")}起動しました：{g.Title}（ゲームが動き出すと、自動でプレイ時間の計測を始めます）";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "起動できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右クリックの「管理者権限で起動」。この作品で初めて選んだときは、説明と「今後は確認しない」の選択を出す</summary>
    private void LaunchAsAdmin(Game g)
    {
        if (!g.RunAsAdmin && !_cfg.AlwaysRunAsAdmin)
        {
            var always = new CheckBox
            {
                Content = "今後、この作品は確認なしで管理者権限で起動する（通常の起動も管理者権限になります）",
                Margin = new Thickness(0, 12, 0, 0),
            };
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "「管理者権限で実行」が必要なゲームを、管理者権限で起動します。\n" +
                       "起動のたびに、Windows の確認画面（ユーザーアカウント制御）が表示されます。\n" +
                       "この設定は、この作品だけに適用されます（外すときは、右クリックのメニューから）。",
            };
            var ok = new Button { Content = "管理者権限で起動", IsDefault = true, MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "キャンセル", IsCancel = true, MinWidth = 80 };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(text);
            panel.Children.Add(always);
            panel.Children.Add(buttons);
            var dlg = new Window
            {
                Title = "管理者権限で起動：" + g.Title,
                Content = panel,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.Height,
                Width = 480,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            ok.Click += (_, _) => dlg.DialogResult = true;
            if (dlg.ShowDialog() != true) return;
            if (always.IsChecked == true)
            {
                g.RunAsAdmin = true;
                _db.Update(g);
                GameGrid.Items.Refresh();
                UpdateAdminCount();
            }
        }
        Launch(g, once: true);
    }

}
