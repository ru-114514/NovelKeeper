using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 優先作品（投稿設定の「今後優先したい作品」パネルに入っている作品。一覧の「特例」列に ★優先 と出る）の付け外し。
/// 「次の1本」「放置作品の見直し」のほか、一覧の右クリックと、詳細欄のボタンからも使える
/// </summary>
public partial class MainWindow
{
    /// <summary>選んだ作品を、投稿設定の最初の「今後優先したい作品」パネルに入れる（なければパネルを足す）。入れた本数を返す</summary>
    private int AddToPriorityPanel(List<Game> games) => ChangePriority(games, add: true);

    /// <summary>
    /// 優先に入れる・外す。投稿設定のパネル・一覧の「特例」列・投稿設定タブを合わせる。入れた・外した本数を返す
    /// （すでに優先の作品と、上限を超える分は入れない）
    /// </summary>
    private int ChangePriority(IReadOnlyCollection<Game> games, bool add)
    {
        var layout = _cfg.PostLayout;
        var ids = games.Select(g => g.Id).ToList();
        var wanted = add ? ids.Count(id => !layout.PriorityIds().Contains(id)) : 0;
        var changed = add ? layout.AddPriority(ids) : layout.RemovePriority(ids);
        if (changed > 0)
        {
            _cfg.Save();
            Game.PriorityIds = layout.PriorityIds();
            Reload();
            PostView.RefreshPost();
        }

        var title = games.Count == 1 ? $"「{games.First().Title}」" : $"{changed}本";
        if (add && changed < wanted)
            StatusText.Text = $"優先したい作品は {PostLayout.MaxPriorityGames} 本までです。投稿設定の「今後優先したい作品」パネルか、一覧の右クリックで外してから追加してください";
        else if (changed > 0)
            StatusText.Text = add ? $"{title}を優先にしました（一覧の「特例」列と、投稿設定の「今後優先したい作品」パネル）" : $"{title}の優先をやめました";
        return changed;
    }

    /// <summary>一覧の右クリックのメニューに、その作品の優先の付け外しを足す（GameGrid_ContextMenuOpening から呼ぶ）</summary>
    private void AddPriorityMenuItem(ContextMenu menu, Game g)
    {
        var item = new MenuItem { Header = g.IsPriority ? $"優先をやめる：{g.Title}" : $"★優先にする：{g.Title}" };
        item.Click += (_, _) => ChangePriority([g], add: !g.IsPriority);
        menu.Items.Add(item);
        menu.Items.Add(new Separator());
    }

    /// <summary>詳細欄のボタン：選んでいる作品の優先を付け外しする</summary>
    private void DetailPriority_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } g) ChangePriority([g], add: !g.IsPriority);
    }

    /// <summary>詳細欄のボタンの文字を、選んでいる作品に合わせる</summary>
    private void ShowDetailPriority(Game? g)
    {
        DetailPriorityButton.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
        DetailPriorityButton.Content = g?.IsPriority == true ? "★優先をやめる" : "★優先にする";
        DetailPriorityButton.ToolTip = g?.IsPriority == true
            ? "投稿設定の「今後優先したい作品」パネルから外します"
            : $"投稿設定の「今後優先したい作品」パネルに入れます（{PostLayout.MaxPriorityGames} 本まで）";
    }
}
