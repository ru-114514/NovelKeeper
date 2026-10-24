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

/// <summary>「今後優先したい作品」の選択欄の1行</summary>
internal sealed class PostPick
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>投稿設定タブ：起点の日から1か月（または1年）のプレイ記録を、パネルを並べた1枚の画像にまとめる</summary>
public partial class MainWindow
{
    private const double PostMinWidth = 900;    // レポートエリアの幅の下限（画像が小さくなりすぎないように）
    private const int PostMaxImageWidth = 1920; // 出力する画像の幅の上限（ピクセル）

    // 画像の配色（アプリのテーマとは別。不透明な色だけを使う：jpg にしたときに色が変わらないように）
    private static SolidColorBrush PostBrush(int rgb) => (SolidColorBrush)Freeze(new SolidColorBrush(
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));

    private static SolidColorBrush PostBrush(Color c, double alpha = 1) => (SolidColorBrush)Freeze(new SolidColorBrush(
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B)));

    private static Color PostRgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private Brush _postCardBorder = Brushes.Gray, _postCardFill = Brushes.White, _postImageFill = Brushes.LightGray;
    private Brush _postTextBrush = Brushes.Black, _postButtonFill = Brushes.White;
    private Color _postCardColor = Colors.White, _postBorderColor = Colors.Gray;
    private double _postCardAlpha = 1; // 全体の透過率から決めた、パネルの不透明度
    private Color _postBackColor = Colors.White; // 画像の下地（画像に透明な部分があっても、jpg で黒くならないように）
    private BitmapSource? _postBackBitmap;
    private string? _postBackBitmapPath;
    private static readonly Color PostAccent = Color.FromRgb(0x4C, 0x8D, 0xFF);

    private bool _postLoading; // 設定欄を書き換えている間は、変更イベントで読み直さない
    private PostReport? _post;
    private int _postVersion;  // 作り直すたびに増やす（古い画像の取得結果を捨てるため）
    private readonly List<Task> _postImageTasks = new();
    private List<string> _postBrands = new();
    private List<Game> _postGames = new();
    private readonly List<FrameworkElement> _postEditButtons = new(); // 各パネルの ⚙ ×（画像には入れない）
    private PostPanel? _postEditing;                                  // 設定を開いているパネル
    private List<PostPick> _postPicks = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PostPick> _postChosen = new();

    private PostLayout Layout => _cfg.PostLayout;

    /// <summary>
    /// 配色をレポートエリアに当てる。土台は「背景: 明るい / 暗い」で、デザイン設定の色・画像・透過率・フォントがあれば、それで上書きする
    /// </summary>
    private void ApplyPostPalette()
    {
        var dark = PostBackgroundBox.SelectedIndex == 1;
        var host = PostReportHost;

        _postBackColor = ParsePostColorValue(_cfg.PostBackColor) ?? PostRgb(dark ? 0x1E1F24 : 0xF3F4F6);
        if (LoadPostBackImage(_cfg.PostBackImage) is { } image)
        {
            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top };
            brush.Freeze();
            host.Background = brush;
        }
        else host.Background = PostBrush(_postBackColor);

        _postTextBrush = PostBrush(ParsePostColorValue(_cfg.PostTextColor) ?? PostRgb(dark ? 0xF1F2F4 : 0x202124));
        System.Windows.Documents.TextElement.SetForeground(host, _postTextBrush);
        if (string.IsNullOrWhiteSpace(_cfg.PostFontFamily)) host.ClearValue(System.Windows.Documents.TextElement.FontFamilyProperty);
        else System.Windows.Documents.TextElement.SetFontFamily(host, new FontFamily(_cfg.PostFontFamily));

        // パネル：色と透過率（透かすのは、塗りと枠だけ。文字は透かさない）
        _postCardAlpha = 1 - Math.Clamp(_cfg.PostPanelTransparency, 0, 100) / 100.0;
        _postCardColor = ParsePostColorValue(_cfg.PostCardColor) ?? PostRgb(dark ? 0x2B2D34 : 0xFFFFFF);
        _postBorderColor = PostRgb(dark ? 0x40434C : 0xD5D9DF);
        _postCardFill = PostBrush(_postCardColor, _postCardAlpha);
        _postCardBorder = PostBrush(_postBorderColor, _postCardAlpha);
        _postImageFill = PostBrush(PostRgb(dark ? 0x23252B : 0xE9ECF0), _postCardAlpha);
        _postButtonFill = PostBrush(PostRgb(dark ? 0x3A3D46 : 0xE9ECF0)); // ボタンは、背景に埋もれない色にする（アプリのテーマに左右されない）
    }

    /// <summary>背景の画像を読む（同じファイルは、読み直さない。読めなければ画像なし）</summary>
    private BitmapSource? LoadPostBackImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        if (_postBackBitmap != null && _postBackBitmapPath == path) return _postBackBitmap;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // ファイルを開いたままにしない
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = PostMaxImageWidth; // 出力の最大幅より大きくは読まない
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            _postBackBitmap = bmp;
            _postBackBitmapPath = path;
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn("投稿設定：背景の画像を読めませんでした", ex);
            return null;
        }
    }

    private void InitPostTab()
    {
        _cfg.PostLayout ??= PostLayout.CreateDefault();
        var migrate = _cfg.PostLayout.Version < PostLayout.CurrentVersion;
        _cfg.PostLayout.MigrateToCurrent(_cfg.PostTitle); // 古い設定：上部のタイトル欄を、1段目のタイトルパネルに引き継ぐ
        _cfg.PostLayout.Normalize();
        if (migrate) _cfg.Save();

        _postLoading = true;
        PostBackgroundBox.SelectedIndex = _cfg.PostDarkBackground ? 1 : 0;
        PostStartPicker.SelectedDate = DateTime.Today.AddMonths(-1).AddDays(1);
        PostYearRadio.IsChecked = _cfg.PostOneYear;
        PostMonthRadio.IsChecked = !_cfg.PostOneYear;
        PostBrandModeBox.SelectedIndex = Math.Clamp(_cfg.PostBrandMode, 0, 2);
        PostExcludeClearedCheck.IsChecked = _cfg.PostExcludeClearedBefore;
        PostColumnsBox.SelectedIndex = Layout.Columns - PostLayout.MinColumns;
        _postLoading = false;

        // 文字の色の見本（押すと、色の欄に入る）
        foreach (var hex in new[] { "#202124", "#FFFFFF", "#E5534B", "#4C8DFF", "#2EA043", "#E0A030", "#9B59D0", "#E86AA6" })
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(0, 0, 4, 4), Tag = hex, ToolTip = hex,
                Background = ParsePostColor(hex), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            };
            swatch.Click += PostSetSwatch_Click;
            PostSetSwatches.Children.Add(swatch);
        }

        // パネルのドラッグ＆ドロップ（レポートエリア全体で受ける）
        PostReportHost.AllowDrop = true;
        PostReportHost.DragOver += PostHost_DragOver;
        PostReportHost.DragLeave += (_, _) => HidePostDropMarker();
        PostReportHost.Drop += PostHost_Drop;

        MainTabs.SelectionChanged += (s, e) =>
        {
            if (e.OriginalSource != MainTabs) return; // タブの中の選択変更は見ない
            if (ReferenceEquals(MainTabs.SelectedItem, PostTab)) RefreshPost();
        };
    }

    /// <summary>レポートエリアの幅は、表示欄いっぱい（ただし 900 より狭くはしない。狭いときは横にスクロール）</summary>
    private void PostScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange == 0) return;
        var width = Math.Max(PostMinWidth, PostScroll.ViewportWidth);
        if (Math.Abs(PostReportHost.Width - width) >= 0.5) PostReportHost.Width = width;
    }

    private void PostSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_postLoading || _db == null) return;
        _cfg.PostOneYear = PostYearRadio.IsChecked == true;
        _cfg.PostBrandMode = Math.Max(0, PostBrandModeBox.SelectedIndex);
        _cfg.PostExcludeClearedBefore = PostExcludeClearedCheck.IsChecked == true;
        _cfg.PostDarkBackground = PostBackgroundBox.SelectedIndex == 1;
        if (PostBrandBox.SelectedItem is string brand) _cfg.PostBrandName = brand;
        _cfg.Save();
        RefreshPost();
    }

    /// <summary>「任意のブランド」の選択欄を、いまの作品のブランドで作り直す（変わっていなければそのまま）</summary>
    private void FillPostBrands(IEnumerable<Game> games)
    {
        var brands = games.Select(g => g.Brand?.Trim()).Where(b => !string.IsNullOrEmpty(b)).Select(b => b!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(b => b, StringComparer.CurrentCulture).ToList();
        if (brands.SequenceEqual(_postBrands)) return;
        _postBrands = brands;
        _postLoading = true;
        PostBrandBox.ItemsSource = brands;
        PostBrandBox.SelectedItem = brands.FirstOrDefault(b => string.Equals(b, _cfg.PostBrandName, StringComparison.OrdinalIgnoreCase));
        _postLoading = false;
    }

    private void RefreshPost()
    {
        if (_postLoading || _db == null) return;
        var start = (PostStartPicker.SelectedDate ?? DateTime.Today).Date;
        var end = PostReportCalculator.PeriodEnd(start, PostYearRadio.IsChecked == true);
        var mode = (PostBrandMode)Math.Max(0, PostBrandModeBox.SelectedIndex);
        var games = _postGames = _db.GetAll();
        FillPostBrands(games);
        PostBrandBox.Visibility = mode == PostBrandMode.Specific ? Visibility.Visible : Visibility.Collapsed;

        var (prevFrom, prevTo) = SummaryCalculator.PreviousRange(start, end); // 前の期間との比較用
        var previous = prevFrom >= new DateTime(1900, 1, 1) ? _db.GetSessions(prevFrom, prevTo.AddDays(1)) : [];
        _post = PostReportCalculator.Build(games, _db.GetSessions(start, end.AddDays(1)), start, end,
            mode, PostBrandBox.SelectedItem as string, excludeClearedBefore: PostExcludeClearedCheck.IsChecked == true,
            previousSessions: previous, replayGameIds: _cfg.PostReplayGameIds.ToHashSet(),
            hiddenGameIds: _cfg.PostHiddenGameIds.ToHashSet(), backlogExcludedIds: _cfg.BacklogExcludedIds.ToHashSet());
        PostHideButton.Content = _cfg.PostHiddenGameIds.Count > 0 ? $"作品を隠す…（{_cfg.PostHiddenGameIds.Count}）" : "作品を隠す…";
        PostReplayButton.IsEnabled = PostExcludeClearedCheck.IsChecked == true;
        RebuildPostReport();
    }

    /// <summary>集計はそのままで、パネルの並びだけ作り直す（レイアウトを変えたとき）</summary>
    private void RebuildPostReport()
    {
        if (_post is not { } report) return;
        var hasPlay = report.TotalSeconds >= 1 || report.Works.Count > 0;
        PostExportButton.IsEnabled = hasPlay;
        PostStatus.Text = hasPlay ? "" : "この期間のプレイ記録はありません（画像出力はできません）";
        BuildPostReport(report);
    }

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
                if (PostLayout.UsesAlign(panel.Kind) && panel.Align is 1 or 2 && content is FrameworkElement fe)
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
        var width = Math.Max(PostMinWidth, PostReportHost.Width is double w && !double.IsNaN(w) ? w : PostMinWidth);
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
            if (_cfg.ShowImages)
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
        if (MessageBox.Show(this, "パネルの並びと設定を、初期配置（2カラム・タイトルと6パネル）に戻しますか？", "NovelKeeper",
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
        if (MessageBox.Show(this, $"「{panel.DisplayTitle}」を削除しますか？", "NovelKeeper",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Layout.Panels.Remove(panel);
        if (ReferenceEquals(_postEditing, panel))
        {
            _postEditing = null;
            PostSettingsPanel.Visibility = Visibility.Collapsed;
        }
        SaveLayoutAndRebuild();
    }

    // ---- 設定パネル ----

    private void OpenPostSettings(PostPanel panel)
    {
        PostDesignPanel.Visibility = Visibility.Collapsed; // 右の欄は、どちらか一方だけ
        _postEditing = panel;
        _postLoading = true;
        var layout = Layout;
        PostSetKindBox.ItemsSource = PostLayout.AllKinds.Select(PostLayout.DefaultTitle).ToList();
        PostSetKindBox.SelectedIndex = (int)panel.Kind;
        PostSetTitleBox.Text = panel.Title ?? "";
        PostSetRowBox.Text = (panel.Row + 1).ToString();
        PostSetColBox.ItemsSource = Enumerable.Range(1, layout.Columns).Select(i => $"{i} 列目").ToList();
        PostSetColBox.SelectedIndex = Math.Clamp(panel.Col, 0, layout.Columns - 1);
        PostSetSpanBox.ItemsSource = Enumerable.Range(1, layout.Columns).Select(i => $"横 {i} カラム").ToList();
        PostSetSpanBox.SelectedIndex = Math.Clamp(panel.Span - 1, 0, layout.Columns - 1);
        PostSetRowSpanBox.ItemsSource = Enumerable.Range(1, PostLayout.MaxRowSpan).Select(i => $"縦 {i} 段").ToList();
        PostSetRowSpanBox.SelectedIndex = Math.Clamp(panel.RowSpan, 1, PostLayout.MaxRowSpan) - 1;
        PostSetCountBox.Text = panel.ItemCount.ToString();
        PostSetItemColsBox.ItemsSource = new[] { "自動", "1", "2", "3", "4" };
        PostSetItemColsBox.SelectedIndex = Math.Clamp(panel.ItemColumns, 0, 4);
        PostSetViewColsBox.ItemsSource = new[] { "自動", "1", "2", "3", "4" };
        PostSetViewColsBox.SelectedIndex = Math.Clamp(panel.ViewColumns, 0, 4);
        PostSetAlignBox.ItemsSource = new[] { "左寄せ", "中央寄せ", "右寄せ" };
        PostSetAlignBox.SelectedIndex = Math.Clamp(panel.Align, 0, 2);
        PostSetTextBox.Text = panel.Text ?? "";
        PostSetFontBox.ItemsSource = PostFontNames();
        var fontIndex = string.IsNullOrWhiteSpace(panel.FontFamily) ? -1 : PostFontNames().IndexOf(panel.FontFamily);
        PostSetFontBox.SelectedIndex = Math.Max(0, fontIndex);
        PostSetColorBox.Text = panel.FontColor ?? "";
        PostSetFontSizeBox.Text = panel.FontSize > 0 ? panel.FontSize.ToString() : "";
        PostSetPeriodCheck.IsChecked = panel.ShowPeriod;
        PostSetBleedCheck.IsChecked = panel.FullBleed;
        PostSetTransFollow.IsChecked = panel.Transparency == null;
        PostSetTrans.Value = panel.Transparency ?? _cfg.PostPanelTransparency;
        PostSetTrans.IsEnabled = panel.Transparency != null;
        PostSetTransText.Text = $"{(int)PostSetTrans.Value}%";
        FillPostPicks(panel);
        ShowPostSettingsError(null);
        _postLoading = false;
        UpdatePostSettingsGroups();
        PostSettingsPanel.Visibility = Visibility.Visible;
    }

    private void PostSetClose_Click(object sender, RoutedEventArgs e)
    {
        _postEditing = null;
        PostSettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void PostSetKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_postLoading) UpdatePostSettingsGroups();
    }

    private List<string>? _postFontNames;

    /// <summary>フォントの選択肢（先頭は「標準」）。入れてある一覧を、名前順に</summary>
    private List<string> PostFontNames() => _postFontNames ??= new[] { "（標準）" }
        .Concat(Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCulture))
        .ToList();

    /// <summary>選んだ項目に合わせて、作品の表示・優先作品・文章・文字の設定を出し分ける。タイトルは、場所と横幅を決まった値にする</summary>
    private void UpdatePostSettingsGroups()
    {
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        PostSetItemsGroup.Visibility = PostLayout.UsesItems(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetItemColsLabel.Visibility = PostSetItemColsBox.Visibility = PostLayout.UsesItemColumns(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetViewColsGroup.Visibility = PostLayout.UsesViewColumns(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetAlignGroup.Visibility = PostLayout.UsesAlign(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetPriorityGroup.Visibility = kind == PostPanelKind.Priority ? Visibility.Visible : Visibility.Collapsed;
        PostSetTextGroup.Visibility = kind == PostPanelKind.Memo ? Visibility.Visible : Visibility.Collapsed;
        PostSetFontGroup.Visibility = PostLayout.UsesFont(kind) ? Visibility.Visible : Visibility.Collapsed;
        PostSetPeriodCheck.Visibility = kind == PostPanelKind.Title ? Visibility.Visible : Visibility.Collapsed;
        PostSetTransGroup.Visibility = kind == PostPanelKind.Title ? Visibility.Visible : Visibility.Collapsed;
        var full = PostLayout.IsFullWidth(kind); // タイトルは、いつも全カラムの幅で左端
        PostSetColBox.IsEnabled = PostSetSpanBox.IsEnabled = !full;
        PostSetRowBox.IsEnabled = !PostLayout.IsLocked(kind); // 移動できないパネルは、段も変えられない
        if (full)
        {
            PostSetColBox.SelectedIndex = 0;
            PostSetSpanBox.SelectedIndex = Layout.Columns - 1;
        }
    }

    private void PostSetTransFollow_Click(object sender, RoutedEventArgs e) =>
        PostSetTrans.IsEnabled = PostSetTransFollow.IsChecked != true;

    private void PostSetTrans_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PostSetTransText != null) PostSetTransText.Text = $"{(int)e.NewValue}%";
    }

    private void PostSetSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) PostSetColorBox.Text = hex;
    }

    /// <summary>設定欄の入力から、測るための仮のパネルを作る（読み取れない欄は、いまの値のまま）</summary>
    private PostPanel PanelFromSettings(PostPanel current)
    {
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        var tmp = new PostPanel
        {
            Kind = kind,
            Title = PostSetTitleBox.Text.Trim(),
            Span = PostLayout.IsFullWidth(kind) ? Layout.Columns : Math.Max(1, PostSetSpanBox.SelectedIndex + 1),
            ItemCount = int.TryParse(PostSetCountBox.Text.Trim(), out var c) ? Math.Clamp(c, 1, PostLayout.MaxItems(kind)) : current.ItemCount,
            ItemColumns = Math.Max(0, PostSetItemColsBox.SelectedIndex),
            Text = PostSetTextBox.Text,
            ViewColumns = Math.Max(0, PostSetViewColsBox.SelectedIndex),
            Align = Math.Max(0, PostSetAlignBox.SelectedIndex),
            FontFamily = PostSetFontBox.SelectedIndex > 0 ? PostSetFontBox.SelectedItem as string : null,
            FontColor = PostSetColorBox.Text.Trim(),
            FontSize = int.TryParse(PostSetFontSizeBox.Text.Trim(), out var fs) ? Math.Clamp(fs, 8, 96) : 0,
            ShowPeriod = PostSetPeriodCheck.IsChecked == true,
        };
        tmp.GameIds = _postChosen.Select(p => p.Id).Take(PostLayout.MaxPriorityGames).ToList();
        return tmp;
    }

    private void PostSetFit_Click(object sender, RoutedEventArgs e)
    {
        if (_postEditing is not { } panel) return;
        var rows = MeasurePostRowSpan(PanelFromSettings(panel));
        PostSetRowSpanBox.SelectedIndex = rows - 1;
        ShowPostSettingsError(null);
        PostStatus.Text = $"中身に合わせて、縦 {rows} 段にしました（「適用」で反映されます）";
    }

    private void ShowPostSettingsError(string? message)
    {
        PostSetError.Text = message ?? "";
        PostSetError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 「再プレイを追加…」：起点の日より前にクリア済みで、期間内に遊んだ作品を一覧にして、
    /// 再プレイとして集計に含める作品を選ぶ（選んだ作品は、作品ごとに保存される）
    /// </summary>
    private void PostReplay_Click(object sender, RoutedEventArgs e)
    {
        var start = (PostStartPicker.SelectedDate ?? DateTime.Today).Date;
        var end = PostReportCalculator.PeriodEnd(start, PostYearRadio.IsChecked == true);
        var included = _cfg.PostReplayGameIds.ToHashSet();
        var candidates = PostReportCalculator.ReplayCandidates(_db.GetAll(), _db.GetSessions(start, end.AddDays(1)), start, end, included);

        var picked = ShowPostPickDialog("再プレイとして含める作品",
            "起点の日より前にクリアした作品（終了日が空欄のプレイ済を含む）は、ふつう集計に入れません。再プレイした作品だけ、☑で集計に含めます（作品ごとに保存されます）。",
            candidates.Select(c => (c.GameId, PostPickText(c.Title, c.Seconds), c.Included)).ToList(),
            "この期間に、起点の日より前にクリアした作品のプレイ記録はありません。");
        if (picked == null) return;
        _cfg.PostReplayGameIds = _cfg.PostReplayGameIds.Where(id => !picked.All.Contains(id))
            .Concat(picked.Checked).Distinct().ToList();
        _cfg.Save();
        RefreshPost();
    }

    private static string PostPickText(string title, double seconds) =>
        seconds >= 1 ? $"{title}　（この期間 {SummaryCalculator.FormatDuration(seconds)}）" : $"{title}　（この期間のプレイ記録なし）";

    /// <summary>
    /// 「作品を隠す…」：期間内に遊んだ作品を一覧にして、☑した作品をプレイ記録の集計から外す
    /// （クリア済みでない作品でも隠せる。作品ごとに保存される）
    /// </summary>
    private void PostHide_Click(object sender, RoutedEventArgs e)
    {
        var start = (PostStartPicker.SelectedDate ?? DateTime.Today).Date;
        var end = PostReportCalculator.PeriodEnd(start, PostYearRadio.IsChecked == true);
        var candidates = PostReportCalculator.HideCandidates(_db.GetAll(), _db.GetSessions(start, end.AddDays(1)), start, end,
            _cfg.PostHiddenGameIds.ToHashSet(), PostExcludeClearedCheck.IsChecked == true, _cfg.PostReplayGameIds.ToHashSet());
        var picked = ShowPostPickDialog("集計から隠す作品",
            "☑した作品は、クリア済みでなくても、プレイ時間・日数・グラフ・作品の欄など、プレイ記録の集計から外します（作品ごとに保存されます。☑を外すと戻ります）。",
            candidates.Select(c => (c.GameId, PostPickText(c.Title, c.Seconds), c.Hidden)).ToList(),
            "この期間のプレイ記録はありません。");
        if (picked == null) return;
        _cfg.PostHiddenGameIds = _cfg.PostHiddenGameIds.Where(id => !picked.All.Contains(id))
            .Concat(picked.Checked).Distinct().ToList();
        _cfg.Save();
        RefreshPost();
    }

    private record PostPickResult(HashSet<long> All, List<long> Checked);

    /// <summary>作品のチェック一覧ダイアログ（すべて選択／すべてクリアは左下）。OK なら一覧の全 ID と、☑の ID を返す</summary>
    private PostPickResult? ShowPostPickDialog(string title, string header, IReadOnlyList<(long Id, string Text, bool Checked)> items, string emptyText)
    {
        var list = new StackPanel();
        var boxes = new List<(CheckBox Box, long Id)>();
        foreach (var c in items)
        {
            var box = new CheckBox
            {
                IsChecked = c.Checked, Margin = new Thickness(0, 0, 0, 4),
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = c.Text }, // 長い作品名は折り返す
            };
            boxes.Add((box, c.Id));
            list.Children.Add(box);
        }
        if (items.Count == 0)
            list.Children.Add(new TextBlock { Text = emptyText, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        var ok = new Button { Content = "OK", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        var selectAll = new Button { Content = "すべて選択", Padding = new Thickness(12, 3, 12, 3) };
        var clearAll = new Button { Content = "すべてクリア", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 0, 0, 0) };
        selectAll.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = true);
        clearAll.Click += (_, _) => boxes.ForEach(b => b.Box.IsChecked = false);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(ok);
        right.Children.Add(cancel);
        var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(selectAll);
        left.Children.Add(clearAll);
        var buttons = new Grid();
        buttons.Children.Add(left); // 一括選択・一括クリアは左下、OK・キャンセルは右下
        buttons.Children.Add(right);
        var head = new TextBlock
        {
            Text = header,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) });
        var win = new Window
        {
            Title = title, Width = 560, Height = 420, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return null;
        return new PostPickResult(boxes.Select(b => b.Id).ToHashSet(), boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList());
    }

    /// <summary>優先したい作品の選択欄：未プレイ・プレイ中の作品（すでに選んでいるものは、状態が変わっていても残す）</summary>
    private void FillPostPicks(PostPanel panel)
    {
        var byId = _postGames.ToDictionary(g => g.Id);
        _postChosen.Clear();
        foreach (var id in panel.GameIds.Where(byId.ContainsKey).Distinct())
            _postChosen.Add(new PostPick { Id = id, Title = byId[id].Title });
        _postPicks = _postGames
            .Where(g => g.Status is GameStatus.Unplayed or GameStatus.Playing)
            .OrderBy(g => g.Title, StringComparer.CurrentCulture)
            .Select(g => new PostPick { Id = g.Id, Title = g.Title })
            .ToList();
        PostSetSearchBox.Text = "";
        PostSetGamesList.ItemsSource = CollectionViewSource.GetDefaultView(_postPicks);
        PostSetChosenList.ItemsSource = _postChosen;
        RefreshPostCandidates();
    }

    /// <summary>候補の一覧：選んだ作品を除き、検索語で絞り込む</summary>
    private void RefreshPostCandidates()
    {
        var text = PostSetSearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_postPicks);
        view.Filter = o => o is PostPick p && _postChosen.All(c => c.Id != p.Id)
            && (text.Length == 0 || p.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        view.Refresh();
        PostSetPickLabel.Text = $"優先したい作品（未プレイ・プレイ中から。ダブルクリックか「追加」で下の一覧へ）";
        PostSetChosenLabel.Text = $"選んだ作品（{_postChosen.Count} / {PostLayout.MaxPriorityGames} 件。ドラッグか ▲▼ で並び替え、× で外す）";
    }

    private void PostSetSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_postLoading) return;
        RefreshPostCandidates();
    }

    private void PostPickAdd(PostPick pick)
    {
        if (_postChosen.Count >= PostLayout.MaxPriorityGames)
        { ShowPostSettingsError($"優先したい作品は、{PostLayout.MaxPriorityGames} 件までです。"); return; }
        _postChosen.Add(new PostPick { Id = pick.Id, Title = pick.Title });
        RefreshPostCandidates();
    }

    private void PostPickAdd_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in PostSetGamesList.SelectedItems.Cast<PostPick>().ToList()) PostPickAdd(p);
    }

    private void PostPickCandidate_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PostSetGamesList.SelectedItem is PostPick p && e.OriginalSource is DependencyObject) PostPickAdd(p);
    }

    private PostPick? PostPickOf(object sender) => (sender as FrameworkElement)?.DataContext as PostPick;

    private void PostPickRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PostPickOf(sender) is { } p) { _postChosen.Remove(p); RefreshPostCandidates(); }
    }

    private void PostPickUp_Click(object sender, RoutedEventArgs e) => PostPickShift(PostPickOf(sender), -1);
    private void PostPickDown_Click(object sender, RoutedEventArgs e) => PostPickShift(PostPickOf(sender), 1);

    private void PostPickShift(PostPick? p, int delta)
    {
        if (p == null) return;
        var i = _postChosen.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _postChosen.Count) return;
        _postChosen.Move(i, j);
    }

    private PostPick? _postPickDrag;
    private Point _postPickDragStart;

    private void PostChosen_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _postPickDrag = null;
        if (IsOnButton(e.OriginalSource, PostSetChosenList)) return;
        var item = ItemsControl.ContainerFromElement(PostSetChosenList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _postPickDrag = item?.DataContext as PostPick;
        _postPickDragStart = e.GetPosition(null);
    }

    private void PostChosen_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_postPickDrag == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _postPickDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var p = _postPickDrag;
        _postPickDrag = null;
        DragDrop.DoDragDrop(PostSetChosenList, new DataObject("NovelKeeper.PostPick", p), DragDropEffects.Move);
    }

    private void PostChosen_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("NovelKeeper.PostPick") is not PostPick p) return;
        var from = _postChosen.IndexOf(p);
        if (from < 0) return;
        var target = ItemsControl.ContainerFromElement(PostSetChosenList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        var to = target?.DataContext is PostPick t ? _postChosen.IndexOf(t) : _postChosen.Count - 1;
        if (to >= 0 && to != from) _postChosen.Move(from, to);
        e.Handled = true;
    }

    private void PostSetApply_Click(object sender, RoutedEventArgs e)
    {
        if (_postEditing is not { } panel) return;
        var layout = Layout;
        var kind = (PostPanelKind)Math.Max(0, PostSetKindBox.SelectedIndex);
        var kindChanged = kind != panel.Kind;

        if (!int.TryParse(PostSetRowBox.Text.Trim(), out var row1) || row1 < 1 || row1 > PostLayout.MaxRow + 1)
        { ShowPostSettingsError($"段は 1〜{PostLayout.MaxRow + 1} の数字で入れてください。"); return; }

        var fontSize = 0;
        if (PostLayout.UsesFont(kind) && PostSetFontSizeBox.Text.Trim().Length > 0 &&
            (!int.TryParse(PostSetFontSizeBox.Text.Trim(), out fontSize) || fontSize < 8 || fontSize > 96))
        { ShowPostSettingsError("文字の大きさは、8〜96 の数字で入れてください。"); return; }
        var color = PostSetColorBox.Text.Trim();
        if (PostLayout.UsesFont(kind) && color.Length > 0 && (!color.StartsWith('#') || ParsePostColor(color) == null))
        { ShowPostSettingsError("文字の色は、#RRGGBB の形（例：#FF8800）で入れてください。"); return; }

        var usesItems = PostLayout.UsesItems(kind);
        var max = PostLayout.MaxItems(kind);
        var count = panel.ItemCount;
        if (usesItems)
        {
            if (!int.TryParse(PostSetCountBox.Text.Trim(), out count) || count < 1 || count > max)
            { ShowPostSettingsError($"件数は 1〜{max} の数字で入れてください。"); return; }
        }
        var picks = _postChosen.Select(p => p.Id).ToList();
        if (kind == PostPanelKind.Priority && picks.Count > PostLayout.MaxPriorityGames)
        { ShowPostSettingsError($"優先したい作品は、{PostLayout.MaxPriorityGames} 件までです（いま {picks.Count} 件）。"); return; }

        // 種類を変えると、タイトルの「全カラム幅」の決まりが変わるので、先に種類を入れて試す
        var oldKind = panel.Kind;
        panel.Kind = kind;
        var error = layout.TryPlace(panel, row1 - 1, Math.Max(0, PostSetColBox.SelectedIndex), PostSetSpanBox.SelectedIndex + 1,
            PostSetRowSpanBox.SelectedIndex + 1);
        if (error != null) { panel.Kind = oldKind; ShowPostSettingsError(error); return; }

        var title = PostSetTitleBox.Text.Trim();
        panel.Title = title.Length == 0 || title == PostLayout.DefaultTitle(kind) ? null : title;
        if (PostLayout.UsesFont(kind))
        {
            panel.FontFamily = PostSetFontBox.SelectedIndex > 0 ? PostSetFontBox.SelectedItem as string : null;
            panel.FontColor = color.Length == 0 ? null : color;
            panel.FontSize = fontSize;
        }
        if (kind == PostPanelKind.Title)
        {
            panel.ShowPeriod = PostSetPeriodCheck.IsChecked == true;
            panel.FullBleed = PostSetBleedCheck.IsChecked == true;
            panel.Transparency = PostSetTransFollow.IsChecked == true ? null : (int)PostSetTrans.Value;
        }
        if (usesItems)
        {
            panel.ItemCount = count;
            panel.ItemColumns = Math.Max(0, PostSetItemColsBox.SelectedIndex);
        }
        else if (kindChanged) panel.ItemCount = PostLayout.NewPanel(kind).ItemCount;
        if (kind == PostPanelKind.Memo) panel.Text = PostSetTextBox.Text;
        panel.ViewColumns = PostLayout.UsesViewColumns(kind) ? Math.Max(0, PostSetViewColsBox.SelectedIndex) : 0;
        panel.Align = PostLayout.UsesAlign(kind) ? Math.Max(0, PostSetAlignBox.SelectedIndex) : 0;
        if (kind == PostPanelKind.Priority)
        {
            panel.GameIds = picks; // 下の「選んだ作品」の並びのまま
        }
        _cfg.Save();
        RebuildPostReport();
        OpenPostSettings(panel); // 反映後の値で表示し直す
        PostStatus.Text = "パネルの設定を反映しました";
    }

    // ================================================================
    // デザイン設定（背景の色・画像、パネルの色と透過率、フォントと文字色）
    // ================================================================

    private const int PostLabelSizeDefault = 13, PostLabelSizeMin = 8, PostLabelSizeMax = 40;

    private string? _postDesImage; // 設定欄で選んだ背景の画像（「適用」で反映）

    private void PostDesign_Click(object sender, RoutedEventArgs e)
    {
        _postEditing = null;
        PostSettingsPanel.Visibility = Visibility.Collapsed; // 右の欄は、どちらか一方だけ
        _postLoading = true;
        PostDesBackColor.Text = _cfg.PostBackColor ?? "";
        PostDesCardColor.Text = _cfg.PostCardColor ?? "";
        PostDesTextColor.Text = _cfg.PostTextColor ?? "";
        PostDesTransparency.Value = Math.Clamp(_cfg.PostPanelTransparency, 0, 100);
        PostDesTransparencyText.Text = $"{(int)PostDesTransparency.Value}%";
        PostDesFontBox.ItemsSource = PostFontNames();
        var fontIndex = string.IsNullOrWhiteSpace(_cfg.PostFontFamily) ? -1 : PostFontNames().IndexOf(_cfg.PostFontFamily);
        PostDesFontBox.SelectedIndex = Math.Max(0, fontIndex);
        PostDesLabelSize.Text = _cfg.PostLabelSize > 0 ? _cfg.PostLabelSize.ToString() : "";
        _postDesImage = _cfg.PostBackImage;
        ShowPostDesignImageName();
        ShowPostDesignError(null);
        _postLoading = false;
        foreach (var box in new[] { PostDesBackColor, PostDesCardColor, PostDesTextColor }) UpdateColorSample(box);
        PostDesignPanel.Visibility = Visibility.Visible;
    }

    private void PostDesClose_Click(object sender, RoutedEventArgs e) => PostDesignPanel.Visibility = Visibility.Collapsed;

    private void ShowPostDesignError(string? message)
    {
        PostDesError.Text = message ?? "";
        PostDesError.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowPostDesignImageName() =>
        PostDesImageName.Text = string.IsNullOrWhiteSpace(_postDesImage) ? "画像なし" : _postDesImage;

    /// <summary>色の欄の右に、入力した色の見本を出す（読めない色は、見本なし）</summary>
    private void UpdateColorSample(TextBox box)
    {
        if (box.Tag is string name && FindName(name) is Border sample)
            sample.Background = ParsePostColor(box.Text) ?? Brushes.Transparent;
    }

    private void PostDesColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) UpdateColorSample(box);
    }

    private void PostDesTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PostDesTransparencyText != null) PostDesTransparencyText.Text = $"{(int)e.NewValue}%";
    }

    private void PostDesPickImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "画像 (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif" };
        if (dlg.ShowDialog(this) != true) return;
        _postDesImage = dlg.FileName;
        ShowPostDesignImageName();
    }

    private void PostDesClearImage_Click(object sender, RoutedEventArgs e)
    {
        _postDesImage = null;
        ShowPostDesignImageName();
    }

    private void PostDesApply_Click(object sender, RoutedEventArgs e)
    {
        // 色の欄を調べる。空欄は「標準」。入っていたら #RRGGBB の形でなければならない
        bool Valid(TextBox box, string label)
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || (text.StartsWith('#') && ParsePostColorValue(text) != null)) return true;
            ShowPostDesignError($"{label}は、#RRGGBB の形（例：#FF8800）で入れてください。");
            return false;
        }
        if (!Valid(PostDesBackColor, "背景色") || !Valid(PostDesCardColor, "パネルの色") || !Valid(PostDesTextColor, "文字の色")) return;
        if (!string.IsNullOrWhiteSpace(_postDesImage) && !File.Exists(_postDesImage))
        { ShowPostDesignError("背景の画像のファイルが見つかりません。選び直してください。"); return; }

        var labelSize = 0;
        if (PostDesLabelSize.Text.Trim().Length > 0 &&
            (!int.TryParse(PostDesLabelSize.Text.Trim(), out labelSize) || labelSize < PostLabelSizeMin || labelSize > PostLabelSizeMax))
        { ShowPostDesignError($"項目名の大きさは、{PostLabelSizeMin}〜{PostLabelSizeMax} の数字で入れてください。"); return; }

        string? OrNull(TextBox box) => box.Text.Trim().Length == 0 ? null : box.Text.Trim();
        _cfg.PostLabelSize = labelSize;
        _cfg.PostBackColor = OrNull(PostDesBackColor);
        _cfg.PostCardColor = OrNull(PostDesCardColor);
        _cfg.PostTextColor = OrNull(PostDesTextColor);
        _cfg.PostBackImage = string.IsNullOrWhiteSpace(_postDesImage) ? null : _postDesImage;
        _cfg.PostPanelTransparency = (int)PostDesTransparency.Value;
        var newFont = PostDesFontBox.SelectedIndex > 0 ? PostDesFontBox.SelectedItem as string : null;
        // 全体のフォントを変えたときは、各パネルで個別に決めたフォントより優先して、すべてのパネルを全体のフォントにそろえる
        if (!string.Equals(newFont, _cfg.PostFontFamily, StringComparison.Ordinal))
            foreach (var p in Layout.Panels) p.FontFamily = null;
        _cfg.PostFontFamily = newFont;

        ShowPostDesignError(null);
        _cfg.Save();
        RebuildPostReport();
        PostStatus.Text = "デザインを反映しました";
    }

    private void PostDesReset_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg.PostFontFamily != null) foreach (var p in Layout.Panels) p.FontFamily = null; // 全体のフォントが変わるので、パネルのフォントもそろえる
        _cfg.PostBackColor = _cfg.PostCardColor = _cfg.PostTextColor = _cfg.PostBackImage = _cfg.PostFontFamily = null;
        _cfg.PostPanelTransparency = 0;
        _cfg.PostLabelSize = 0;
        _cfg.Save();
        PostDesign_Click(sender, e); // 設定欄を、標準の値で開き直す
        RebuildPostReport();
        PostStatus.Text = "デザインを標準に戻しました";
    }

    // ================================================================
    // 画像出力（NovelKeeper_年月日.jpg。幅は画面の表示に合わせ、最大 1920）
    // ================================================================

    private async void PostExport_Click(object sender, RoutedEventArgs e)
    {
        if (_post == null) return;
        PostExportButton.IsEnabled = false;
        try
        {
            // 取得中の画像があれば、少し待つ
            PostStatus.Text = "画像を準備中…";
            var pending = _postImageTasks.ToArray();
            if (pending.Length > 0) await Task.WhenAny(Task.WhenAll(pending), Task.Delay(15000));
            PostReportHost.UpdateLayout();
            PostStatus.Text = "";

            var dlg = new SaveFileDialog
            {
                Filter = "JPEG 画像 (*.jpg)|*.jpg",
                FileName = $"NovelKeeper_{DateTime.Today:yyyyMMdd}.jpg",
                DefaultExt = ".jpg", AddExtension = true, OverwritePrompt = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            var (width, height) = SavePostImage(dlg.FileName);
            PostStatus.Text = $"保存しました：{dlg.FileName}（{width}×{height}）";
        }
        catch (Exception ex)
        {
            Log.Warn("投稿設定：画像を出力できませんでした", ex);
            PostStatus.Text = "";
            MessageBox.Show(this, "画像を出力できませんでした。\n" + ex.Message, "NovelKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { PostExportButton.IsEnabled = _post != null && (_post.TotalSeconds >= 1 || _post.Works.Count > 0); }
    }

    /// <summary>レポートエリアを jpg にして保存する。画面の表示幅に合わせ、1920 ピクセルを超えるときは縮める（⚙ × は入れない）</summary>
    private (int Width, int Height) SavePostImage(string path)
    {
        var host = PostReportHost;
        double w = host.ActualWidth, h = host.ActualHeight;
        if (w < 1 || h < 1) throw new InvalidOperationException("レポートエリアが表示されていません。");

        var scale = Math.Min(VisualTreeHelper.GetDpi(host).DpiScaleX, PostMaxImageWidth / w); // 画面の実際の画素数に合わせる
        int pw = Math.Max(1, (int)Math.Round(w * scale)), ph = Math.Max(1, (int)Math.Round(h * scale));

        HidePostDropMarker();
        foreach (var b in _postEditButtons) b.Visibility = Visibility.Hidden; // 場所は空けたまま、描かない
        try
        {
            var dv = new DrawingVisual { Transform = new ScaleTransform(scale, scale) };
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(PostBrush(_postBackColor), null, new Rect(0, 0, w, h)); // jpg は透明を持てないので、不透明な下地を敷く
                dc.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);

            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        finally
        {
            foreach (var b in _postEditButtons) b.Visibility = Visibility.Visible;
        }
        return (pw, ph);
    }
}
