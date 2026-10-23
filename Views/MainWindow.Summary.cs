using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>レポートタブ：期間を選んで、プレイ時間のグラフ・カレンダー・プレイした作品を見る</summary>
public partial class MainWindow
{
    private sealed record SummaryRow(long GameId, string Title, string Text, double Percent, double Width);

    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));

    private const double MaxChartHeight = 340; // グラフの高さの上限

    private SummaryResult? _summary;
    private bool _summaryLoading; // 日付欄を書き換えている間は、変更イベントで読み直さない

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>起動時：期間を「直近1週間」にしておく（画面を開いたときに集計される）</summary>
    private void InitSummaryTab()
    {
        SetSummaryRange(DateTime.Today.AddDays(-6), DateTime.Today);
        // 置き場所の大きさが変わったら、グラフを描き直す（高さを置き場所に合わせるため）
        foreach (var sv in new[] { SummaryMainScroll, SummaryRightScroll, SummaryBottomScroll })
            sv.SizeChanged += (_, _) => DrawSummaryChart();
        InitSummaryExtras();
        ApplySummaryLayout();
        MainTabs.SelectionChanged += (s, e) =>
        {
            // タブの中の一覧などの選択変更でも届くので、タブ自身の切り替えだけ見る
            if (e.OriginalSource != MainTabs) return;
            ApplySummaryLayout(); // 別ウィンドウの欄は、レポートのタブを開いている間だけ出す
            if (ReferenceEquals(MainTabs.SelectedItem, SummaryTab)) RefreshSummary();
        };
    }

    private void SetSummaryRange(DateTime from, DateTime to)
    {
        _summaryLoading = true;
        SummaryFromPicker.SelectedDate = from;
        SummaryToPicker.SelectedDate = to;
        _summaryLoading = false;
    }

    private void SummaryPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var today = DateTime.Today;
        var days = int.Parse(tag);
        if (days == 0)
        {
            // 全期間：最初の記録の日から今日まで
            var first = _db.GetSessions(DateTime.MinValue, today.AddDays(1)).Select(r => r.Start.Date).DefaultIfEmpty(today).Min();
            SetSummaryRange(first, today);
        }
        else if (days == 30) SetSummaryRange(today.AddMonths(-1).AddDays(1), today);
        else SetSummaryRange(today.AddDays(-(days - 1)), today);
        RefreshSummary();
    }

    private void SummaryDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_summaryLoading && _db != null) RefreshSummary();
    }

    private void RefreshSummary()
    {
        var from = (SummaryFromPicker.SelectedDate ?? DateTime.Today).Date;
        var to = (SummaryToPicker.SelectedDate ?? DateTime.Today).Date;
        if (to < from) (from, to) = (to, from);

        var rows = _summaryRows = _db.GetSessions(from, to.AddDays(1)).ToList();
        _summaryGames = _db.GetAll();
        var r = _summary = SummaryCalculator.Build(rows, from, to);

        var range = r.DayCount == 1 ? $"{from:yyyy/MM/dd}" : $"{from:yyyy/MM/dd} 〜 {to:yyyy/MM/dd}（{r.DayCount}日間）";
        SummaryHeadline.Text = $"{range}　合計 {SummaryCalculator.FormatDuration(r.TotalSeconds)}";
        var note = r.DayCount == 1
            ? $"プレイした作品 {r.Games.Count}本"
            : $"1日あたり平均 {SummaryCalculator.FormatDuration(r.TotalSeconds / r.DayCount)}　／　" +
              $"プレイした日 {r.PlayedDays}/{r.DayCount}日　／　プレイした作品 {r.Games.Count}本";

        // 直前の同じ長さの期間との比較（今日なら前日、1週間なら前の1週間）
        var (pf, pt) = SummaryCalculator.PreviousRange(from, to);
        if (pf >= new DateTime(1900, 1, 1))
        {
            var prev = SummaryCalculator.Build(_db.GetSessions(pf, pt.AddDays(1)), pf, pt);
            var label = r.DayCount == 1 ? "前日" : $"前の{r.DayCount}日間";
            note += prev.TotalSeconds < 1
                ? $"　／　{label}は記録なし"
                : $"　／　{label}比 {SummaryCalculator.FormatDelta(r.TotalSeconds, prev.TotalSeconds)}";
        }
        SummaryNote.Text = note;

        var parts = new List<string>();
        if (r.DayCount > 1 && r.BestDay is { } best)
            parts.Add($"最も遊んだ日：{best.Day:M/d}（{SummaryCalculator.Weekday(best.Day)}） {SummaryCalculator.FormatDuration(best.Seconds)}");
        if (r.LongestSession is { } longest)
            parts.Add($"最長の1回：{longest.Title} {SummaryCalculator.FormatDuration(longest.Seconds)}（{longest.Start:M/d}）");
        if (r.PeakHour is int peak) parts.Add($"よく遊ぶ時間帯：{peak}時台");
        SummaryHighlights.Text = string.Join("　／　", parts);
        SummaryHighlights.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryChartTitle.Text = r.IsHourly ? "時間帯別のプレイ時間（0:00〜23:59）"
            : r.DayCount <= SummaryCalculator.MaxDailyBars ? "日別のプレイ時間"
            : r.DayCount <= SummaryCalculator.MaxWeeklyBars ? "週別のプレイ時間（月曜始まり）"
            : "月別のプレイ時間";

        ShowSummaryGames();
        DrawSummaryChart();
        DrawSummaryExtras();
    }

    /// <summary>作品欄の中身。下に置いたときは、横に並べて見やすくする（1件の幅を決めて折り返す）</summary>
    private void ShowSummaryGames()
    {
        if (_summary is not { } r) return;
        // メインか、作品だけの下の欄のように広い置き場所では、横に並べて折り返す
        var pos = SummaryPanel("games").Position;
        var wide = pos == "Main" || (pos == "Bottom" && SummaryBottomStack.Children.Count == 1);
        var width = wide ? 300 : double.NaN;
        SummaryGames.ItemsPanel = wide ? WrapPanelTemplate : StackPanelTemplate;
        SummaryGames.ItemsSource = r.Games
            .Select(g => new SummaryRow(g.GameId, g.Title,
                $"{SummaryCalculator.FormatDuration(g.Seconds)}（{g.Seconds / r.TotalSeconds:P0}）",
                g.Seconds / r.TotalSeconds * 100, width))
            .ToList();
        SummaryEmpty.Visibility = r.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ItemsPanelTemplate PanelTemplate(string panel) => (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
        $"<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><{panel}/></ItemsPanelTemplate>");

    private static readonly ItemsPanelTemplate StackPanelTemplate = PanelTemplate("StackPanel");
    private static readonly ItemsPanelTemplate WrapPanelTemplate = PanelTemplate("WrapPanel");

    // ================================================================
    // 棒グラフ（Canvas に描く。色はテーマに合わせて、文字は標準の色・罫線は灰色の半透明）
    // ================================================================

    private void DrawSummaryChart()
    {
        var c = SummaryChart;
        c.Children.Clear();
        if (_summary is not { } r) return;
        var buckets = r.Buckets;

        const double left = 46, right = 10, top = 12, bottom = 24;
        // 棒1本の幅と高さに上限を付ける（広い画面でも、棒が間延びしない見やすい大きさにする）
        var availW = SummaryChartFrame.ActualWidth - 26; // 欄の余白と枠の分
        if (availW < 120) return;
        var maxSlot = r.IsHourly ? 36 : 56;
        var slot = Math.Min((availW - left - right) / Math.Max(1, buckets.Count), maxSlot);
        double w = slot * Math.Max(1, buckets.Count);
        // 高さは、置いている場所（メイン・右・下・別ウィンドウ）の見える高さに合わせる（見出しなどの分を引く）
        // 縦に積んだ欄が複数あるときは、その数で割って、全部が収まりやすい高さにする
        double hostH = 400;
        for (var d = ParentOf(SummaryChartFrame); d != null; d = ParentOf(d))
            if (d is ScrollViewer sv) { hostH = sv.ActualHeight; break; }
        var stacked = SummaryChartFrame.Parent is StackPanel sp ? Math.Max(1, sp.Children.Count) : 1;
        var chartH = Math.Clamp(hostH / stacked - 90, 160, MaxChartHeight);
        double h = chartH - top - bottom;
        SummaryChartBorder.Width = left + w + right + 2;
        SummaryChartBorder.Height = chartH + 2;

        var max = buckets.Count == 0 ? 0 : buckets.Max(b => b.Seconds);
        var step = SummaryCalculator.AxisStep(max);
        var axisMax = Math.Max(step, Math.Ceiling(max / step) * step);

        // 横線と縦軸の目盛り
        for (var v = 0d; v <= axisMax + 0.1; v += step)
        {
            var y = top + h - h * v / axisMax;
            Add(c, new Rectangle { Width = w, Height = 1, Fill = Brushes.Gray, Opacity = 0.35 }, left, y);
            var label = new TextBlock { Text = AxisText(v), FontSize = 11, Opacity = 0.8, Width = left - 6, TextAlignment = TextAlignment.Right };
            Add(c, label, 0, y - 8);
        }

        var every = (int)Math.Ceiling(34 / slot); // 横軸の文字が重ならない間隔
        for (var i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            var x = left + slot * i;
            var barH = h * b.Seconds / axisMax;
            var tip = $"{b.Detail}\n{SummaryCalculator.FormatDuration(b.Seconds)}";
            if (barH > 0)
            {
                var bar = new Rectangle { Width = Math.Max(1, slot * 0.72), Height = Math.Max(1, barH), Fill = BarBrush };
                Add(c, bar, x + slot * 0.14, top + h - Math.Max(1, barH));
            }
            // 棒のない日もマウスを合わせると中身が見えるように、枠いっぱいの透明な当たり判定を重ねる
            Add(c, new Rectangle { Width = slot, Height = h, Fill = Brushes.Transparent, ToolTip = tip }, x, top);
            if (i % every == 0)
                Add(c, new TextBlock { Text = b.Label, FontSize = 11, Opacity = 0.8, Width = slot * every, TextAlignment = TextAlignment.Left }, x, top + h + 4);
        }
        DrawSummaryCalendar(r);
    }

    // ================================================================
    // カレンダー（遊んだ日を濃淡で表す。1日だけのときは出さない）
    // ================================================================

    private static readonly Brush[] CalendarBrushes =
    {
        Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80))),
        Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0x8D, 0xFF))),
    };

    private void DrawSummaryCalendar(SummaryResult r)
    {
        var panel = SummaryCalendar;
        panel.Children.Clear();
        if (r.DayCount < 2)
        {
            SummaryCalendarTitle.Text = "期間が2日以上のときに表示します";
            return;
        }

        // 期間にかかる月を並べる。長い期間は、直近12か月だけ表示する
        const int maxMonths = 12;
        var firstMonth = new DateTime(r.From.Year, r.From.Month, 1);
        var lastMonth = new DateTime(r.To.Year, r.To.Month, 1);
        var cut = false;
        if (((lastMonth.Year - firstMonth.Year) * 12 + lastMonth.Month - firstMonth.Month) >= maxMonths)
        {
            firstMonth = lastMonth.AddMonths(-(maxMonths - 1));
            cut = true;
        }
        var from = r.From > firstMonth ? r.From : firstMonth;
        SummaryCalendarTitle.Text = "遊んだ日（濃いほど長い）" + (cut ? "　※直近12か月を表示" : "");

        var max = 0d;
        for (var d = from; d <= r.To; d = d.AddDays(1)) max = Math.Max(max, r.DailySeconds.GetValueOrDefault(d));

        const double cellW = 32, cellH = 26;
        for (var m = firstMonth; m <= lastMonth; m = m.AddMonths(1))
        {
            // 月ごとの一般的なカレンダー（日〜土を横に並べ、週ごとに下へ）
            var grid = new Grid();
            for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellW) });
            for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellH) });

            for (var i = 0; i < 7; i++)
            {
                var head = new TextBlock
                {
                    Text = "日月火水木金土"[i].ToString(), FontSize = 11, Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(head, i);
                grid.Children.Add(head);
            }

            var offset = (int)m.DayOfWeek;
            var days = DateTime.DaysInMonth(m.Year, m.Month);
            for (var day = 1; day <= days; day++)
            {
                var d = m.AddDays(day - 1);
                var inRange = d >= from && d <= r.To;
                var v = inRange ? r.DailySeconds.GetValueOrDefault(d) : 0;
                var level = v < 1 ? 0 : v / max <= 0.25 ? 1 : v / max <= 0.5 ? 2 : v / max <= 0.75 ? 3 : 4;
                var tip = $"{d:yyyy/MM/dd}（{SummaryCalculator.Weekday(d)}）\n" +
                          (!inRange ? "期間の外" : v < 1 ? "プレイなし" : SummaryCalculator.FormatDuration(v));
                var box = new Border
                {
                    Margin = new Thickness(1), CornerRadius = new CornerRadius(3), ToolTip = tip,
                    Background = inRange ? CalendarBrushes[level] : Brushes.Transparent,
                    BorderThickness = new Thickness(d == DateTime.Today ? 1.5 : 0), BorderBrush = BarBrush, // 今日は枠を付ける
                    Child = new TextBlock
                    {
                        Text = day.ToString(), FontSize = 11, Opacity = inRange ? 0.95 : 0.35,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                Grid.SetColumn(box, (offset + day - 1) % 7);
                Grid.SetRow(box, (offset + day - 1) / 7 + 1);
                grid.Children.Add(box);
            }

            // 外側に年/月
            var block = new StackPanel { Margin = new Thickness(0, 0, 18, 12) };
            block.Children.Add(new TextBlock { Text = $"{m.Year}年{m.Month}月", FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 0, 2) });
            block.Children.Add(grid);
            panel.Children.Add(block);
        }
    }

    // ================================================================
    // 作品欄のクリック → 一覧タブでその作品を開く
    // ================================================================

    private void SummaryGameItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SummaryRow row }) return;

        Game? Find() => (GameGrid.ItemsSource as IEnumerable<Game>)?.FirstOrDefault(g => g.Id == row.GameId);

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
        e.Handled = true;
    }

    private static void Add(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static string AxisText(double seconds) =>
        seconds == 0 ? "0" : seconds % 3600 == 0 ? $"{seconds / 3600:0}h" : $"{seconds / 60:0}分";
}
