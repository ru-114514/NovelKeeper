using System.Windows;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

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
        RefreshPost();
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

    /// <summary>選んだ作品を、投稿設定の最初の「今後優先したい作品」パネルに入れる（なければパネルを足す）</summary>
    private void AddToPriorityPanel(List<Game> games)
    {
        var layout = _cfg.PostLayout;
        var panel = layout.Panels.FirstOrDefault(p => p.Kind == PostPanelKind.Priority) ?? layout.Add(PostPanelKind.Priority);
        foreach (var g in games)
        {
            if (panel.GameIds.Count >= PostLayout.MaxPriorityGames) break;
            if (!panel.GameIds.Contains(g.Id)) panel.GameIds.Add(g.Id);
        }
        _cfg.Save();
        Game.PriorityIds = layout.Panels.Where(p => p.Kind == PostPanelKind.Priority).SelectMany(p => p.GameIds).ToHashSet();
        Reload();
        RefreshPost();
    }
}
