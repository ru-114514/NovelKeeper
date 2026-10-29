using System.Windows;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>タブの画面（UserControl）に渡す、メイン画面の窓口（IMainHost）</summary>
public partial class MainWindow : ISettingsHost
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

    bool IMainHost.EgsBusy => _egsBusy;

    bool IMainHost.BeginEgsWork() => BeginEgsWork(lockDetail: false, quiet: true);

    void IMainHost.EndEgsWork() => EndEgsWork();

    void IMainHost.ImportAll() => ImportAll_Click(this, new RoutedEventArgs());

    void IMainHost.Sync() => Sync_Click(this, new RoutedEventArgs());

    void IMainHost.AutoDetect() => AutoDetect();

    void IMainHost.ExitApp() => ExitApp();

    bool IMainHost.IsExiting => _exiting;

    bool IMainHost.IsSummaryActive => IsVisible && ReferenceEquals(MainTabs.SelectedItem, SummaryTab);

    // 詳細設定タブ（SettingsTabView）から
    void ISettingsHost.RefreshListRows() => GameGrid.Items.Refresh();
    void ISettingsHost.UpdateAdminBadge() => UpdateAdminBadge();
    void ISettingsHost.ThemeChanged() => ApplyCategoryColors();
    void ISettingsHost.ApplyDetailLayout() => ApplyDetailLayout();
    void ISettingsHost.CleanupBeforePurchase() => CleanupBeforePurchase_Click(this, new RoutedEventArgs());
    void ISettingsHost.ResetZoom() => SetListZoom(1.0);
    void ISettingsHost.RefreshImage() => ShowImage(Selected);
    void ISettingsHost.ClearImages() => ClearImages();
    void ISettingsHost.OpenScoreCriteria() => ScoreCriteria_Click(this, new RoutedEventArgs());
    bool ISettingsHost.SetScoreItemsEnabled(bool on) => SetScoreItemsEnabled(on);
    IReadOnlyList<ColumnSetting> ISettingsHost.ColumnSettings() => ColumnSettings();
    bool ISettingsHost.IsLockedColumn(string key) => LockedColumns.Contains(key);
    void ISettingsHost.SaveColumns(List<ColumnSetting> columns) => SaveColumns(columns);
    void ISettingsHost.ResetColumns() => ResetColumns();

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
