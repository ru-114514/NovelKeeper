using System.Windows;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

public partial class SearchWindow : Window
{
    private readonly EgsClient _egs;
    private readonly Database _db;

    public SearchWindow(EgsClient egs, Database db)
    {
        InitializeComponent();
        _egs = egs;
        _db = db;
        Loaded += (_, _) => KeywordBox.Focus();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var keyword = KeywordBox.Text.Trim();
        if (keyword == "") return;

        SearchButton.IsEnabled = false;
        InfoText.Text = "検索中…";
        try
        {
            var results = await _egs.SearchAsync(keyword);
            ResultGrid.ItemsSource = results;
            InfoText.Text = results.Count == 0 ? "見つかりませんでした" : $"{results.Count} 件";
        }
        catch (Exception ex)
        {
            InfoText.Text = "検索に失敗しました";
            MessageBox.Show(ex.Message, "検索エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var added = 0;
        var skipped = 0;
        foreach (var item in ResultGrid.SelectedItems.Cast<EgsGame>())
        {
            if (_db.ExistsEgsId(item.Id)) { skipped++; continue; }
            _db.Insert(new Game
            {
                EgsId = item.Id,
                Title = item.Title,
                Brand = item.Brand,
                SellDay = item.SellDay,
                Median = item.Median,
                EgsAverage = item.Average,
                EgsCount = item.Count,
                EgsRegistered = DateTime.Today, // 批評空間の登録日と同じく「手元に置いた日」。同期で批評空間の登録日があれば、そちらに合わせる
            });
            added++;
        }
        InfoText.Text = $"{added} 件追加しました" + (skipped > 0 ? $"（登録済み {skipped} 件はスキップ）" : "") +
                        "。実行ファイルはメイン画面の右側で設定してください。";
    }
}
