using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 「データ出力」タブ（作品の一覧を CSV に書き出す・バックアップ）。
/// 左のサブナビゲーションで、書き出す範囲（一覧に表示中／全作品）か、バックアップを選ぶ。
/// メイン画面とは IMainHost を通してやりとりする（自動バックアップの部分は ExportTabView.AutoBackup.cs）
/// </summary>
public partial class ExportTabView : UserControl
{
    private IMainHost _host = null!;
    private Database _db = null!;
    private EgsConfig _cfg = null!;
    private string? _exportDir; // 前回の書き出し先（次回の保存画面で開く）

    public ExportTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（ほかの Init より先に、1回だけ呼ぶ）</summary>
    internal void Init(IMainHost host)
    {
        _host = host;
        _db = host.Db;
        _cfg = host.Config;
    }

    private Window OwnerWindow => _host.Window;

    private bool ExportAll => (ExportNav.SelectedItem as ListBoxItem)?.Tag as string == "all";

    /// <summary>書き出す作品。「全作品」でも、購入前の作品は「一覧に表示する」の設定に従う</summary>
    private List<Game> ExportGames() => ExportAll
        ? CsvExporter.SelectAll(_db.GetAll(), _cfg.ShowBeforePurchase)
        : _host.ShownGames().ToList();

    /// <summary>起動時：範囲を「一覧に表示中」にしておく（タブを開くたびの数え直しは、メイン画面から RefreshExportPanel を呼ぶ）</summary>
    internal void InitExportTab()
    {
        ExportNav.SelectionChanged += (_, _) => RefreshExportPanel();
        ExportNav.SelectedIndex = 0; // 「一覧に表示中」
    }

    /// <summary>選んだ範囲に合わせて、説明・件数・書き出す列を表示する</summary>
    internal void RefreshExportPanel()
    {
        var backup = (ExportNav.SelectedItem as ListBoxItem)?.Tag as string == "backup";
        BackupPanel.Visibility = backup ? Visibility.Visible : Visibility.Collapsed;
        ExportPanel.Visibility = backup ? Visibility.Collapsed : Visibility.Visible;
        if (backup) return;

        var all = ExportAll;
        ExportTitle.Text = all ? "全作品を書き出す" : "一覧に表示中の作品を書き出す";
        ExportDescription.Text = all
            ? "登録している作品をすべて書き出します。「一覧」タブの絞り込み（表示:）には関係なく、状態を問わず全件が対象です。ただし購入前の作品は、「詳細設定」の「購入前の作品を一覧に表示する」がオフのときは含めません（一覧と同じ扱いです。ただし、発売済みで購入確認が必要な作品は含めます）。"
            : "いま「一覧」タブに表示している作品だけを、一覧の並び順のままで書き出します。「一覧」タブの「表示」（絞り込み）を変えると、対象も変わります。";
        ExportCount.Text = $"対象：{ExportGames().Count}作品";
        ExportColumns.Text = string.Join("、", CsvExporter.ColumnNames(ExportReviewCheck.IsChecked == true, CsvScoreItems()));
    }

    /// <summary>任意評価基準を使っているときだけ、CSV に項目別の点数の列を足す</summary>
    private IReadOnlyList<IReadOnlyList<ScoreItem>>? CsvScoreItems() => ScoreItemSettings.Enabled ? ScoreItemSettings.Templates : null;

    private void ExportReviewCheck_Click(object sender, RoutedEventArgs e) => RefreshExportPanel();

