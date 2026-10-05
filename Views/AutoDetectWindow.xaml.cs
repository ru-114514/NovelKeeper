using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>検出結果の1行</summary>
public class DetectRow : INotifyPropertyChanged
{
    public required DetectedGame Detected { get; init; }

    /// <summary>使う exe（通常は起動する exe、Steam は監視する exe）</summary>
    public ExeCandidate? SelectedCandidate { get; set; }
    public double Score { get; set; }

    /// <summary>同じ作品に、ほかの検出結果も割り当てられている（自動設定から除外する）</summary>
    public bool IsDuplicate { get; set; }

    public string NoteText => IsDuplicate ? "重複のため除外" : Detected.CertainText;
    public string ScoreText => SelectedGame == null ? "" : $"{Score:P0}";

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnChanged(); }
    }

    private Game? _selectedGame;
    public Game? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (_selectedGame == value) return;
            var manual = _selectedGame != null || value != null;
            _selectedGame = value;
            OnChanged();
            OnChanged(nameof(ScoreText));
            if (manual && value != null) IsChecked = true; // 作品を選び直したら自動で☑
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class AutoDetectWindow : Window
{
    private const double AutoCheckScore = 0.6; // これ以上の一致度なら最初から☑

    private readonly Database _db;
    private readonly EgsConfig _cfg;
    private List<DetectRow> _rows = new();

    /// <summary>作品の選択肢（ComboBox 用）</summary>
    public List<Game> Games { get; private set; }

    public AutoDetectWindow(Database db, EgsConfig cfg)
    {
        InitializeComponent();
        _db = db;
        _cfg = cfg;
        Games = _db.GetAll();
        DataContext = this;

        FoldersBox.Text = string.Join(";", _cfg.GameFolders);
        ShortcutFoldersBox.Text = string.Join(";", _cfg.ShortcutFolders);
        UseShortcutsCheck.IsChecked = _cfg.UseShortcuts;
        SteamCheck.IsChecked = _cfg.ScanSteam;
        StartMenuCheck.IsChecked = _cfg.ScanStartMenu;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "ゲームをインストールしているフォルダを選んでください" };
        if (dlg.ShowDialog(this) != true) return;
        var list = SplitList(FoldersBox.Text);
        if (!list.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase)) list.Add(dlg.FolderName);
        FoldersBox.Text = string.Join(";", list);
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (SplitList(FoldersBox.Text).Count == 0 && SteamCheck.IsChecked != true && UseShortcutsCheck.IsChecked != true)
        {
            MessageBox.Show("「フォルダを追加…」で、ゲームをインストールしているフォルダを指定してください。", "自動検出");
            return;
        }

        // 入力した条件を設定ファイルに保存しておく（次回から自動で入る）
        _cfg.GameFolders = SplitList(FoldersBox.Text);
        _cfg.ShortcutFolders = SplitList(ShortcutFoldersBox.Text);
        _cfg.ScanSteam = SteamCheck.IsChecked == true;
        _cfg.ScanStartMenu = StartMenuCheck.IsChecked == true;
        _cfg.UseShortcuts = UseShortcutsCheck.IsChecked == true;
        _cfg.Save();

        ScanButton.IsEnabled = false;
        InfoText.Text = "検出中…（ゲームの数によっては少し時間がかかります）";
        try
        {
            var folders = _cfg.GameFolders;
            // 「ショートカットも使う」が外れていれば、ショートカットは一切見ない
            var shortcutFolders = _cfg.UseShortcuts ? _cfg.ShortcutFolders : new List<string>();
            var scanSteam = _cfg.ScanSteam;
            var scanStartMenu = _cfg.UseShortcuts && _cfg.ScanStartMenu;
            var games = Games;

            // 重い処理は別スレッドで行い、画面を固まらせない
            // （ショートカットの読み取りに COM を使うので STA スレッドで動かす）
            _rows = await RunStaAsync(() =>
            {
                var detected = GameScanner.ScanAll(folders, scanSteam, shortcutFolders, scanStartMenu);
                return detected.Select(d => MakeRow(d, games)).ToList();
            });

            // 同じ作品に複数の検出結果が割り当てられた場合は、その作品ごと自動設定から外す
            foreach (var grp in _rows.Where(r => r.SelectedGame != null)
                                     .GroupBy(r => r.SelectedGame!.Id)
                                     .Where(gr => gr.Count() > 1))
            {
                foreach (var r in grp)
                {
                    r.IsDuplicate = true;
                    r.IsChecked = false;
                }
            }

            ApplyFilter();
            var missing = folders.Concat(shortcutFolders).Where(f => !System.IO.Directory.Exists(f)).ToList();
            var certain = _rows.Count(r => r.IsChecked);
            var unsure = _rows.Count(r => (!r.Detected.IsExeCertain || r.IsDuplicate) && r.SelectedGame != null);
            InfoText.Text = $"{_rows.Count} 件見つかりました。exe を特定できて ☑ が付いた作品：{certain} 件、要確認：{unsure} 件。\n" +
                            "まず「☑ した作品に設定する」で特定できた分を設定してください。要確認の作品は、一覧の「起動設定」欄にショートカットをドロップして設定できます。" +
                            (missing.Count > 0 ? $"\n見つからないフォルダ：{string.Join(", ", missing)}" : "");
        }
        catch (Exception ex)
        {
            InfoText.Text = "検出に失敗しました：" + ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    /// <summary>見つかったゲームに、いちばん名前が近い作品を割り当てる</summary>
    private static DetectRow MakeRow(DetectedGame d, List<Game> games)
    {
        Game? best = null;
        double bestScore = 0;
        foreach (var g in games)
        {
            foreach (var name in d.Names)
            {
                var s = GameScanner.Similarity(name, g.Title);
                if (s > bestScore) { bestScore = s; best = g; }
            }
        }

        var row = new DetectRow { Detected = d, Score = bestScore, SelectedCandidate = d.Candidates.FirstOrDefault() };
        if (bestScore >= 0.4) row.SelectedGame = best;
        // 自動で ☑ を付けるのは「作品名が十分一致」「exe を1つに特定できた」「まだ未設定」のものだけ
        row.IsChecked = bestScore >= AutoCheckScore && best != null && string.IsNullOrEmpty(best.ExePath)
                        && d.IsExeCertain;
        return row;
    }

    private void ShowAllCheck_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private static List<string> SplitList(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>処理を STA スレッド（COM が使えるスレッド）で実行して、結果を待つ</summary>
    private static Task<T> RunStaAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>初期状態では「まだ実行ファイルが未設定の作品」に当たる行だけ表示する</summary>
    private void ApplyFilter()
    {
        var showAll = ShowAllCheck.IsChecked == true;
        ResultGrid.ItemsSource = _rows
            // 「ショートカット」だけで見つかったもので、どの作品とも一致しないもの（ツール類など）は隠す
            .Where(r => showAll || r.Detected.Source != "ショートカット" || r.SelectedGame != null)
            // 重複して除外した行は隠す
            .Where(r => showAll || !r.IsDuplicate)
            // 実行ファイルを設定済みの作品は隠す
            .Where(r => showAll || r.SelectedGame == null || string.IsNullOrEmpty(r.SelectedGame.ExePath))
            .OrderByDescending(r => r.IsChecked)
            .ThenByDescending(r => r.Score)
            .ToList();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var targets = _rows.Where(r => r.IsChecked && r.SelectedGame != null).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("☑ が付いていて、作品が選ばれている行がありません。");
            return;
        }

        // 同じ作品に複数の候補が ☑ されている場合は、その作品は設定しない（手動でショートカットを設定してもらう）
        var dupIds = targets.GroupBy(r => r.SelectedGame!.Id)
                            .Where(gr => gr.Count() > 1)
                            .Select(gr => gr.Key)
                            .ToHashSet();
        var skippedTitles = targets.Where(r => dupIds.Contains(r.SelectedGame!.Id))
                                   .Select(r => r.SelectedGame!.Title)
                                   .Distinct()
                                   .ToList();
        targets = targets.Where(r => !dupIds.Contains(r.SelectedGame!.Id)).ToList();

        foreach (var r in targets)
        {
            var g = r.SelectedGame!;
            if (r.Detected.IsSteam)
            {
                // Steam：起動は Steam 経由、選んだ exe は「監視するプロセス名」に使う
                g.ExePath = r.Detected.SteamUrl;
                if (r.SelectedCandidate != null)
                    g.MonitorProcess = System.IO.Path.GetFileName(r.SelectedCandidate.Path);
            }
            else
            {
                if (r.SelectedCandidate == null) continue;
                g.ExePath = r.SelectedCandidate.Path;
            }
            // プレイ中の判定に使うフォルダ（Steam はインストール先、それ以外は exe のフォルダで判定）
            g.InstallDir = r.Detected.IsSteam && System.IO.Directory.Exists(r.Detected.InstallDir)
                ? r.Detected.InstallDir
                : null;
            _db.Update(g);
        }

        var message = $"{targets.Count} 件の作品に実行ファイルを設定しました。";
        if (skippedTitles.Count > 0)
            message += $"\n\n次の {skippedTitles.Count} 件は候補が重複したため設定していません。\n" +
                       "一覧の「起動設定」欄にショートカットをドロップして設定してください。\n" +
                       string.Join("\n", skippedTitles.Take(15).Select(t => "・" + t)) +
                       (skippedTitles.Count > 15 ? "\n…" : "");
        MessageBox.Show(message, "自動検出");
        Games = _db.GetAll();
        DialogResult = true;
    }
}
