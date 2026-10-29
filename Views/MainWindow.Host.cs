using System.Windows;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>タブの画面（UserControl）に渡す、メイン画面の窓口（IMainHost）</summary>
public partial class MainWindow : IMainHost
{
    Window IMainHost.Window => this;
    Database IMainHost.Db => _db;
    EgsConfig IMainHost.Config => _cfg;
    EgsClient IMainHost.Egs => _egs;
    GameImageService IMainHost.Images => Images;

    void IMainHost.SetStatus(string text) => StatusText.Text = text;

    IReadOnlyList<Game> IMainHost.ShownGames() => GameGrid.Items.OfType<Game>().ToList();

    void IMainHost.ShowGameInList(long gameId) => ShowGameInList(gameId);

    void IMainHost.ReloadList() => Reload();

    void IMainHost.ExitApp() => ExitApp();

    bool IMainHost.IsExiting => _exiting;

    bool IMainHost.IsSummaryActive => IsVisible && ReferenceEquals(MainTabs.SelectedItem, SummaryTab);

    /// <summary>一覧タブに切り替えて、その作品を選ぶ（絞り込みで隠れているときは、「すべて」に戻す）</summary>
    private void ShowGameInList(long gameId)
    {
        Game? Find() => (GameGrid.ItemsSource as IEnumerable<Game>)?.FirstOrDefault(g => g.Id == gameId);

        MainTabs.SelectedIndex = 0; // 一覧タブ
        if (Find() == null)
        {
            // 絞り込みで隠れているときは、「すべて」に戻す
            if (FilterBox.SelectedIndex != 0) FilterBox.SelectedIndex = 0; else Reload();
        }
        if (Find() is { } game)
        {
            GameGrid.SelectedItem = game;
            GameGrid.ScrollIntoView(game);
        }
    }
}
