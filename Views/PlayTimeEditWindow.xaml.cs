using System.Windows;
using System.Windows.Input;
using ErogeLauncher.Data;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>補正画面の一覧の1行</summary>
public sealed class SessionListItem(SessionEntry entry)
{
    public SessionEntry Entry { get; } = entry;
    public string StartText => Entry.Start.ToString("yyyy/MM/dd HH:mm");
    public string EndText => Entry.End.ToString("yyyy/MM/dd HH:mm");
    public string DurationText => SummaryCalculator.FormatDuration(Entry.Seconds);
    public string KindText => Entry.Manual ? "手動" : "自動";
}

/// <summary>1作品のプレイ時間の記録を、追加・編集・削除する</summary>
public partial class PlayTimeEditWindow : Window
{
    private readonly Database _db;
    private readonly Game _game;

    /// <summary>記録を変えたか（閉じたあと、呼び出し側が一覧を読み直すために使う）</summary>
    public bool Changed { get; private set; }

    public PlayTimeEditWindow(Database db, Game game)
    {
        InitializeComponent();
        _db = db;
        _game = game;
        TitleText.Text = game.Title;
        LoadList();
    }

    private void LoadList(long? selectId = null)
    {
        var entries = _db.GetGameSessions(_game.Id);
        var items = entries.Select(e => new SessionListItem(e)).ToList();
        SessionGrid.ItemsSource = items;
        SessionGrid.SelectedItem = items.FirstOrDefault(i => i.Entry.Id == selectId);

        var total = entries.Sum(e => e.Seconds);
        TotalText.Text = entries.Count == 0
            ? "プレイ時間の記録はありません。"
            : $"合計 {SummaryCalculator.FormatDuration(total)}（{entries.Count}件）";
    }

    private SessionEntry? SelectedEntry => (SessionGrid.SelectedItem as SessionListItem)?.Entry;

    /// <summary>ほかの記録（ほかの作品も含む）と時間が重なるときは、そのまま登録するか聞く。登録してよければ true</summary>
    private bool ConfirmOverlap(DateTime start, long seconds, SessionEntry? ignore)
    {
        var hits = ManualSessionInput.Overlaps(_db.GetSessions(start, start.AddSeconds(seconds)), _game.Id, start, seconds, ignore);
        if (hits.Count == 0) return true;

        var lines = string.Join("\n", hits.Take(3).Select(r => $"・{r.Title}　{r.Start:yyyy/MM/dd HH:mm}～{r.End:HH:mm}"));
        var more = hits.Count > 3 ? $"\n　ほか {hits.Count - 3} 件" : "";
        return MessageBox.Show(this,
            $"入力した時間（{start:yyyy/MM/dd HH:mm}～{start.AddSeconds(seconds):HH:mm}）が、次の記録と重なっています。\n\n{lines}{more}\n\n" +
            "そのまま登録すると、プレイ時間が二重に数えられます。登録しますか？",
            "プレイ時間の補正", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SessionEditWindow(_game.Title, null) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!ConfirmOverlap(dlg.ResultStart, dlg.ResultSeconds, null)) return;
        var id = _db.AddManualSession(_game.Id, dlg.ResultStart, dlg.ResultSeconds);
        Log.Info($"プレイ時間を手動で追加 game={_game.Id} start={dlg.ResultStart:yyyy-MM-dd HH:mm} seconds={dlg.ResultSeconds}");
        Changed = true;
        LoadList(id);
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private void SessionGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControlRowOf(d) != null) EditSelected();
    }

    /// <summary>ダブルクリックが見出しや余白ではなく、行の上だったか</summary>
    private static System.Windows.Controls.DataGridRow? ItemsControlRowOf(DependencyObject d)
    {
        while (d != null && d is not System.Windows.Controls.DataGridRow)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as System.Windows.Controls.DataGridRow;
    }

    private void EditSelected()
    {
        if (SelectedEntry is not SessionEntry entry)
        {
            MessageBox.Show(this, "編集する記録を選んでください。", "プレイ時間の補正");
            return;
        }
        if (!entry.Manual &&
            MessageBox.Show(this, "これは、ゲームの起動中に自動で記録された分です。\n書き換えると「手動」の記録になります。続けますか？",
                "プレイ時間の補正", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var dlg = new SessionEditWindow(_game.Title, entry) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (!ConfirmOverlap(dlg.ResultStart, dlg.ResultSeconds, entry)) return;
        _db.UpdateManualSession(entry.Id, dlg.ResultStart, dlg.ResultSeconds);
        Log.Info($"プレイ時間を手動で編集 game={_game.Id} session={entry.Id} {entry.Seconds}秒 → {dlg.ResultSeconds}秒");
        Changed = true;
        LoadList(entry.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not SessionEntry entry)
        {
            MessageBox.Show(this, "削除する記録を選んでください。", "プレイ時間の補正");
            return;
        }
        var text = $"次の記録を削除します。元に戻せません。\n\n{entry.Start:yyyy/MM/dd HH:mm}　{SummaryCalculator.FormatDuration(entry.Seconds)}（{(entry.Manual ? "手動" : "自動")}）";
        if (MessageBox.Show(this, text, "プレイ時間の補正", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

        _db.DeleteSession(entry.Id);
        Log.Info($"プレイ時間の記録を削除 game={_game.Id} session={entry.Id} {entry.Seconds}秒");
        Changed = true;
        LoadList();
    }
}
