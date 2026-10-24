using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ErogeLauncher.Models;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

/// <summary>
/// レポートの「年間・月間のまとめ」欄と「目標」欄。
/// どちらも、上で選んだ期間には関係なく、今年・今月（と、その前の年・月）を見る。
/// </summary>
public partial class MainWindow
{
    private SummaryExtras.Recap? _recapYear, _recapMonth, _recapPrevYear, _recapPrevMonth;
    private SummaryExtras.Streak _streakBest = new(0, null, null);
    private int _streakNow;
    private IReadOnlyList<(DateTime Month, double Seconds, int Clears)> _recapMonths = Array.Empty<(DateTime, double, int)>();

    /// <summary>まとめ・目標の元になる数字を読み直す（期間を読み直すたびに。欄の大きさが変わっただけでは読まない）</summary>
    private void LoadSummaryRecap()
    {
        var today = DateTime.Today;
        var all = _db.GetSessions(DateTime.MinValue, today.AddDays(1)).ToList();
        var games = _summaryGames;

        var first = all.Count == 0 ? today : all.Min(s => s.Start).Date;
        var daily = SummaryCalculator.Build(all, first, today).DailySeconds;
        _streakBest = SummaryExtras.LongestStreak(daily, first, today);
        _streakNow = SummaryExtras.CurrentStreak(daily, today);

        var y = new DateTime(today.Year, 1, 1);
        var m = new DateTime(today.Year, today.Month, 1);
        _recapYear = SummaryExtras.BuildRecap(all, games, y, y.AddYears(1).AddDays(-1));
        _recapPrevYear = SummaryExtras.BuildRecap(all, games, y.AddYears(-1), y.AddDays(-1));
        _recapMonth = SummaryExtras.BuildRecap(all, games, m, m.AddMonths(1).AddDays(-1));
        _recapPrevMonth = SummaryExtras.BuildRecap(all, games, m.AddMonths(-1), m.AddDays(-1));
        _recapMonths = SummaryExtras.MonthlyTotals(all, games, today, 12);
        LoadSummaryTrend(all);
    }

    // ---- 年間・月間のまとめ ----

    private void DrawSummaryRecap()
    {
        var (_, title, body) = _summaryExtra["recap"];
        body.Children.Clear();
        if (_recapYear == null || _recapMonth == null || _recapPrevYear == null || _recapPrevMonth == null) return;
        var today = DateTime.Today;
        title.Text = $"{today:yyyy年M月d日}までのまとめ（上で選んだ期間には関係なく、今年・今月を表示）";

        AddRecapBlock(body, $"今年（{today.Year}年）", _recapYear, _recapPrevYear, "前年");
        AddRecapBlock(body, $"今月（{today.Month}月）", _recapMonth, _recapPrevMonth, "前月");

        var streak = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        streak.Children.Add(RecapLine("いまの連続", _streakNow == 0 ? "なし" : $"{_streakNow}日"));
        streak.Children.Add(RecapLine("過去最長の連続",
            _streakBest.Days == 0 ? "なし" : $"{_streakBest.Days}日（{_streakBest.Start:yyyy/M/d}〜{_streakBest.End:M/d}）"));
        streak.Children.Add(new TextBlock { Text = "連続は、1日の合計が10分以上プレイした日が続いた数です", FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(streak);

        body.Children.Add(new TextBlock { Text = "月別（直近12か月）", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 4) });
        var max = _recapMonths.Count == 0 ? 0 : _recapMonths.Max(x => x.Seconds);
        foreach (var (month, seconds, clears) in _recapMonths.Reverse())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{month:yyyy/M}", Opacity = 0.8 });
            var frac = max < 1 ? 0 : seconds / max;
            var bar = new Border
            {
                Height = 10, CornerRadius = new CornerRadius(2), Background = BarBrush, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, Width = Math.Max(seconds >= 1 ? 2 : 0, 150 * frac),
                ToolTip = $"{month:yyyy年M月}　{SummaryCalculator.FormatDuration(seconds)}　クリア {clears}本",
            };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var text = new TextBlock { Text = $"{SummaryCalculator.FormatDuration(seconds)}　クリア {clears}本", Margin = new Thickness(8, 0, 0, 0), Opacity = 0.85 };
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            body.Children.Add(row);
        }
    }

