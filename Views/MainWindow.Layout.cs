using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;
using Microsoft.Win32;

namespace NovelKeeper.Views;

/// <summary>メイン画面のうち、詳細欄の配置（右・下・別ウィンドウ・ドラッグでの移動）を担当する部分</summary>
public partial class MainWindow
{
    // ================================================================
    // 詳細欄の配置（右に縦長／下に横長／非表示）
    // ================================================================

    private const string DetailDragFormat = "NovelKeeper.DetailPanel";
    private Point _detailDragStart;
    private bool _detailDragging;

    private Window? _floatWindow;   // 「自由な位置」に置いたときの別ウィンドウ
    private bool _detailDragCanceled;

    /// <summary>設定に合わせて、一覧と詳細欄を並べ直す（右／下／別ウィンドウ／非表示）</summary>
    private void ApplyDetailLayout()
    {
        var visible = _cfg.DetailVisible;
        var position = _cfg.DetailPosition;
        DetailToggleButton.Content = visible ? "詳細欄を隠す" : "詳細欄を表示";
        SyncDetailPositionBox();
        Dispatcher.InvokeAsync(UpdateToolbarOverflow, DispatcherPriority.Loaded); // ボタンの文字の長さが変わるため

        // ---- 詳細欄を、一覧の画面と別ウィンドウのどちらに置くか ----
        if (position == "Float")
        {
            var w = EnsureFloatWindow();
            if (!ReferenceEquals(w.Content, DetailBorder))
            {
                ContentGrid.Children.Remove(DetailBorder);
                w.Content = DetailBorder;
            }
        }
        else if (!ContentGrid.Children.Contains(DetailBorder))
        {
            if (_floatWindow != null) _floatWindow.Content = null;
            ContentGrid.Children.Add(DetailBorder);
        }

        // ---- 一覧の画面の並べ方 ----
        ContentGrid.RowDefinitions.Clear();
        ContentGrid.ColumnDefinitions.Clear();
        foreach (UIElement child in ContentGrid.Children)
        {
            Grid.SetRow(child, 0);
            Grid.SetColumn(child, 0);
        }

        var docked = visible && position != "Float";
        DetailSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        DetailBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (position == "Float")
        {
            DetailBorder.BorderThickness = new Thickness(0);
            MoveDetailButton.Content = "⇥ 右に戻す";
            if (visible && IsVisible) ShowFloatWindow();
            else _floatWindow?.Hide();
            return;
        }
        _floatWindow?.Hide();
        if (!docked) return;

        if (position == "Bottom")
        {
            // 下に横長：一覧 ／ 境目 ／ 詳細欄
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ContentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cfg.DetailHeight), MinHeight = 120 });
            Grid.SetRow(DetailSplitter, 1);
            Grid.SetRow(DetailBorder, 2);

            DetailSplitter.Height = 6;
            DetailSplitter.Width = double.NaN;
            DetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            DetailSplitter.ResizeDirection = GridResizeDirection.Rows;
            DetailSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            DetailSplitter.Cursor = Cursors.SizeNS;
            DetailBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            MoveDetailButton.Content = "⇥ 右へ";
        }
        else
        {
            // 右に縦長：一覧 ｜ 境目 ｜ 詳細欄
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cfg.DetailWidth), MinWidth = 260 });
            Grid.SetColumn(DetailSplitter, 1);
            Grid.SetColumn(DetailBorder, 2);

            DetailSplitter.Width = 6;
            DetailSplitter.Height = double.NaN;
            DetailSplitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            DetailSplitter.VerticalAlignment = VerticalAlignment.Stretch;
            DetailSplitter.ResizeDirection = GridResizeDirection.Columns;
            DetailSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            DetailSplitter.Cursor = Cursors.SizeWE;
            DetailBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            MoveDetailButton.Content = "⤓ 下へ";
        }
    }

    /// <summary>別ウィンドウ（自由な位置に置ける詳細欄）を用意する</summary>
    private Window EnsureFloatWindow()
    {
        if (_floatWindow != null) return _floatWindow;

        var w = new Window
        {
            Title = "作品の詳細",
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(280, _cfg.FloatWidth),
            Height = Math.Max(200, _cfg.FloatHeight),
        };
        // ×で閉じたら「非表示」にする（ウィンドウ自体は使い回す）
        w.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            _cfg.DetailVisible = false;
            SaveLayout();
        };
        w.LocationChanged += (_, _) => { if (w.IsVisible) { _cfg.FloatLeft = w.Left; _cfg.FloatTop = w.Top; } };
        w.SizeChanged += (_, _) => { if (w.IsVisible) { _cfg.FloatWidth = w.Width; _cfg.FloatHeight = w.Height; } };
        _floatWindow = w;
        return w;
    }

    private void ShowFloatWindow()
    {
        var w = EnsureFloatWindow();

        // 位置が未設定、または画面の外（モニター構成が変わったなど）なら、一覧の右寄りに出す
        var left = _cfg.FloatLeft ?? double.NaN;
        var top = _cfg.FloatTop ?? double.NaN;
        var onScreen = !double.IsNaN(left) && !double.IsNaN(top) &&
                       left > SystemParameters.VirtualScreenLeft - 50 &&
                       top > SystemParameters.VirtualScreenTop - 50 &&
                       left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                       top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
        if (!onScreen)
        {
            left = Left + Math.Max(0, ActualWidth - w.Width - 40);
            top = Top + 80;
        }
        w.Left = left;
        w.Top = top;
        if (w.Owner == null) w.Owner = this; // 一覧の画面が表示されてからでないと設定できない
        if (!w.IsVisible) w.Show();
    }

    private void SaveLayout()
    {
        _cfg.Save();
        ApplyDetailLayout();
    }

    private void ToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailVisible = !_cfg.DetailVisible;
        SaveLayout();
    }

    private void HideDetail_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailVisible = false;
        SaveLayout();
    }

    private void ToggleDetailPosition_Click(object sender, RoutedEventArgs e)
    {
        _cfg.DetailPosition = _cfg.DetailPosition == "Right" ? "Bottom" : "Right"; // 下→右、別ウィンドウ→右
        SaveLayout();
    }

    /// <summary>境目をドラッグして大きさを変えたら、その大きさを覚えておく</summary>
    private void DetailSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_cfg.DetailPosition == "Bottom" && ContentGrid.RowDefinitions.Count == 3)
            _cfg.DetailHeight = Math.Round(ContentGrid.RowDefinitions[2].ActualHeight);
        else if (ContentGrid.ColumnDefinitions.Count == 3)
            _cfg.DetailWidth = Math.Round(ContentGrid.ColumnDefinitions[2].ActualWidth);
        _cfg.Save();
    }

    // ---- 見出しのドラッグで移動 ----

    private void DetailHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _detailDragStart = e.GetPosition(this);
    }

    private void DetailHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_detailDragging || e.LeftButton != MouseButtonState.Pressed) return;

        // 見出しの中のボタンを押しているときはドラッグしない
        for (var d = e.OriginalSource as DependencyObject; d != null && d != DetailHeader; d = ParentOf(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _detailDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _detailDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _detailDragging = true;
        _detailDragCanceled = false;
        DropOverlay.Visibility = Visibility.Visible;
        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(DetailHeader, new DataObject(DetailDragFormat, "detail"), DragDropEffects.Move);
        }
        finally
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            SetZoneHighlight(DropRight, false);
            SetZoneHighlight(DropBottom, false);
            _detailDragging = false;
        }

        // 「右」「下」の置き場所以外で離した → 別ウィンドウにして、その位置に置く（Esc で取り消したときは何もしない）
        if (result == DragDropEffects.None && !_detailDragCanceled)
        {
            var cursor = CursorPositionInDip();
            _cfg.DetailPosition = "Float";
            _cfg.DetailVisible = true;
            _cfg.FloatLeft = cursor.X - 60;
            _cfg.FloatTop = cursor.Y - 15;
            SaveLayout();
        }
    }

    private void DetailHeader_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _detailDragCanceled = true;
    }

    /// <summary>マウスカーソルの位置（画面の拡大率を考慮した WPF の座標）</summary>
    private Point CursorPositionInDip()
    {
        var p = System.Windows.Forms.Cursor.Position; // 実ピクセル
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(new Point(p.X, p.Y));
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border zone && e.Data.GetDataPresent(DetailDragFormat)) SetZoneHighlight(zone, true);
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border zone) SetZoneHighlight(zone, false);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DetailDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string position } || !e.Data.GetDataPresent(DetailDragFormat)) return;
        _cfg.DetailPosition = position;
        _cfg.DetailVisible = true;
        e.Effects = DragDropEffects.Move; // 置き場所に置いた（別ウィンドウにはしない）
        e.Handled = true;
        Dispatcher.InvokeAsync(SaveLayout); // ドラッグが終わってから並べ直す
    }

    private static void SetZoneHighlight(Border zone, bool on)
    {
        zone.Background = new SolidColorBrush(on ? Color.FromArgb(0x70, 0x4A, 0x90, 0xE2) : Color.FromArgb(0x26, 0x4A, 0x90, 0xE2));
    }

    /// <summary>詳細設定タブを開く（通知領域のメニューからも使う）</summary>
    private void OpenSettings()
    {
        MainTabs.SelectedItem = SettingsTab;
    }

}
