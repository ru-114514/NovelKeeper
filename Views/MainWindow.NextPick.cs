using System.Windows;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>一覧の「🎲 次の1本…」：批評空間の中央値・POV・タグ・ジャンルで絞り込んで、次に遊ぶ作品を選ぶ</summary>
public partial class MainWindow
{
    private void NextPick_Click(object sender, RoutedEventArgs e)
    {
        new NextPickWindow(this, _egs, _cfg, () => _db.GetAll(), AddToPriorityPanel).ShowDialog();
    }

    private void StaleReview_Click(object sender, RoutedEventArgs e)
    {
        var last = _db.GetSessions(new DateTime(2000, 1, 1), DateTime.Today.AddDays(1))
            .GroupBy(s => s.GameId).ToDictionary(g => g.Key, g => g.Max(s => s.End));
        var win = new StaleReviewWindow(this, _cfg, _db.GetAll(), last, AddToPriorityPanel, DropGame);
        win.ShowDialog();
        if (!win.Changed) return;
        Reload();
        PostView.RefreshPost();
    }

    /// <summary>状態を「中断」にする（批評空間と連携している作品は、次の同期で反映する）</summary>
    private void DropGame(Game game)
    {
        var g = _db.Get(game.Id);
        if (g == null || g.Status == GameStatus.Dropped) return;
        var before = SavedState.Of(g);
        g.Status = GameStatus.Dropped;
        if (g.EgsId != null) g.PendingReview.UnionWith(PushEngine.ChangedKeys(before, g));
        _db.Update(g);
    }
}
