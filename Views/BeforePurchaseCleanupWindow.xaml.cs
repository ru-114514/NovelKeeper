using System.Windows;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>整理画面の一覧の1行</summary>
public sealed class CleanupItem(Game game)
{
    public Game Game { get; } = game;
    public bool Checked { get; set; }
    public string Title => Game.Title;
    public string Brand => Game.Brand ?? "";
    public string SellDay => Game.SellDay ?? "";
    public string WillText => string.IsNullOrEmpty(Game.Review.PurchaseWill) ? "ー" : Game.Review.PurchaseWillText;
    public string Note => BeforePurchaseCleanup.HasOwnContent(Game) ? "得点・感想あり" : "";
}

/// <summary>購入前の作品から、批評空間とツールの両方から外す作品を選ぶ</summary>
public partial class BeforePurchaseCleanupWindow : Window
{
    private readonly List<CleanupItem> _items;

    /// <summary>選ばれた作品（「削除…」で閉じたときだけ入っている）</summary>
    public List<Game> Selected { get; private set; } = [];

    public BeforePurchaseCleanupWindow(IEnumerable<Game> candidates)
    {
        InitializeComponent();
        _items = candidates.Select(g => new CleanupItem(g)).ToList();
        ItemGrid.ItemsSource = _items;
        UpdateCount();
    }

    private void UpdateCount()
    {
        var n = _items.Count(i => i.Checked);
        CountText.Text = $"購入前の作品 {_items.Count} 件のうち、{n} 件を選んでいます";
        RunButton.IsEnabled = n > 0;
    }

    private void SetAll(Func<CleanupItem, bool> pick)
    {
        foreach (var i in _items) i.Checked = pick(i);
        ItemGrid.Items.Refresh();
        UpdateCount();
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateCount();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);
    private void SelectNoWill_Click(object sender, RoutedEventArgs e) => SetAll(i => string.IsNullOrEmpty(i.Game.Review.PurchaseWill));

    /// <summary>得点・感想も空にして、批評空間の記録を消すか（初期はオフ）</summary>
    public bool EraseContent { get; private set; }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        EraseContent = EraseCheck.IsChecked == true;
        Selected = _items.Where(i => i.Checked).Select(i => i.Game).ToList();
        if (Selected.Count == 0) return;
        DialogResult = true;
    }
}
