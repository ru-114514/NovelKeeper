using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// レポート画面の配置。グラフ・カレンダー・作品の3つの欄を、それぞれ
/// メイン（左・中央）／右／下／別ウィンドウ に置け、隠すこともできる（詳細欄と同じ操作）。
/// </summary>
public partial class MainWindow
{
    private const string SummaryDragFormat = "NovelKeeper.SummaryPanel";
    private static readonly string[] SummaryKeys = { "chart", "calendar", "games", "heat", "scatter", "cleared", "recap", "goal" };
    private const int SummaryBasicCount = 3; // 「並べ方」で並べ直すのは、最初の3つ（グラフ・カレンダー・作品）だけ
    private static readonly Dictionary<string, string> SummaryNames = new()
    {
        ["chart"] = "プレイ時間のグラフ", ["calendar"] = "カレンダー", ["games"] = "プレイした作品",
        ["heat"] = "時間帯ヒートマップ", ["scatter"] = "点数×プレイ時間", ["cleared"] = "クリアと積みゲー",
        ["recap"] = "年間・月間のまとめ", ["goal"] = "目標",
    };
    private static readonly Dictionary<string, string> SummaryShortNames = new()
    {
        ["heat"] = "ヒートマップ", ["scatter"] = "点数×時間", ["cleared"] = "クリア",
        ["recap"] = "まとめ", ["goal"] = "目標",
    };
    private readonly Dictionary<string, ToggleButton> _summaryToggles = new();

    private readonly Dictionary<string, Window> _summaryFloats = new(); // 「別ウィンドウ」にした欄
    private string _summaryStarZone = "Main"; // 上段で伸び縮みする置き場所
    private int _summaryLeftCol = -1, _summaryRightCol = -1; // 左・右の置き場所の列の番号（なければ -1）
    private Point _summaryDragStart;
    private bool _summaryDragging;
    private bool _summaryDragCanceled;

    private FrameworkElement SummaryFrame(string key) => key switch
    {
        "chart" => SummaryChartFrame,
        "calendar" => SummaryCalendarFrame,
        "games" => SummaryGamesFrame,
        _ => _summaryExtra[key].Frame,
    };

    private ToggleButton SummaryToggle(string key) => key switch
    {
        "chart" => SummaryChartToggle,
        "calendar" => SummaryCalendarToggle,
        "games" => SummaryGamesToggle,
        _ => _summaryToggles[key],
    };

    /// <summary>欄の状態（なければ、初期の置き場所で作る：作品は右、ほかはメイン）</summary>
    private SummaryPanelState SummaryPanel(string key)
    {
        if (!_cfg.SummaryPanels.TryGetValue(key, out var s))
            _cfg.SummaryPanels[key] = s = new SummaryPanelState
            {
                Position = key == "games" ? "Right" : "Main",
                Visible = Array.IndexOf(SummaryKeys, key) < SummaryBasicCount, // 追加欄は、表示ボタンで出す
            };
        return s;
    }

    private static void Detach(FrameworkElement e)
    {
        switch (LogicalTreeHelper.GetParent(e))
        {
            case Panel p: p.Children.Remove(e); break;
            case ContentControl c: c.Content = null; break;
        }
    }