    private static void AddRecapBlock(Panel body, string heading, SummaryExtras.Recap r, SummaryExtras.Recap prev, string prevName)
    {
        body.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2) });
        var delta = prev.Seconds < 1 ? $"（{prevName}は記録なし）" : $"（{prevName}比 {SummaryCalculator.FormatDelta(r.Seconds, prev.Seconds)}）";
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        block.Children.Add(RecapLine("プレイ時間", $"{SummaryCalculator.FormatDuration(r.Seconds)} {delta}"));
        block.Children.Add(RecapLine("プレイした日", $"{r.PlayedDays}日"));
        block.Children.Add(RecapLine("プレイした作品", $"{r.Games}本"));
        block.Children.Add(RecapLine("クリア", $"{r.Clears}本（{prevName}は {prev.Clears}本）"));
        block.Children.Add(RecapLine("最長の連続",
            r.Longest.Days == 0 ? "なし" : $"{r.Longest.Days}日（{r.Longest.Start:M/d}〜{r.Longest.End:M/d}）"));
        body.Children.Add(block);
    }

    private static UIElement RecapLine(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label, Opacity = 0.7 });
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    // ---- 目標 ----

    private sealed record GoalRow(TextBox Box, ProgressBar Bar, TextBlock Text);

    private readonly Dictionary<string, GoalRow> _goalRows = new();
    private TextBlock? _goalError;
    private bool _goalBuilt, _goalSetting;

    /// <summary>入力欄は一度だけ作り、あとは数字と進み具合だけを更新する（入力の途中で作り直さないため）</summary>
    private void DrawSummaryGoal()
    {
        var (_, title, body) = _summaryExtra["goal"];
        var today = DateTime.Today;
        title.Text = "プレイの目標（0 または空欄は、目標なし）";
        if (!_goalBuilt)
        {
            _goalBuilt = true;
            _goalError = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            body.Children.Add(_goalError);
            foreach (var scope in new[] { "Year", "Month" })
            {
                var head = new TextBlock { Tag = scope, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 4) };
                body.Children.Add(head);
                body.Children.Add(BuildGoalRow(scope + "Hours", "プレイ時間", "時間", SummaryExtras.MaxGoalHours));
                body.Children.Add(BuildGoalRow(scope + "Clears", "クリア", "本", SummaryExtras.MaxGoalClears));
            }
        }

        foreach (var child in body.Children.OfType<TextBlock>().Where(t => t.Tag is string))
            child.Text = (string)child.Tag == "Year" ? $"今年の目標（{today.Year}年）" : $"今月の目標（{today.Month}月）";

        _goalSetting = true;
        try
        {
            foreach (var (key, row) in _goalRows)
            {
                var goal = GoalValue(key);
                if (!row.Box.IsKeyboardFocusWithin) row.Box.Text = goal == 0 ? "" : goal.ToString();
                var year = key.StartsWith("Year");
                var recap = year ? _recapYear : _recapMonth;
                if (recap == null) continue;
                var hours = key.EndsWith("Hours");
                var actual = hours ? recap.Seconds : recap.Clears;
                var pct = SummaryExtras.GoalPercent(actual, hours ? goal * 3600.0 : goal);
                row.Bar.Value = Math.Min(100, pct ?? 0);
                row.Bar.Visibility = pct == null ? Visibility.Collapsed : Visibility.Visible;
                row.Text.Text = pct == null
                    ? (hours ? $"いま {SummaryCalculator.FormatDuration(actual)}" : $"いま {recap.Clears}本")
                    : hours
                        ? $"{SummaryCalculator.FormatDuration(actual)} / {goal}時間（{pct}%）"
                        : $"{recap.Clears}本 / {goal}本（{pct}%）";
            }
        }
        finally
        {
            _goalSetting = false;
        }
    }

    private UIElement BuildGoalRow(string key, string label, string unit, int max)
    {
        var box = new TextBox { Width = 64, Tag = key, MaxLength = 4, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = $"0〜{max} の整数" };
        box.LostKeyboardFocus += (_, _) => CommitGoal(box, max);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitGoal(box, max); e.Handled = true; }
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Width = 150, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = 76, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = unit, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(bar);
        row.Children.Add(text);
        _goalRows[key] = new GoalRow(box, bar, text);
        return row;
    }

    private int GoalValue(string key) => key switch
    {
        "YearHours" => _cfg.GoalYearHours,
        "YearClears" => _cfg.GoalYearClears,
        "MonthHours" => _cfg.GoalMonthHours,
        _ => _cfg.GoalMonthClears,
    };

    private void CommitGoal(TextBox box, int max)
    {
        if (_goalSetting || box.Tag is not string key || _goalError == null) return;
        var n = SummaryExtras.ParseGoal(box.Text, max);
        if (n == null)
        {
            _goalError.Text = $"入力エラー。0～{max} の整数で入力してください。";
            _goalError.Visibility = Visibility.Visible;
            box.Text = GoalValue(key) == 0 ? "" : GoalValue(key).ToString(); // 直前の値に戻す
            return;
        }
        _goalError.Visibility = Visibility.Collapsed;
        if (n.Value == GoalValue(key)) return;
        switch (key)
        {
            case "YearHours": _cfg.GoalYearHours = n.Value; break;
            case "YearClears": _cfg.GoalYearClears = n.Value; break;
            case "MonthHours": _cfg.GoalMonthHours = n.Value; break;
            default: _cfg.GoalMonthClears = n.Value; break;
        }
        _cfg.Save();
        DrawSummaryGoal();
    }
}
