using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// メイン画面の入力まわりの共通の動き：
///  ・手で打つ欄（1行の入力欄）で Enter を押すと、その欄の内容を確定する（詳細欄は「保存」、ログイン欄は「保存してログイン」）
///  ・画面の文字は、右クリック →「コピー」でコピーできる（一覧の中・ボタン・選択欄などを除く）
/// </summary>
public partial class MainWindow
{
    private static bool _copyMenuRegistered;

    private void InitInputHelpers()
    {
        PreviewKeyDown += MainWindow_PreviewKeyDown_Enter;

        // 画面の文字（TextBlock）の右クリックは、アプリ全体で1回だけ登録する
        if (_copyMenuRegistered) return;
        _copyMenuRegistered = true;
        EventManager.RegisterClassHandler(typeof(TextBlock), MouseRightButtonUpEvent, new MouseButtonEventHandler(TextBlock_RightClick));
    }

    private void MainWindow_PreviewKeyDown_Enter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused == null) return;

        // 詳細欄の1行の入力欄：Enter で「保存」
        if (focused is TextBox { AcceptsReturn: false } tb && DetailBoxes().Contains(tb))
        {
            if (!DetailPanel.IsEnabled || Selected == null) return;
            e.Handled = true;
            Save_Click(this, new RoutedEventArgs());
            return;
        }

        // ログイン設定タブ：Enter で「保存してログイン」
        if (LoginView.TryHandleEnter(focused))
        {
            e.Handled = true;
            return;
        }

        // そのほかの1行の入力欄（詳細設定の数値など）：Enter で次へ進めて確定する（欄を離れたときに反映される欄のため）
        if (focused is TextBox { AcceptsReturn: false } other && !IsInsideGrid(other))
        {
            e.Handled = true;
            other.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    private IEnumerable<TextBox> DetailBoxes() =>
        new[] { ScoreBox, TotalPlayTimeBox, FunTimeBox, BeforeScoreBox, PriceBox, HitokotoBox };

    private static bool IsInsideGrid(DependencyObject d) => HasAncestor<DataGrid>(d);

    private static void TextBlock_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock tb || string.IsNullOrWhiteSpace(tb.Text)) return;
        if (tb.ContextMenu != null) return; // もともと右クリックのメニューがあるものは、そのまま
        if (HasAncestor<DataGrid>(tb) || HasAncestor<ButtonBase>(tb) || HasAncestor<ComboBox>(tb) || HasAncestor<ComboBoxItem>(tb) ||
            HasAncestor<ListBoxItem>(tb) || HasAncestor<TabItem>(tb) || HasAncestor<MenuItem>(tb) || HasAncestor<TextBox>(tb)) return;

        var text = tb.Text;
        var item = new MenuItem { Header = "コピー" };
        item.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); }
            catch (Exception ex) { Log.Warn("文字をコピーできませんでした", ex); }
        };
        var menu = new ContextMenu { PlacementTarget = tb, Items = { item } };
        menu.Closed += (_, _) => tb.ContextMenu = null;
        tb.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static bool HasAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
             p != null;
             p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T) return true;
        return false;
    }
}