    /// <summary>設定に合わせて、3つの欄を並べ直す</summary>
    private void ApplySummaryLayout()
    {
        var onTab = IsVisible && ReferenceEquals(MainTabs.SelectedItem, SummaryTab);

        foreach (var key in SummaryKeys) Detach(SummaryFrame(key));
        SummaryLeftStack.Children.Clear();
        SummaryRightStack.Children.Clear();
        SummaryBottomStack.Children.Clear();
        SummaryMainStack.Children.Clear();

        foreach (var key in SummaryKeys)
        {
            var s = SummaryPanel(key);
            var frame = SummaryFrame(key);
            SummaryToggle(key).IsChecked = s.Visible;

            if (!s.Visible || s.Position == "Float")
            {
                if (s.Visible)
                {
                    // 別ウィンドウ：レポートのタブを開いている間だけ出す
                    ((ScrollViewer)EnsureSummaryFloat(key).Content).Content = frame;
                    if (onTab) ShowSummaryFloat(key); else _summaryFloats[key].Hide();
                }
                else if (_summaryFloats.TryGetValue(key, out var hidden)) hidden.Hide();
                continue;
            }

            if (_summaryFloats.TryGetValue(key, out var w)) w.Hide();
            var zone = s.Position switch
            {
                "Left" => (Panel)SummaryLeftStack,
                "Right" => SummaryRightStack,
                "Bottom" => SummaryBottomStack,
                _ => SummaryMainStack,
            };
            zone.Children.Add(frame);
        }

        // ---- 置き場所の並べ方：（左｜メイン｜右） の上段と、下段 ----
        var hasLeft = SummaryLeftStack.Children.Count > 0;
        var hasMain = SummaryMainStack.Children.Count > 0;
        var hasRight = SummaryRightStack.Children.Count > 0;
        var hasBottom = SummaryBottomStack.Children.Count > 0;
        var top = hasLeft || hasMain || hasRight;
        var grid = SummaryContentGrid;
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();

        // 上段は左・メイン・右の最大3カラム。メインがあればメインが伸び縮みし、なければ右（右もなければ左）が伸び縮みする
        _summaryStarZone = hasMain ? "Main" : hasRight ? "Right" : "Left";
        _summaryLeftCol = _summaryRightCol = -1;
        void AddColumn(FrameworkElement zone, string name, double width, FrameworkElement? splitterBefore)
        {
            if (splitterBefore != null)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(splitterBefore, grid.ColumnDefinitions.Count - 1);
            }
            var star = _summaryStarZone == name;
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = star ? new GridLength(1, GridUnitType.Star) : new GridLength(width),
                MinWidth = star ? 260 : 200,
            });
            Grid.SetColumn(zone, grid.ColumnDefinitions.Count - 1);
        }
        if (hasLeft)
        {
            AddColumn(SummaryLeftBorder, "Left", _cfg.SummaryLeftWidth, null);
            _summaryLeftCol = grid.ColumnDefinitions.Count - 1;
        }
        if (hasMain) AddColumn(SummaryMainScroll, "Main", 0, hasLeft ? SummaryLSplitter : null);
        if (hasRight)
        {
            AddColumn(SummaryRightBorder, "Right", _cfg.SummaryRightWidth, hasLeft || hasMain ? SummaryVSplitter : null);
            _summaryRightCol = grid.ColumnDefinitions.Count - 1;
        }
        if (grid.ColumnDefinitions.Count == 0) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var cols = grid.ColumnDefinitions.Count;

        if (top) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = hasBottom ? 120 : 0 });
        if (top && hasBottom)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cfg.SummaryBottomHeight), MinHeight = 90 });
        }
        else if (hasBottom) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (grid.RowDefinitions.Count == 0) grid.RowDefinitions.Add(new RowDefinition());

        SummaryLeftBorder.Visibility = hasLeft ? Visibility.Visible : Visibility.Collapsed;
        SummaryMainScroll.Visibility = hasMain ? Visibility.Visible : Visibility.Collapsed;
        SummaryRightBorder.Visibility = hasRight ? Visibility.Visible : Visibility.Collapsed;
        SummaryBottomBorder.Visibility = hasBottom ? Visibility.Visible : Visibility.Collapsed;
        SummaryLSplitter.Visibility = hasLeft && hasMain ? Visibility.Visible : Visibility.Collapsed;
        SummaryVSplitter.Visibility = hasRight && (hasLeft || hasMain) ? Visibility.Visible : Visibility.Collapsed;
        SummaryHSplitter.Visibility = top && hasBottom ? Visibility.Visible : Visibility.Collapsed;

        foreach (var e in new UIElement[] { SummaryLeftBorder, SummaryMainScroll, SummaryRightBorder, SummaryLSplitter, SummaryVSplitter })
            Grid.SetRow(e, 0);
        SummaryLeftBorder.BorderThickness = new Thickness(0, 0, hasMain || hasRight ? 1 : 0, 0);
        SummaryRightBorder.BorderThickness = new Thickness(hasLeft || hasMain ? 1 : 0, 0, 0, 0);
        foreach (var sp in new[] { SummaryLSplitter, SummaryVSplitter })
        {
            sp.Width = 6;
            sp.Height = double.NaN;
            sp.ResizeDirection = GridResizeDirection.Columns;
            sp.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
            sp.Cursor = Cursors.SizeWE;
        }

        Grid.SetRow(SummaryHSplitter, 1);
        Grid.SetColumn(SummaryHSplitter, 0);
        Grid.SetColumnSpan(SummaryHSplitter, cols);
        SummaryHSplitter.Height = 6;
        SummaryHSplitter.Width = double.NaN;
        SummaryHSplitter.ResizeDirection = GridResizeDirection.Rows;
        SummaryHSplitter.ResizeBehavior = GridResizeBehavior.PreviousAndNext;
        SummaryHSplitter.Cursor = Cursors.SizeNS;
        Grid.SetRow(SummaryBottomBorder, top ? 2 : 0);
        Grid.SetColumn(SummaryBottomBorder, 0);
        Grid.SetColumnSpan(SummaryBottomBorder, cols);
        SummaryBottomBorder.BorderThickness = new Thickness(0, top ? 1 : 0, 0, 0);

        ShowSummaryGames(); // 作品は、置き場所によって並べ方を変える
        DrawSummaryChart();
        DrawSummaryExtras();
    }

    // ================================================================
    // 別ウィンドウ
    // ================================================================

    private Window EnsureSummaryFloat(string key)
    {
        if (_summaryFloats.TryGetValue(key, out var existing)) return existing;

        var s = SummaryPanel(key);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) => DrawSummaryChart();
        var w = new Window
        {
            Title = SummaryNames[key],
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = Math.Max(280, s.Width),
            Height = Math.Max(200, s.Height),
            Content = scroll,
        };
        // ×で閉じたら「隠す」にする（ウィンドウ自体は使い回す）
        w.Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            s.Visible = false;
            SaveSummaryLayout();
        };
        w.LocationChanged += (_, _) => { if (w.IsVisible) { s.Left = w.Left; s.Top = w.Top; } };
        w.SizeChanged += (_, _) => { if (w.IsVisible) { s.Width = w.Width; s.Height = w.Height; } };
        _summaryFloats[key] = w;
        return w;
    }

    private void ShowSummaryFloat(string key)
    {
        var w = EnsureSummaryFloat(key);
        var s = SummaryPanel(key);

        // 位置が未設定、または画面の外（モニター構成が変わったなど）なら、画面の右寄りに少しずらして出す
        var left = s.Left ?? double.NaN;
        var top = s.Top ?? double.NaN;
        var onScreen = !double.IsNaN(left) && !double.IsNaN(top) &&
                       left > SystemParameters.VirtualScreenLeft - 50 &&
                       top > SystemParameters.VirtualScreenTop - 50 &&
                       left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                       top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;
        if (!onScreen)
        {
            var i = Array.IndexOf(SummaryKeys, key);
            left = Left + Math.Max(0, ActualWidth - w.Width - 40) - i * 30;
            top = Top + 120 + i * 30;
        }
        w.Left = left;
        w.Top = top;
        if (w.Owner == null) w.Owner = this;
        if (!w.IsVisible) w.Show();
    }

    private void HideSummaryFloats()
    {
        foreach (var w in _summaryFloats.Values) w.Hide();
    }

    private void CloseSummaryFloats()
    {
        foreach (var w in _summaryFloats.Values) w.Close();
    }

    // ================================================================
    // 操作
    // ================================================================

    private void SaveSummaryLayout()
    {
        _cfg.Save();
        ApplySummaryLayout();
    }

    private void SummaryPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string key } t) return;
        SummaryPanel(key).Visible = t.IsChecked == true;
        SaveSummaryLayout();
    }

    /// <summary>「並べ方」：3つの欄を、縦に3段／横に3列／初期配置にまとめて並べる</summary>
    private void SummaryArrange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string layout }) return;
        var positions = layout switch
        {
            "Rows" => new[] { "Main", "Main", "Main" },      // 縦に3段（上から グラフ・カレンダー・作品）
            "Columns" => new[] { "Left", "Main", "Right" },  // 横に3列（左から グラフ・カレンダー・作品）
            _ => new[] { "Main", "Main", "Right" },          // 初期配置
        };
        for (var i = 0; i < SummaryBasicCount; i++)
        {
            var s = SummaryPanel(SummaryKeys[i]);
            s.Position = positions[i];
            s.Visible = true;
        }
        SaveSummaryLayout();
    }

    private void SummaryPanelHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SummaryPanel(key).Visible = false;
        SaveSummaryLayout();
    }

    /// <summary>「⇄ 場所」：置き場所を選ぶメニュー</summary>
    private void SummaryPanelMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } button) return;
        var current = SummaryPanel(key).Position;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (position, label) in new[] { ("Main", "メイン（中央）"), ("Left", "左に縦長"), ("Right", "右に縦長"), ("Bottom", "下に横長"), ("Float", "別ウィンドウ") })
        {
            var item = new MenuItem { Header = label, IsCheckable = false, IsChecked = position == current };
            item.Click += (_, _) =>
            {
                var s = SummaryPanel(key);
                s.Position = position;
                s.Visible = true;
                if (position == "Float") { s.Left = null; s.Top = null; } // 画面の右寄りに出す
                SaveSummaryLayout();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>境目をドラッグして大きさを変えたら、その大きさを覚えておく</summary>
    private void SummarySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var cols = SummaryContentGrid.ColumnDefinitions;
        if (ReferenceEquals(sender, SummaryHSplitter) && SummaryContentGrid.RowDefinitions.Count == 3)
            _cfg.SummaryBottomHeight = Math.Round(SummaryContentGrid.RowDefinitions[2].ActualHeight);
        else if (!ReferenceEquals(sender, SummaryHSplitter))
        {
            // 固定の幅の置き場所（伸び縮みしない側）の幅だけ覚える
            if (_summaryLeftCol >= 0 && _summaryStarZone != "Left")
                _cfg.SummaryLeftWidth = Math.Round(cols[_summaryLeftCol].ActualWidth);
            if (_summaryRightCol >= 0 && _summaryStarZone != "Right")
                _cfg.SummaryRightWidth = Math.Round(cols[_summaryRightCol].ActualWidth);
        }
        _cfg.Save();
    }

    private void SummaryFrame_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSummaryChart();

    // ---- 見出しのドラッグで移動 ----

    private void SummaryHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _summaryDragStart = e.GetPosition(this);
    }

    private void SummaryHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_summaryDragging || e.LeftButton != MouseButtonState.Pressed || sender is not Border { Tag: string key } header) return;

        // 見出しの中のボタンを押しているときはドラッグしない
        for (var d = e.OriginalSource as DependencyObject; d != null && d != header; d = ParentOf(d))
            if (d is ButtonBase) return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _summaryDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _summaryDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _summaryDragging = true;
        _summaryDragCanceled = false;
        SummaryDropOverlay.Visibility = Visibility.Visible;
        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(header, new DataObject(SummaryDragFormat, key), DragDropEffects.Move);
        }
        finally
        {
            SummaryDropOverlay.Visibility = Visibility.Collapsed;
            SetZoneHighlight(SummaryDropLeft, false);
            SetZoneHighlight(SummaryDropMain, false);
            SetZoneHighlight(SummaryDropRight, false);
            SetZoneHighlight(SummaryDropBottom, false);
            _summaryDragging = false;
        }

        // 置き場所以外で離した → 別ウィンドウにして、その位置に置く（Esc で取り消したときは何もしない）
        if (result == DragDropEffects.None && !_summaryDragCanceled)
        {
            var cursor = CursorPositionInDip();
            var s = SummaryPanel(key);
            s.Position = "Float";
            s.Visible = true;
            s.Left = cursor.X - 60;
            s.Top = cursor.Y - 15;
            SaveSummaryLayout();
        }
    }

    private void SummaryHeader_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) _summaryDragCanceled = true;
    }

    private void SummaryDropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border zone && e.Data.GetDataPresent(SummaryDragFormat)) SetZoneHighlight(zone, true);
    }

    private void SummaryDropZone_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border zone) SetZoneHighlight(zone, false);
    }

    private void SummaryDropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SummaryDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SummaryDropZone_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string position } || e.Data.GetData(SummaryDragFormat) is not string key) return;
        var s = SummaryPanel(key);
        s.Position = position;
        s.Visible = true;
        e.Effects = DragDropEffects.Move; // 置き場所に置いた（別ウィンドウにはしない）
        e.Handled = true;
        Dispatcher.InvokeAsync(SaveSummaryLayout); // ドラッグが終わってから並べ直す
    }
}
