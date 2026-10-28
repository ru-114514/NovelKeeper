using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// レポートの追加欄：時間帯ヒートマップ・点数×プレイ時間・クリアと積みゲー。
/// グラフ・カレンダー・作品と同じ「欄」として、移動・非表示・別ウィンドウにできる（枠は起動時にコードで作る）。
/// </summary>
public partial class MainWindow
{
    private static readonly string[] SummaryExtraKeys = { "heat", "scatter", "cleared", "recap", "trend", "goal" };
    private static readonly string[] WeekdayNames = { "月", "火", "水", "木", "金", "土", "日" };

    private readonly Dictionary<string, (Border Frame, TextBlock Title, Panel Body)> _summaryExtra = new();
    private List<SessionRow> _summaryRows = new();
    private IReadOnlyList<Game> _summaryGames = Array.Empty<Game>();

    private void InitSummaryExtras()
    {
        var toggleAt = SummaryToggleBar.Children.IndexOf(SummaryGamesToggle) + 1;
        foreach (var key in SummaryExtraKeys)
        {
            _summaryExtra[key] = BuildSummaryExtraFrame(key);
            var toggle = new ToggleButton { Content = SummaryShortNames[key], Tag = key, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 3, 8, 3) };
            toggle.Click += SummaryPanelToggle_Click;
            SummaryToggleBar.Children.Insert(toggleAt++, toggle);
            _summaryToggles[key] = toggle;
        }
    }

    private (Border Frame, TextBlock Title, Panel Body) BuildSummaryExtraFrame(string key)
    {
        var hide = new Button { Content = "✕", Tag = key, Cursor = System.Windows.Input.Cursors.Arrow, ToolTip = "この欄を隠す（上の表示ボタンで再表示）", Padding = new Thickness(8, 0, 8, 0) };
        hide.Click += SummaryPanelHide_Click;
        var move = new Button { Content = "⇄ 場所", Tag = key, Cursor = System.Windows.Input.Cursors.Arrow, Padding = new Thickness(8, 0, 8, 0) };
        move.Click += SummaryPanelMove_Click;
        DockPanel.SetDock(hide, Dock.Right);
        DockPanel.SetDock(move, Dock.Right);

        var headerRow = new DockPanel();
        headerRow.Children.Add(hide);
        headerRow.Children.Add(move);
        headerRow.Children.Add(new TextBlock { Text = "⠿ " + SummaryNames[key], VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });
        var header = new Border
        {
            Tag = key, Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)), Padding = new Thickness(10, 4, 10, 4),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            ToolTip = "ドラッグして、メイン・右・下、または好きな場所（別ウィンドウ）に移動できます",
            Child = headerRow,
        };
        header.PreviewMouseLeftButtonDown += SummaryHeader_PreviewMouseLeftButtonDown;
        header.MouseMove += SummaryHeader_MouseMove;
        header.QueryContinueDrag += SummaryHeader_QueryContinueDrag;
        DockPanel.SetDock(header, Dock.Top);

        var title = new TextBlock { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        Panel body = key == "scatter" || key == "heat" ? new Canvas { HorizontalAlignment = HorizontalAlignment.Left } : new StackPanel();
        var content = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        content.Children.Add(title);
        content.Children.Add(body);

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(content);
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)), BorderThickness = new Thickness(1),
            Margin = new Thickness(2, 2, 6, 6), Child = dock,
        };
        frame.SizeChanged += (_, _) => DrawSummaryExtras();
        return (frame, title, body);
    }

    /// <summary>追加欄を描き直す（期間を読み直したとき・欄の大きさが変わったとき）</summary>
    private void DrawSummaryExtras()
    {
        if (_summary is not { } r || _summaryExtra.Count == 0) return;
        DrawSummaryHeatmap(r);
        DrawSummaryScatter(r);
        DrawSummaryCleared(r);
        DrawSummaryRecap();
        DrawSummaryTrend();
        DrawSummaryGoal();
    }

    // ---- 時間帯ヒートマップ（曜日×時間帯） ----

    private void DrawSummaryHeatmap(SummaryResult r)
    {
        var (frame, title, body) = _summaryExtra["heat"];
        var c = (Canvas)body;
        c.Children.Clear();
        var cells = SummaryExtras.Heatmap(_summaryRows, r.From, r.To);
        var max = cells.Cast<double>().Max();
        title.Text = max < 1 ? "この期間のプレイ記録はありません" : "曜日×時間帯のプレイ時間（濃いほど長い）";

        const double left = 26, top = 18;
        var availW = frame.ActualWidth - 26;
        if (availW < 150) return;
        var cell = Math.Clamp((availW - left) / 24, 10, 28);
        c.Width = left + cell * 24 + 2;
        c.Height = top + cell * 7 + 2;

        for (var h = 0; h < 24; h += 3)
            Add(c, new TextBlock { Text = h.ToString(), FontSize = 11, Opacity = 0.8 }, left + cell * h, 0);
        for (var d = 0; d < 7; d++)
        {
            Add(c, new TextBlock { Text = WeekdayNames[d], FontSize = 11, Opacity = 0.8, Width = left - 6, TextAlignment = TextAlignment.Right }, 0, top + cell * d + (cell - 14) / 2);
            for (var h = 0; h < 24; h++)
            {
                var v = cells[d, h];
                var level = v < 1 ? 0 : v / max <= 0.25 ? 1 : v / max <= 0.5 ? 2 : v / max <= 0.75 ? 3 : 4;
                var tip = $"{WeekdayNames[d]}曜 {h}時台\n" + (v < 1 ? "プレイなし" : SummaryCalculator.FormatDuration(v));
                Add(c, new Border { Width = cell - 2, Height = cell - 2, CornerRadius = new CornerRadius(3), Background = CalendarBrushes[level], ToolTip = tip },
                    left + cell * h + 1, top + cell * d + 1);
            }
        }
    }

    // ---- 点数×プレイ時間（散布図） ----

    private void DrawSummaryScatter(SummaryResult r)
    {
        var (frame, title, body) = _summaryExtra["scatter"];
        var c = (Canvas)body;
        c.Children.Clear();
        var points = SummaryExtras.ScorePoints(r.Games, _summaryGames);
        title.Text = points.Count == 0
            ? "この期間に遊んだ作品に、得点を付けたものはありません"
            : $"点数×プレイ時間（期間内に遊んだ{points.Count}本。横：期間内のプレイ時間／縦：点数）";
        if (points.Count == 0) { c.Width = c.Height = 0; return; }

        const double left = 40, right = 14, top = 10, bottom = 26;
        var availW = frame.ActualWidth - 26;
        if (availW < 180) return;
        var w = Math.Min(availW, 640) - left - right;
        const double h = 230;
        c.Width = left + w + right;
        c.Height = top + h + bottom;

        var minScore = points.Min(p => p.Score);
        var yMin = Math.Max(0, Math.Floor((minScore - 5) / 10.0) * 10);
        var yMax = Math.Max(100, points.Max(p => p.Score));
        var maxSec = points.Max(p => p.Seconds);
        var step = SummaryCalculator.AxisStep(maxSec);
        var xMax = Math.Max(step, Math.Ceiling(maxSec / step) * step);

        var yStep = (yMax - yMin) <= 40 ? 10 : 20;
        for (var v = yMin; v <= yMax + 0.1; v += yStep)
        {
            var y = top + h - h * (v - yMin) / (yMax - yMin);
            Add(c, new Rectangle { Width = w, Height = 1, Fill = Brushes.Gray, Opacity = 0.35 }, left, y);
            Add(c, new TextBlock { Text = v.ToString("0"), FontSize = 11, Opacity = 0.8, Width = left - 6, TextAlignment = TextAlignment.Right }, 0, y - 8);
        }
        for (var v = 0d; v <= xMax + 0.1; v += step)
        {
            var x = left + w * v / xMax;
            Add(c, new Rectangle { Width = 1, Height = h, Fill = Brushes.Gray, Opacity = 0.2 }, x, top);
            Add(c, new TextBlock { Text = AxisText(v), FontSize = 11, Opacity = 0.8 }, x - 6, top + h + 4);
        }
        foreach (var p in points)
        {
            var x = left + w * p.Seconds / xMax;
            var y = top + h - h * (p.Score - yMin) / (yMax - yMin);
            var dot = new Ellipse
            {
                Width = 11, Height = 11, Fill = BarBrush, Opacity = 0.8, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{p.Title}\n点数 {p.Score}／{SummaryCalculator.FormatDuration(p.Seconds)}\nクリックすると、一覧タブでこの作品を開きます",
                DataContext = new SummaryRow(p.GameId, p.Title, "", 0, 0),
            };
            dot.MouseLeftButtonUp += SummaryGameItem_Click;
            Add(c, dot, x - 5.5, y - 5.5);
        }
    }

    // ---- クリアと積みゲー ----

    private void DrawSummaryCleared(SummaryResult r)
    {
        var (_, title, body) = _summaryExtra["cleared"];
        body.Children.Clear();
        var cleared = SummaryExtras.Cleared(_summaryGames, r.From, r.To);
        var scores = cleared.Where(x => x.Score != null).Select(x => (double)x.Score!.Value).ToList();
        var avg = scores.Count == 0 ? "" : $"（平均点 {scores.Average():0.#}）";
        var backlog = SummaryExtras.BacklogCount(_summaryGames, _cfg.BacklogExcludedIds.ToHashSet());
        var change = BacklogHistory.Change(_cfg.BacklogSnapshots, r.From, r.To, backlog, DateTime.Today);
        title.Text = $"期間内にクリアした作品 {cleared.Count}本{avg}　／　現在の積みゲー {backlog}本" +
                     (change == null ? "" : $"（{BacklogHistory.Describe(change)}）");

        if (cleared.Count == 0)
        {
            body.Children.Add(new TextBlock { Text = "この期間にクリアした作品はありません", Opacity = 0.7 });
            return;
        }
        foreach (var item in cleared)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent,
                DataContext = new SummaryRow(item.GameId, item.Title, "", 0, 0), ToolTip = "クリックすると、一覧タブでこの作品を開きます" };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{item.Date:M/d}", Opacity = 0.7, Width = 44 });
            var name = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var score = new TextBlock { Text = item.Score is int s ? $"{s}点" : "", Margin = new Thickness(8, 0, 0, 0), Opacity = 0.85 };
            Grid.SetColumn(score, 2);
            row.Children.Add(score);
            row.MouseLeftButtonUp += SummaryGameItem_Click;
            body.Children.Add(row);
        }
    }
}
