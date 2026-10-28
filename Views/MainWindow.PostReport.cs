using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ErogeLauncher.Models;
using ErogeLauncher.Services;
using Microsoft.Win32;

namespace ErogeLauncher.Views;

/// <summary>投稿設定タブ：レポートエリア（画像になる部分）の組み立て</summary>
public partial class MainWindow
{
    // ================================================================
    // レポートエリア（この部分が画像になる）
    // ================================================================

    private static TextBlock PostText(string text, double size, bool bold = false, double opacity = 1) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Opacity = opacity, TextWrapping = TextWrapping.Wrap,
    };

    private static UIElement PostValue(string big, string? sub = null)
    {
        var p = new StackPanel();
        p.Children.Add(PostText(big, 30, bold: true));
        if (!string.IsNullOrEmpty(sub)) p.Children.Add(PostText(sub, 12, opacity: 0.7));
        return p;
    }

    private static UIElement BrandValue(PostBrand? b)
    {
        if (b == null) return PostValue("―", "該当するブランドなし");
        var detail = $"{SummaryCalculator.FormatDuration(b.Seconds)}　{b.GameCount}本" +
                     (b.AverageScore is double a ? $"　平均 {a:0.#} 点" : "");
        var p = new StackPanel();
        p.Children.Add(PostText(b.Brand, 24, bold: true));
        p.Children.Add(PostText(detail, 12, opacity: 0.7));
        return p;
    }

    private void BuildPostReport(PostReport r)
    {
        _postVersion++;
        _postImageTasks.Clear();
        _postEditButtons.Clear();
        ApplyPostPalette();

        var root = new StackPanel();
        var layout = Layout;
        var grid = new Grid { Margin = new Thickness(-4, -4, -4, -4) }; // パネルの余白（4px）を、外側の余白と相殺する
        for (var c = 0; c < layout.Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        // 1段の高さは決まっている（パネルは、段の整数倍の大きさになる）
        var rows = layout.Panels.Select(p => p.Row + p.RowSpan).DefaultIfEmpty(0).Max();
        for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PostRowUnit) });

        foreach (var panel in layout.Panels)
        {
            var card = BuildPostPanel(panel, r);
            Grid.SetRow(card, panel.Row);
            Grid.SetRowSpan(card, panel.RowSpan);
            Grid.SetColumn(card, panel.Col);
            Grid.SetColumnSpan(card, panel.Span);
            grid.Children.Add(card);
        }
        // ドラッグ中に、置き先を示す枠（画像には入れない：編集用のボタンと同じ扱い）
        _postDropMarker = new Border
        {
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Margin = new Thickness(4),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_postDropMarker, 100);
        grid.Children.Add(_postDropMarker);
        _postGrid = grid;
        root.Children.Add(grid);
        PostReportHost.Child = root;
    }

    private const double PostRowUnit = PostLayout.RowUnitPx; // 1段の高さ（パネルの外側の余白 4px ずつを含む）
    private bool _postMeasuring;            // 中身の大きさを測っている間（グラフの高さを標準にする）

    /// <summary>#RRGGBB の色を読む（空・読めない値は null）</summary>
    private static Color? ParsePostColorValue(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch { return null; }
    }

    private static Brush? ParsePostColor(string? hex) => ParsePostColorValue(hex) is { } c ? PostBrush(c) : null;

    /// <summary>パネルの文字の設定（フォント・色）を当てた文字。大きさは、呼び出し側が決める</summary>
    private static TextBlock PostStyledText(string text, PostPanel p, double size, bool bold, double opacity = 1)
    {
        var t = PostText(text, size, bold, opacity);
        if (!string.IsNullOrWhiteSpace(p.FontFamily)) t.FontFamily = new FontFamily(p.FontFamily);
        if (ParsePostColor(p.FontColor) is { } brush) t.Foreground = brush;
        return t;
    }

    /// <summary>パネル1枚（見出し・中身・右上の ⚙ ×）。タイトルと見出しのパネルは、枠なしの文字だけにする</summary>
    private Border BuildPostPanel(PostPanel panel, PostReport r, bool measure = false)
    {
        var frameless = panel.Kind == PostPanelKind.Heading; // 見出しだけ、枠なし（タイトルも、ほかのパネルと同じ枠を付ける）
        var body = new StackPanel();
        if (panel.Kind == PostPanelKind.Title)
        {
            var size = panel.FontSize > 0 ? panel.FontSize : 22;
            var t = PostStyledText(panel.DisplayTitle, panel, size, bold: true);
            t.Margin = new Thickness(0, 0, 56, 0);
            body.Children.Add(t);
            if (panel.ShowPeriod)
                body.Children.Add(PostStyledText($"{r.From:yyyy/MM/dd} 〜 {r.To:yyyy/MM/dd}", panel, Math.Max(11, Math.Round(size * 0.6)), bold: false, opacity: 0.7));
        }
        else
        {
            var title = panel.Kind == PostPanelKind.Heading
                ? PostStyledText(panel.DisplayTitle, panel, panel.FontSize > 0 ? panel.FontSize : 20, bold: true)
                : PostText(panel.DisplayTitle, _cfg.PostLabelSize > 0 ? _cfg.PostLabelSize : PostLabelSizeDefault, opacity: 0.7);
            title.Margin = new Thickness(0, 0, 56, 0); // 右上のボタンと重ならないように
            body.Children.Add(title);
            if (!frameless)
            {
                var content = BuildPostContent(panel, r);
                content.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 0));
                if (PostLayout.UsesAlign(panel.Kind) && panel.Align is 1 or 2 && content is FrameworkElement fe
                    && !(PostLayout.UsesItemColumns(panel.Kind) && panel.ItemColumns > 0)) // 列数を決めた並びは、パネルの幅をその列数で等分する（寄せると、作品名の長さで幅が変わる）
                {
                    // 余白ができるとき、中身を中央・右に寄せる（複数行の文字も、同じ側に揃える）
                    fe.HorizontalAlignment = panel.Align == 1 ? HorizontalAlignment.Center : HorizontalAlignment.Right;
                    if (fe is TextBlock tb) tb.TextAlignment = panel.Align == 1 ? TextAlignment.Center : TextAlignment.Right;
                }
                body.Children.Add(content);
            }
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -8, -8, 0),
        };
        if (!measure && !_postMeasuring && _post != null && panel.Kind != PostPanelKind.DailyChart) // グラフは縦幅に合わせて高さが変わるので、対象外
        {
            // 中身が縦幅に収まらず、切れているとき：クリックで必要な段数まで広げる（下のパネルはずれる）
            var need = MeasurePostRowSpan(panel);
            if (need > panel.RowSpan)
            {
                var warn = PostPanelButton("⚠", $"中身が切れています。クリックで縦幅を {need} 段に広げます（下のパネルはずれます）", (_, _) =>
                {
                    var error = Layout.TryResize(panel, need);
                    PostStatus.Text = error ?? $"「{panel.DisplayTitle}」の縦幅を {need} 段に広げました";
                    if (error == null) Dispatcher.BeginInvoke(new Action(SaveLayoutAndRebuild));
                });
                warn.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
                buttons.Children.Add(warn);
            }
        }
        buttons.Children.Add(PostPanelButton("⚙", "このパネルの設定", (_, _) => OpenPostSettings(panel)));
        buttons.Children.Add(PostPanelButton("×", "このパネルを削除", (_, _) => RemovePostPanel(panel)));
        _postEditButtons.Add(buttons);

        var inner = new Grid();
        inner.Children.Add(body);
        inner.Children.Add(buttons);
        var card = new Border
        {
            Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, Child = inner,
            Cursor = System.Windows.Input.Cursors.SizeAll, ToolTip = "ドラッグして移動",
            Background = Brushes.Transparent, // 余白でもドラッグを始められるように
        };
        if (frameless)
        {
            card.Padding = new Thickness(4, 6, 4, 4);
            if (panel.Kind == PostPanelKind.Heading)
            {
                card.BorderThickness = new Thickness(0, 0, 0, 2); // 見出しは、下に線を引く
                card.BorderBrush = _postCardBorder;
            }
        }
        else
        {
            card.Padding = new Thickness(14);
            card.BorderThickness = new Thickness(1);
            // 透過率は、パネル自身の設定があればそれ、なければデザイン設定の全体の値
            var alpha = panel.Transparency is int own ? 1 - own / 100.0 : _postCardAlpha;
            card.BorderBrush = PostBrush(_postBorderColor, alpha);
            card.Background = PostBrush(_postCardColor, alpha);
            card.CornerRadius = new CornerRadius(8);
        }
        if (!measure) card.Height = panel.RowSpan * PostRowUnit - 8; // 縦幅は、段数で決まる（中身が多いと、はみ出た分は切れる）
        if (panel.Kind == PostPanelKind.Title && panel.FullBleed)
        {
            // 画像の左右の端まで広げる（外側の余白 16px を打ち消す）。1段目なら、上の端まで。左右の枠線と角の丸みはなくす
            var top = panel.Row == 0;
            card.Margin = new Thickness(-16, top ? -16 : 4, -16, 4);
            if (!measure) card.Height += top ? 12 : 0; // 上に伸ばした分、下の位置は変えない
            card.CornerRadius = new CornerRadius(0);
            card.BorderThickness = new Thickness(0, top ? 0 : 1, 0, 1);
            card.Padding = new Thickness(34, 14 + (top ? 12 : 0), 34, 14);
        }
        HookPostDrag(card, panel);
        return card;
    }

    /// <summary>中身に必要な段数を、実際に並べて測る（レポートの幅と、パネルの横幅から求める）</summary>
    private int MeasurePostRowSpan(PostPanel panel)
    {
        if (_post is not { } report) return PostLayout.EstimateRowSpan(panel.Kind, panel.ItemCount, panel.ItemColumns);
        double width = PostMaxImageWidth;
        var cell = (width - 40 + 8) / Layout.Columns * panel.Span; // 余白 20px ずつ、外側の余白の相殺 4px ずつ
        var buttons = _postEditButtons.Count;
        var tasks = _postImageTasks.Count;
        _postMeasuring = true;
        try
        {
            var card = BuildPostPanel(panel, report, measure: true);
            card.Measure(new Size(cell, double.PositiveInfinity));
            return Math.Clamp((int)Math.Ceiling(card.DesiredSize.Height / PostRowUnit), 1, PostLayout.MaxRowSpan);
        }
        finally
        {
            _postMeasuring = false;
            _postEditButtons.RemoveRange(buttons, _postEditButtons.Count - buttons); // 測るために作った分は、残さない
            if (_postImageTasks.Count > tasks) _postImageTasks.RemoveRange(tasks, _postImageTasks.Count - tasks);
        }
    }

    private static ControlTemplate? _postButtonTemplate;

    /// <summary>⚙ × のボタン。アプリのテーマの見た目を使わず、画像の配色に合わせる（明るい背景でも見える）</summary>
    private Button PostPanelButton(string text, string tip, RoutedEventHandler click)
    {
        _postButtonTemplate ??= (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='5'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.65'/></Trigger></ControlTemplate.Triggers>" +
            "</ControlTemplate>");
        var b = new Button
        {
            Content = text, ToolTip = tip, Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0),
            FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand, Template = _postButtonTemplate,
            Foreground = _postTextBrush, Background = _postButtonFill, BorderBrush = _postCardBorder,
        };
        b.Click += click;
        return b;
    }

    private UIElement BuildPostContent(PostPanel panel, PostReport r)
    {
        switch (panel.Kind)
        {
            case PostPanelKind.TotalTime:
                return PostValue(SummaryCalculator.FormatDuration(r.TotalSeconds));
            case PostPanelKind.ClearCount:
                return PostValue($"{r.ClearCount} 本");
            case PostPanelKind.Score:
                return r.AverageScore is double s
                    ? PostValue($"{s:0.#} 点", "クリアした作品の平均")
                    : PostValue("―", "期間内にクリアして得点を付けた作品なし");
            case PostPanelKind.Backlog:
                return PostValue($"{r.BacklogCount} 本",
                    BacklogHistory.Describe(BacklogHistory.Change(_cfg.BacklogSnapshots, r.From, r.To, r.BacklogCount, DateTime.Today)));
            case PostPanelKind.BestBrand:
                return BrandValue(r.Brand);
            case PostPanelKind.DaysPlayed:
                return PostValue($"{r.PlayedDays} 日", $"期間 {r.DayCount} 日のうち、遊んだ日");
            case PostPanelKind.AvgPerDay:
                return PostValue(SummaryCalculator.FormatDuration(r.DayCount == 0 ? 0 : r.TotalSeconds / r.DayCount), $"期間 {r.DayCount} 日の平均");
            case PostPanelKind.WorksCount:
                return PostValue($"{r.PlayedGameCount} 本", "期間内に遊んだ作品");
            case PostPanelKind.LongestSession:
                return r.LongestSession is { } ls
                    ? PostValue(SummaryCalculator.FormatDuration(ls.Seconds), $"{ls.Title}（{ls.Start:M/d}）")
                    : PostValue("―", "プレイの記録なし");
            case PostPanelKind.BestDay:
                return r.BestDay is { } bd
                    ? PostValue(SummaryCalculator.FormatDuration(bd.Seconds), $"{bd.Day:M/d}（{SummaryCalculator.Weekday(bd.Day)}）")
                    : PostValue("―", "プレイの記録なし");
            case PostPanelKind.PeakHour:
                return r.PeakHour is int h ? PostValue($"{h} 時台", "いちばんよく遊ぶ時間帯") : PostValue("―", "プレイの記録なし");
            case PostPanelKind.LongestStreak:
            {
                var st = SummaryExtras.LongestStreak(r.DailySeconds, r.From, r.To);
                return st.Days == 0
                    ? PostValue("―", "1日10分以上遊んだ日の連続なし")
                    : PostValue($"{st.Days} 日", $"{st.Start:M/d}〜{st.End:M/d}（1日10分以上）");
            }
            case PostPanelKind.GoalProgress:
            {
                // 期間が1年ほどなら年間の目標、それ以外は月間の目標と比べる
                var year = r.DayCount >= 360;
                var hoursGoal = year ? _cfg.GoalYearHours : _cfg.GoalMonthHours;
                var clearGoal = year ? _cfg.GoalYearClears : _cfg.GoalMonthClears;
                var name = year ? "年間" : "月間";
                if (hoursGoal == 0 && clearGoal == 0)
                    return PostValue("―", $"レポートの「目標」で、{name}の目標を決めてください");
                var lines = new List<string>();
                if (hoursGoal > 0) lines.Add($"プレイ時間 {SummaryCalculator.FormatDuration(r.TotalSeconds)} / {hoursGoal}時間");
                if (clearGoal > 0) lines.Add($"クリア {r.ClearCount}本 / {clearGoal}本");
                var main = hoursGoal > 0
                    ? $"{SummaryExtras.GoalPercent(r.TotalSeconds, hoursGoal * 3600.0)}%"
                    : $"{SummaryExtras.GoalPercent(r.ClearCount, clearGoal)}%";
                return PostValue(main, $"{name}の目標　" + string.Join("　", lines));
            }
            case PostPanelKind.PrevComparison:
                return r.PreviousSeconds is double prev && prev >= 1
                    ? PostValue(SummaryCalculator.FormatDelta(r.TotalSeconds, prev), $"前の {r.DayCount} 日間は {SummaryCalculator.FormatDuration(prev)}")
                    : PostValue("―", $"前の {r.DayCount} 日間は記録なし");
            case PostPanelKind.ScoreVsMedian:
                if (r.MedianCompare is not { } mc) return PostValue("―", "点数と批評空間の中央値がそろった、クリア作品なし");
                var diff = mc.Mine - mc.Median;
                return PostValue(Math.Abs(diff) < 0.05 ? "±0 点" : $"{(diff > 0 ? "+" : "-")}{Math.Abs(diff):0.#} 点",
                    $"自分の平均 {mc.Mine:0.#} 点　／　批評空間の中央値の平均 {mc.Median:0.#} 点");
            case PostPanelKind.ClearedWorks:
                if (r.Cleared.Count == 0) return PostText("期間内にクリアした作品はありません", 13, opacity: 0.7);
                return BuildPostTiles(r.Cleared.Select(c => (c.Title, c.EgsId, (string?)(c.Score is int sc ? $"{sc} 点" : null))).ToList(), panel);
            case PostPanelKind.TopScoreWorks:
            {
                var top = r.Cleared.Where(c => c.Score != null).OrderByDescending(c => c.Score).ThenBy(c => c.Title, StringComparer.CurrentCulture).ToList();
                if (top.Count == 0) return PostText("期間内にクリアして得点を付けた作品はありません", 13, opacity: 0.7);
                return BuildPostTiles(top.Select(c => (c.Title, c.EgsId, (string?)$"{c.Score} 点")).ToList(), panel);
            }
            case PostPanelKind.BrandRanking:
                return BuildPostBrandRanking(r, panel);
            case PostPanelKind.DailyChart:
                return BuildPostChart(r, panel);
            case PostPanelKind.ScoreDistribution:
                return BuildPostScoreBars(r);
            case PostPanelKind.Calendar:
                return BuildPostCalendar(r, panel);
            case PostPanelKind.Memo:
                return string.IsNullOrWhiteSpace(panel.Text)
                    ? PostText("⚙ から、文章を入力してください", 13, opacity: 0.7)
                    : PostStyledText(panel.Text, panel, panel.FontSize > 0 ? panel.FontSize : 14, bold: false);
            case PostPanelKind.Priority:
            {
                var byId = _postGames.ToDictionary(g => g.Id);
                var picked = panel.GameIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                if (picked.Count == 0) return PostText("⚙ から、優先したい作品を選んでください", 13, opacity: 0.7);
                return BuildPostTiles(picked.Select(g => (g.Title, g.EgsId, (string?)null)).ToList(), panel);
            }
            default:
                if (r.Works.Count == 0) return PostText("この期間のプレイ記録はありません", 13, opacity: 0.7);
                return BuildPostTiles(r.Works.Select(w => (w.Title, w.EgsId, (string?)SummaryCalculator.FormatDuration(w.Seconds))).ToList(), panel);
        }
    }

    /// <summary>ブランド別ランキング：プレイ時間の長い順に、1行ずつ</summary>
    private UIElement BuildPostBrandRanking(PostReport r, PostPanel panel)
    {
        var p = new StackPanel();
        if (r.Brands.Count == 0) return PostText("ブランドの分かる作品のプレイ記録がありません", 13, opacity: 0.7);
        var i = 0;
        Panel rows = panel.ViewColumns > 1 ? new System.Windows.Controls.Primitives.UniformGrid { Columns = panel.ViewColumns } : p;
        if (!ReferenceEquals(rows, p)) p.Children.Add(rows);
        foreach (var b in r.Brands.Take(panel.ItemCount))
        {
            i++;
            var row = new Grid { Margin = new Thickness(0, 2, panel.ViewColumns > 1 ? 14 : 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = PostText($"{i}. {b.Brand}", 14, bold: i == 1);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            var detail = PostText($"{SummaryCalculator.FormatDuration(b.Seconds)}　{b.GameCount}本", 12, opacity: 0.7);
            detail.Margin = new Thickness(10, 2, 0, 0);
            Grid.SetColumn(detail, 1);
            row.Children.Add(name);
            row.Children.Add(detail);
            rows.Children.Add(row);
        }
        if (r.Brands.Count > panel.ItemCount) p.Children.Add(PostText($"ほか {r.Brands.Count - panel.ItemCount} ブランド", 12, opacity: 0.7));
        return p;
    }

    /// <summary>プレイ時間の棒グラフ（日別。長い期間は週別・月別。縦幅を指定すると、それに合わせて高さが変わる）</summary>
    private UIElement BuildPostChart(PostReport r, PostPanel panel)
    {
        var buckets = r.Buckets;
        var max = buckets.Count == 0 ? 0 : buckets.Max(b => b.Seconds);
        if (max < 1) return PostText("この期間のプレイ記録はありません", 13, opacity: 0.7);

        var height = _postMeasuring ? 160 : Math.Max(50, panel.RowSpan * PostRowUnit - 8 - 100); // 縦幅に合わせて高さが変わる
        var accent = PostBrush((PostAccent.R << 16) | (PostAccent.G << 8) | PostAccent.B);
        var bars = new Grid { Height = height };
        var labels = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        var step = (int)Math.Ceiling(buckets.Count / 8.0); // ラベルは8個ほどまで
        for (var i = 0; i < buckets.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            labels.ColumnDefinitions.Add(new ColumnDefinition());
            var s = buckets[i].Seconds;
            var bar = new Border
            {
                Background = accent, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0),
                Height = s <= 0 ? 0 : Math.Max(2, s / max * height), CornerRadius = new CornerRadius(2, 2, 0, 0),
            };
            Grid.SetColumn(bar, i);
            bars.Children.Add(bar);
            if (i % step == 0)
            {
                var t = PostText(buckets[i].Label, 10, opacity: 0.7);
                t.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(t, i);
                Grid.SetColumnSpan(t, Math.Min(step, buckets.Count - i));
                labels.Children.Add(t);
            }
        }
        var p = new StackPanel();
        p.Children.Add(PostText($"最大 {SummaryCalculator.FormatDuration(max)}", 11, opacity: 0.7));
        p.Children.Add(bars);
        p.Children.Add(labels);
        return p;
    }

    /// <summary>点数の分布（59以下 / 60代 / 70代 / 80代 / 90以上）を横棒で</summary>
    private UIElement BuildPostScoreBars(PostReport r)
    {
        var total = r.ScoreBins.Sum();
        if (total == 0) return PostText("期間内にクリアして得点を付けた作品はありません", 13, opacity: 0.7);
        var accent = PostBrush((PostAccent.R << 16) | (PostAccent.G << 8) | PostAccent.B);
        string[] names = { "59以下", "60代", "70代", "80代", "90以上" };
        var max = r.ScoreBins.Max();
        var p = new StackPanel();
        for (var i = 0; i < 5; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.Children.Add(PostText(names[i], 12, opacity: 0.8));

            var ratio = (double)r.ScoreBins[i] / max;
            var track = new Grid { Height = 14 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(ratio, 0) * 100, GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((1 - ratio) * 100, GridUnitType.Star) });
            track.Children.Add(new Border { Background = accent, CornerRadius = new CornerRadius(3) });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);

            var count = PostText($"{r.ScoreBins[i]} 本", 12);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            p.Children.Add(row);
        }
        return p;
    }

    /// <summary>
    /// カレンダー（レポートのタブと同じ仕様）：期間にかかる月ごとに、日〜土を横に並べた一般的なカレンダー。
    /// 遊んだ日を、時間が長いほど濃い色にし、今日は枠を付ける。長い期間は、直近12か月だけ表示する
    /// </summary>
    private UIElement BuildPostCalendar(PostReport r, PostPanel panel)
    {
        if (r.DayCount < 2) return PostText("期間が2日以上のときに表示します", 13, opacity: 0.7);

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

        var max = 0d;
        for (var d = from; d <= r.To; d = d.AddDays(1)) max = Math.Max(max, r.DailySeconds.GetValueOrDefault(d));

        const double cellW = 32, cellH = 26;
        Panel wrap = panel.ViewColumns > 0
            ? new System.Windows.Controls.Primitives.UniformGrid { Columns = panel.ViewColumns }
            : new WrapPanel();
        for (var m = firstMonth; m <= lastMonth; m = m.AddMonths(1))
        {
            var grid = new Grid();
            for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellW) });
            for (var i = 0; i < 7; i++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellH) });

            for (var i = 0; i < 7; i++)
            {
                var head = PostText("日月火水木金土"[i].ToString(), 11, opacity: 0.7);
                head.HorizontalAlignment = HorizontalAlignment.Center;
                head.VerticalAlignment = VerticalAlignment.Center;
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
                var number = PostText(day.ToString(), 11, opacity: inRange ? 0.95 : 0.35);
                number.HorizontalAlignment = HorizontalAlignment.Center;
                number.VerticalAlignment = VerticalAlignment.Center;
                var box = new Border
                {
                    Margin = new Thickness(1), CornerRadius = new CornerRadius(3),
                    Background = inRange ? CalendarBrushes[level] : Brushes.Transparent,
                    BorderThickness = new Thickness(d == DateTime.Today ? 1.5 : 0), BorderBrush = BarBrush, // 今日は枠を付ける
                    Child = number,
                };
                Grid.SetColumn(box, (offset + day - 1) % 7);
                Grid.SetRow(box, (offset + day - 1) / 7 + 1);
                grid.Children.Add(box);
            }

            var block = new StackPanel { Margin = new Thickness(0, 0, 18, 12) };
            var title = PostText($"{m.Year}年{m.Month}月", 14, bold: true);
            title.Margin = new Thickness(2, 0, 0, 2);
            block.Children.Add(title);
            block.Children.Add(grid);
            wrap.Children.Add(block);
        }

        var p = new StackPanel();
        p.Children.Add(PostText("遊んだ日（濃いほど長い）" + (cut ? "　※直近12か月を表示" : ""), 11, opacity: 0.7));
        wrap.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(wrap);
        return p;
    }

    /// <summary>作品を、画像・作品名・（あれば）補足の組で並べる。件数と列数はパネルの設定に従う</summary>
    private UIElement BuildPostTiles(List<(string Title, int? EgsId, string? Sub)> items, PostPanel panel)
    {
        var p = new StackPanel();
        // 列数が「自動」なら、一定の幅の作品を、パネルの幅に入るだけ並べる（余った余白は、表示位置に従って寄せる）
        Panel grid = panel.ItemColumns > 0
            ? new System.Windows.Controls.Primitives.UniformGrid { Columns = panel.ItemColumns }
            : new WrapPanel();
        foreach (var (title, egsId, sub) in items.Take(panel.ItemCount))
        {
            var item = new StackPanel { Margin = new Thickness(4) };
            if (panel.ItemColumns <= 0) item.Width = 150;
            if (_cfg.ShowImages || (egsId is int customId && GameImageService.Custom(customId) != null))
            {
                // 画像は同じ大きさの枠の中に、縦横比を保って中央に収める（画像の形がそろっていなくても、並びが揃う）
                var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2) };
                item.Children.Add(new Border { Height = 154, Background = _postImageFill, CornerRadius = new CornerRadius(4), Child = img });
                if (egsId is int id) LoadPostImage(img, id);
            }
            var t = PostText(title, 12, bold: true);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxHeight = 34; // 2行まで
            t.Margin = new Thickness(0, 4, 0, 0);
            item.Children.Add(t);
            if (!string.IsNullOrEmpty(sub)) item.Children.Add(PostText(sub, 12, opacity: 0.8));
            grid.Children.Add(item);
        }
        p.Children.Add(grid);
        if (items.Count > panel.ItemCount)
            p.Children.Add(PostText($"ほか {items.Count - panel.ItemCount} 本", 12, opacity: 0.7));
        return p;
    }

    /// <summary>作品の画像を入れる。保存済みならすぐ、なければ取得してから入れる（取得中に作り直されたら捨てる）</summary>
    private void LoadPostImage(Image img, int egsId)
    {
        var path = GameImageService.Cached(egsId);
        if (path != null) { SetPostImage(img, path, egsId); return; }
        if (!_cfg.ShowImages) return; // オフのときは、通信せず、差し替えた画像だけを使う
        var version = _postVersion;
        _postImageTasks.Add(Task.Run(async () =>
        {
            string? got;
            try { got = await Images.GetAsync(egsId, CancellationToken.None); }
            catch { got = null; }
            if (got == null) return;
            await Dispatcher.InvokeAsync(() => { if (version == _postVersion) SetPostImage(img, got, egsId); });
        }));
    }

    private static void SetPostImage(Image img, string path, int egsId)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // ファイルを開いたままにしない
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelHeight = 300;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Warn("投稿設定：画像を表示できませんでした", ex);
            GameImageService.Forget(egsId); // 壊れた画像は消して、次回取り直す
        }
    }
}