    private void BackupExport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "NovelKeeper バックアップ (*.zip)|*.zip",
            FileName = $"NovelKeeper_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            DefaultExt = ".zip",
            OverwritePrompt = false, // 上書きはせず、同じ名前があれば (1) (2) … を付けて別ファイルにする
        };
        if (_exportDir != null && Directory.Exists(_exportDir))
        {
            dlg.InitialDirectory = _exportDir;
            dlg.FileName = Path.GetFileName(CsvExporter.UniquePath(Path.Combine(_exportDir, dlg.FileName)));
        }
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        try
        {
            var path = CsvExporter.UniquePath(dlg.FileName);
            _exportDir = Path.GetDirectoryName(path);
            DataBackup.Export(path, AppPaths.Database, AppPaths.Config);
            _host.SetStatus($"バックアップを書き出しました：{path}");
        }
        catch (Exception ex)
        {
            Log.Warn("バックアップの書き出しに失敗", ex);
            MessageBox.Show(OwnerWindow, "バックアップを書き出せませんでした。保存先に書き込めるかを確認してください。\n\n" + ex.Message,
                "バックアップ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackupImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NovelKeeper バックアップ (*.zip)|*.zip" };
        if (_exportDir != null && Directory.Exists(_exportDir)) dlg.InitialDirectory = _exportDir;
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        var info = DataBackup.Inspect(dlg.FileName);
        if (!info.Ok)
        {
            MessageBox.Show(OwnerWindow, info.Error, "バックアップ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var withConfig = BackupWithConfig.IsChecked == true;
        if (withConfig && !info.HasConfig)
        {
            MessageBox.Show(OwnerWindow, "このバックアップには、設定が入っていません。作品の一覧とプレイ時間だけを戻します。", "バックアップ");
            withConfig = false;
        }
        var current = _db.GetAll().Count;
        var ask = MessageBox.Show(OwnerWindow,
            $"いまの作品 {current} 件を、バックアップの作品 {info.Games} 件に置き換えます{(withConfig ? "（設定も戻します）" : "")}。\n\n" +
            "置き換える前に、いまの内容を backups フォルダに控えとして残します。\n反映のため、アプリを再起動します。よろしいですか？",
            "バックアップから戻す", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (ask != MessageBoxResult.OK) return;

        try
        {
            DataBackup.Stage(dlg.FileName, DataBackup.PendingDir, withConfig);
            RestartApp();
        }
        catch (Exception ex)
        {
            Log.Warn("バックアップの取り込みの準備に失敗", ex);
            try { if (Directory.Exists(DataBackup.PendingDir)) Directory.Delete(DataBackup.PendingDir, true); } catch { }
            MessageBox.Show(OwnerWindow, "バックアップを取り込めませんでした。\n\n" + ex.Message, "バックアップ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>このアプリを終了して、少し待ってから起動し直す（取り込みの反映のため。二重起動を防ぐ）</summary>
    private void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) throw new InvalidOperationException("アプリの場所を取得できませんでした。");
        // cmd.exe は Windows のものを指定する（名前だけだと、exe と同じフォルダに置かれた同じ名前の偽物が先に使われるため）
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Process.Start(new ProcessStartInfo(cmd, $"/c timeout /t 3 /nobreak >nul & start \"\" \"{exe}\"")
        { CreateNoWindow = true, UseShellExecute = false });
        _host.ExitApp();
    }

    private void ExportRun_Click(object sender, RoutedEventArgs e)
    {
        var all = ExportAll;
        var includeReview = ExportReviewCheck.IsChecked == true;
        var games = ExportGames();
        if (games.Count == 0)
        {
            MessageBox.Show(OwnerWindow, "書き出す作品がありません。", "データ出力");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV ファイル (*.csv)|*.csv",
            FileName = $"NovelKeeper_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
        };
        dlg.OverwritePrompt = false; // 上書きはせず、同じ名前があれば (1) (2) … を付けて別ファイルにする
        if (_exportDir != null && Directory.Exists(_exportDir))
        {
            dlg.InitialDirectory = _exportDir; // 前回の保存先を開く
            dlg.FileName = Path.GetFileName(CsvExporter.UniquePath(Path.Combine(_exportDir, dlg.FileName)));
        }
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        try
        {
            var path = CsvExporter.UniquePath(dlg.FileName);
            _exportDir = Path.GetDirectoryName(path);
            CsvExporter.Write(path, games, includeReview, CsvScoreItems());
            _host.SetStatus($"CSV に書き出しました（{games.Count}作品）：{path}");
        }
        catch (Exception ex)
        {
            Log.Warn("CSV の書き出しに失敗", ex);
            MessageBox.Show(OwnerWindow, "CSV を書き出せませんでした。\n同じ名前のファイルを Excel などで開いていないか、保存先に書き込めるかを確認してください。\n\n" + ex.Message,
                "データ出力", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
