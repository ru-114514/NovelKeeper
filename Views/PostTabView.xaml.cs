using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;
using static NovelKeeper.Views.ViewHelpers;

namespace NovelKeeper.Views;

/// <summary>「今後優先したい作品」の選択欄の1行</summary>
internal sealed class PostPick
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
}

/// <summary>
/// 投稿設定タブ：起点の日から1か月（または1年）のプレイ記録を、パネルを並べた1枚の画像にまとめる。
/// メイン画面とは IMainHost を通してやりとりする（レポートエリアの組み立ては .Report.cs、レイアウトの編集は .Layout.cs、
/// パネルごとの設定欄は .Settings.cs、デザイン設定は .Design.cs、画像出力は .Export.cs）
/// </summary>
public partial class PostTabView : UserControl
{
    private IMainHost _host = null!;
    private Database _db = null!;
    private EgsConfig _cfg = null!;

    public PostTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（InitPostTab より先に、1回だけ呼ぶ）</summary>
    internal void Init(IMainHost host)
    {
        _host = host;
        _db = host.Db;
        _cfg = host.Config;
    }

    private Window OwnerWindow => _host.Window;

    private const int PostMaxImageWidth = 1920; // レポートエリアの幅と、出力する画像の幅（ピクセル）

    // 画像の配色（アプリのテーマとは別。不透明な色だけを使う：jpg にしたときに色が変わらないように）
    private static SolidColorBrush PostBrush(int rgb) => (SolidColorBrush)Freeze(new SolidColorBrush(
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));

    private static SolidColorBrush PostBrush(Color c, double alpha = 1) => (SolidColorBrush)Freeze(new SolidColorBrush(
        Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B)));

    private static Color PostRgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private Brush _postCardBorder = Brushes.Gray, _postImageFill = Brushes.LightGray;
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
            var bmp = ImageFile.Load(path, decodeWidth: PostMaxImageWidth); // 出力の最大幅より大きくは読まない
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

    /// <summary>起動時：設定を画面に表示する（タブを開くたびの作り直しは、メイン画面から RefreshPost を呼ぶ）</summary>
    internal void InitPostTab()
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
    }

    // レポートエリアの本体は、いつも 1920 の幅で作る。画面には、拡大・縮小して表示する（出力は本体から作る）
    private const double PostZoomMin = 0.25, PostZoomMax = 2.0;
    private bool _postFit = true; // true の間は、表示欄の幅に合わせる（起動のたびに、ここから始まる）
    private double _postZoom = 1;

    /// <summary>表示欄の幅に、レポートエリアが収まる倍率</summary>
    private double PostFitZoom()
    {
        var avail = PostScroll.ActualWidth - SystemParameters.VerticalScrollBarWidth - 1;
        return avail > 0 ? Math.Clamp(avail / PostMaxImageWidth, PostZoomMin, PostZoomMax) : 1;
    }

    private void SetPostZoom(double zoom)
    {
        _postZoom = Math.Clamp(zoom, PostZoomMin, PostZoomMax);
        PostZoomTransform.ScaleX = PostZoomTransform.ScaleY = _postZoom;
        PostZoomText.Text = $"{_postZoom * 100:0}%" + (_postFit ? "（幅に合わせる）" : "");
    }

    private void PostScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_postFit) SetPostZoom(PostFitZoom());
    }

    private void PostFit_Click(object sender, RoutedEventArgs e)
    {
        _postFit = true;
        SetPostZoom(PostFitZoom());
    }

    /// <summary>Ctrl＋ホイールで、表示を拡大・縮小する（出力する画像の大きさは変わらない）</summary>
    private void PostScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        _postFit = false;
        SetPostZoom(_postZoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
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

    /// <summary>集計し直して、レポートエリアを作り直す</summary>
    internal void RefreshPost()
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
        SyncPriorityMarks();
        if (_post is not { } report) return;
        var hasPlay = report.TotalSeconds >= 1 || report.Works.Count > 0;
        PostExportButton.IsEnabled = hasPlay;
        PostStatus.Text = hasPlay ? "" : "この期間のプレイ記録はありません（画像出力はできません）";
        BuildPostReport(report);
    }

    /// <summary>「今後優先したい作品」が変わっていたら、一覧を読み直して「特例」列を合わせる</summary>
    private void SyncPriorityMarks()
    {
        var ids = Layout.PriorityIds();
        if (ids.SetEquals(Game.PriorityIds)) return;
        _host.ReloadList();
    }
}
