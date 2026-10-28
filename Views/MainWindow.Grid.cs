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

/// <summary>メイン画面のうち、一覧の表示（横スクロール・拡大縮小・列の幅）を担当する部分</summary>
public partial class MainWindow
{
    // ================================================================
    // 一覧の左右スライダー（一覧の中の横スクロールと連動させる）
    // ================================================================

    private ScrollViewer? _gridScroll;
    private bool _syncingScroll;

    private bool _gridHooked;

    private void GameGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // タブを切り替えるたびに呼ばれるので、つなぐのは最初の1回だけ
        if (_gridHooked) return;
        _gridHooked = true;
        ApplyColumnLayout();
        RestoreColumnWidths();
        AttachGridScroll();
        // 見た目の部品がまだできていないときに備えて、配置が終わるたびに確認する
        GameGrid.LayoutUpdated += (_, _) => { if (_gridScroll == null) AttachGridScroll(); else SyncHScroll(); };
        GameGrid.SizeChanged += (_, _) => SyncHScroll();
        GameGrid.ColumnReordered += (_, _) => SaveColumnOrderFromGrid();
        GameGrid.ContextMenuOpening += GameGrid_ContextMenuOpening;
        GameGrid.ContextMenu = new ContextMenu(); // 中身は開くたびに作る
    }

    private bool _columnWidthsHooked;

    /// <summary>保存しておいた列の幅を戻し、以後、幅を変えたら保存する</summary>
    private void RestoreColumnWidths()
    {
        if (_columnWidthsHooked) return;
        _columnWidthsHooked = true;

        var widthDescriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        foreach (var col in GameGrid.Columns)
        {
            var key = col.Header?.ToString() ?? "";
            if (_cfg.ColumnWidths.TryGetValue(key, out var w) && w >= col.MinWidth)
                col.Width = new DataGridLength(w);

            widthDescriptor.AddValueChanged(col, (_, _) =>
            {
                if (col.Width.IsAbsolute) _cfg.ColumnWidths[key] = Math.Round(col.Width.Value);
                else if (col.ActualWidth > 0) _cfg.ColumnWidths[key] = Math.Round(col.ActualWidth);
                _saveColumnsTimer ??= CreateSaveTimer();
                _saveColumnsTimer.Stop();
                _saveColumnsTimer.Start(); // ドラッグ中に何度も保存しないよう、止まってから1秒後に保存
            });
        }
    }

    private DispatcherTimer? _saveColumnsTimer;

    private DispatcherTimer CreateSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => { t.Stop(); _cfg.Save(); };
        return t;
    }

    /// <summary>一覧の中のスクロール部品を見つけて、スライダーとつなぐ</summary>
    private void AttachGridScroll()
    {
        if (_gridScroll != null) return;
        GameGrid.ApplyTemplate();
        _gridScroll = FindChild<ScrollViewer>(GameGrid);
        if (_gridScroll == null) return;
        _gridScroll.ScrollChanged += (_, _) => SyncHScroll();
        SyncHScroll();
    }

    /// <summary>一覧の横スクロールの状態を、下のスライダーに反映する</summary>
    private void SyncHScroll()
    {
        if (_gridScroll == null) return;
        if (Math.Abs(ListHScroll.Maximum - Math.Max(0, _gridScroll.ScrollableWidth)) < 0.5 &&
            Math.Abs(ListHScroll.Value - _gridScroll.HorizontalOffset) < 0.5 &&
            Math.Abs(ListHScroll.ViewportSize - Math.Max(1, _gridScroll.ViewportWidth)) < 0.5) return; // 変化なし
        _syncingScroll = true;
        ListHScroll.Maximum = Math.Max(0, _gridScroll.ScrollableWidth);
        ListHScroll.ViewportSize = Math.Max(1, _gridScroll.ViewportWidth);
        ListHScroll.Value = _gridScroll.HorizontalOffset;
        // Excel のように常に表示・操作できる状態にしておく（全部の列が見えているときは、つまみが端から端までになる）
        _syncingScroll = false;
    }

    private void ListHScroll_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingScroll) _gridScroll?.ScrollToHorizontalOffset(e.NewValue);
    }

    /// <summary>Shift＋ホイールで左右に動かす。Ctrl＋ホイールで拡大縮小</summary>
    private void GameGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SetListZoom(_cfg.ListZoom + (e.Delta > 0 ? 0.1 : -0.1));
            e.Handled = true;
            return;
        }
        if (_gridScroll == null || (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        _gridScroll.ScrollToHorizontalOffset(_gridScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private const double MinZoom = 0.6;
    private const double MaxZoom = 2.0;

    /// <summary>一覧の表示倍率を変える（60%～200%）。倍率は保存して次回も使う</summary>
    private void SetListZoom(double zoom, bool showStatus = true)
    {
        zoom = Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 1);
        GameGrid.LayoutTransform = Math.Abs(zoom - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(zoom, zoom);
        if (Math.Abs(zoom - _cfg.ListZoom) > 0.001)
        {
            _cfg.ListZoom = zoom;
            _cfg.Save();
        }
        ZoomText.Text = $"{zoom * 100:0}%";
        if (showStatus) StatusText.Text = $"一覧の表示倍率：{zoom * 100:0}%（Ctrl＋0 で 100% に戻す）";
    }

    /// <summary>Ctrl＋0 で 100% に戻す</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            SetListZoom(1.0);
            e.Handled = true;
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is T deeper) return deeper;
        }
        return null;
    }

}
