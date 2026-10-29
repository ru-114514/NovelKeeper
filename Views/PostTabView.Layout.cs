using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>投稿設定タブ：レイアウトの編集とドラッグ＆ドロップ</summary>
public partial class PostTabView
{
    // ================================================================
    // レイアウトの編集（＋追加・⚙設定・×削除・カラム数・初期配置）
    // ================================================================

    private void SaveLayoutAndRebuild()
    {
        _cfg.Save();
        RebuildPostReport();
    }

    private void PostColumns_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_postLoading || PostColumnsBox.SelectedIndex < 0) return;
        Layout.SetColumns(PostColumnsBox.SelectedIndex + PostLayout.MinColumns); // はみ出す・重なるパネルは、幅を縮めて置き直す
        SaveLayoutAndRebuild();
        if (_postEditing != null) OpenPostSettings(_postEditing);
    }

    private void PostAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PostAddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (groupName, kinds) in PostLayout.Groups)
        {
            var group = new MenuItem { Header = groupName };
            foreach (var kind in kinds)
            {
                var item = new MenuItem { Header = PostLayout.DefaultTitle(kind) };
                var k = kind;
                item.Click += (_, _) =>
                {
                    var probe = PostLayout.NewPanel(k); // 中身に合わせた縦幅（段数）を、実際に測って決める
                    probe.Span = PostLayout.IsFullWidth(k) ? Layout.Columns : 1;
                    var panel = Layout.Add(k, MeasurePostRowSpan(probe)); // 空いている場所のうち、いちばん上・左
                    SaveLayoutAndRebuild();
                    PostStatus.Text = $"「{panel.DisplayTitle}」を {panel.Row + 1} 段目・{panel.Col + 1} 列目に追加しました";
                };
                group.Items.Add(item);
            }
            menu.Items.Add(group);
        }
        menu.IsOpen = true;
    }

    // ================================================================
    // ドラッグ＆ドロップでの移動
    // ================================================================

    private const string PostDragFormat = "NovelKeeper.PostPanel";
    private Grid? _postGrid;
    private Border? _postDropMarker;
    private PostPanel? _postDragCandidate;
    private Point _postDragStart;

    private static bool IsOnButton(object? source, DependencyObject stop)
    {
        var d = source as DependencyObject;
        while (d != null && !ReferenceEquals(d, stop))
        {
            if (d is Button) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>パネルを、ボタン以外のどこをつかんでも、ドラッグで動かせるようにする</summary>
    private void HookPostDrag(Border card, PostPanel panel)
    {
        if (PostLayout.IsLocked(panel.Kind))
        {
            card.Cursor = System.Windows.Input.Cursors.Arrow; // タイトルは移動できない
            card.ToolTip = null;
            return;
        }
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (IsOnButton(e.OriginalSource, card)) return;
            _postDragCandidate = panel;
            _postDragStart = e.GetPosition(this);
        };
        card.PreviewMouseLeftButtonUp += (_, _) => _postDragCandidate = null;
        card.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || !ReferenceEquals(_postDragCandidate, panel)) return;
            var d = e.GetPosition(this) - _postDragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _postDragCandidate = null;
            try { DragDrop.DoDragDrop(card, new DataObject(PostDragFormat, panel.Id), DragDropEffects.Move); }
            finally { HidePostDropMarker(); }
        };
    }

    private PostPanel? DraggedPostPanel(DragEventArgs e) =>
        e.Data.GetData(PostDragFormat) is string id ? Layout.Panels.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>マウスの位置から、置き先の段・列を求める（いちばん下より下なら、新しい段）</summary>
    private (int Row, int Col, bool NewRow)? PostCellAt(DragEventArgs e)
    {
        if (_postGrid is not { } grid || grid.ActualWidth < 1) return null;
        var pt = e.GetPosition(grid);
        var cols = Layout.Columns;
        var col = Math.Clamp((int)Math.Floor(pt.X / (grid.ActualWidth / cols)), 0, cols - 1);
        // 1段の高さは決まっているので、位置から段が求まる（いまの段数より下なら、新しい段）。
        // グリッドは、外側の余白を相殺するために 4px はみ出している
        var row = Math.Clamp((int)Math.Floor((pt.Y - 4) / PostRowUnit), 0, PostLayout.MaxRow);
        return (row, col, row >= grid.RowDefinitions.Count);
    }

    private void HidePostDropMarker()
    {
        if (_postDropMarker != null) _postDropMarker.Visibility = Visibility.Collapsed;
    }

    private void PostHost_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DraggedPostPanel(e) is not { } panel || PostCellAt(e) is not { } cell || _postDropMarker == null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (cell.NewRow) // いまの段数より下：枠は出さず、案内だけ
        {
            HidePostDropMarker();
            PostStatus.Text = $"ここで離すと、{cell.Row + 1} 段目に置きます（下に段が増えます）";
            return;
        }
        var (row, col, swap) = Layout.PlanMove(panel, cell.Row, cell.Col, out var ok);
        Grid.SetRow(_postDropMarker, row);
        Grid.SetColumn(_postDropMarker, col);
        Grid.SetColumnSpan(_postDropMarker, panel.Span);
        Grid.SetRowSpan(_postDropMarker, panel.RowSpan);
        var shift = ok && swap == null && Layout.WouldShift(panel, row, col);
        var color = !ok ? Color.FromRgb(0xE5, 0x53, 0x4B) : swap != null || shift ? Color.FromRgb(0xE0, 0xA0, 0x30) : PostAccent;
        _postDropMarker.BorderBrush = PostBrush((color.R << 16) | (color.G << 8) | color.B);
        _postDropMarker.Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        _postDropMarker.Visibility = Visibility.Visible;
        PostStatus.Text = !ok ? "その場所には動かせません（タイトルと重なるか、段の上限を超えます）"
            : swap != null ? $"「{swap.DisplayTitle}」と入れ替えます"
            : shift ? "ここに置きます（重なるパネルは下にずれます）" : "ここに移動します";
    }

    private void PostHost_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HidePostDropMarker();
        if (DraggedPostPanel(e) is not { } panel || PostCellAt(e) is not { } cell) return;
        var error = Layout.TryMove(panel, cell.Row, cell.Col);
        PostStatus.Text = error ?? "パネルを移動しました";
        if (error != null) return;
        // 描き直しは、ドロップの処理が終わってから行う
        Dispatcher.BeginInvoke(new Action(SaveLayoutAndRebuild));
    }

    private void PostResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(OwnerWindow, "パネルの並びと設定を、初期配置（2カラム・タイトルと6パネル）に戻しますか？", "NovelKeeper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _cfg.PostLayout = PostLayout.CreateDefault();
        _postEditing = null;
        PostSettingsPanel.Visibility = Visibility.Collapsed;
        _postLoading = true;
        PostColumnsBox.SelectedIndex = Layout.Columns - PostLayout.MinColumns;
        _postLoading = false;
        SaveLayoutAndRebuild();
    }

    private void RemovePostPanel(PostPanel panel)
    {
        if (MessageBox.Show(OwnerWindow, $"「{panel.DisplayTitle}」を削除しますか？", "NovelKeeper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Layout.Panels.Remove(panel);
        if (ReferenceEquals(_postEditing, panel))
        {
            _postEditing = null;
            PostSettingsPanel.Visibility = Visibility.Collapsed;
        }
        SaveLayoutAndRebuild();
    }

}
