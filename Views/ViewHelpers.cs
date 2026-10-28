using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NovelKeeper.Views;

/// <summary>
/// メイン画面と、そこから分けたタブの画面（UserControl）で共通に使う、小さな道具。
/// ファイルの先頭で using static NovelKeeper.Views.ViewHelpers; とすると、名前だけで呼べる
/// </summary>
internal static class ViewHelpers
{
    /// <summary>グラフの棒の色（レポート・投稿画像で共通）</summary>
    public static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF)));

    /// <summary>カレンダー・ヒートマップの濃淡（0：なし 〜 4：いちばん長い）</summary>
    public static readonly Brush[] CalendarBrushes =
    {
        Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80))),
        Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x4C, 0x8D, 0xFF))),
        Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0x8D, 0xFF))),
    };

    public static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>親の部品（文字の一部などの場合は、見た目の部品ではないので別の方法でたどる）</summary>
    public static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>ドラッグ中の置き場所を、上にあるときだけ濃くする</summary>
    public static void SetZoneHighlight(Border zone, bool on)
    {
        zone.Background = new SolidColorBrush(on ? Color.FromArgb(0x70, 0x4A, 0x90, 0xE2) : Color.FromArgb(0x26, 0x4A, 0x90, 0xE2));
    }

    /// <summary>マウスカーソルの位置（画面の拡大率を考慮した WPF の座標）。拡大率は、visual が表示されている画面のものを使う</summary>
    public static Point CursorPositionInDip(Visual visual)
    {
        var p = System.Windows.Forms.Cursor.Position; // 実ピクセル
        var source = PresentationSource.FromVisual(visual);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(new Point(p.X, p.Y));
    }

    /// <summary>フォルダをエクスプローラーで開く（explorer.exe は Windows フォルダのものを指定する。同じ名前の偽物を使わないため）</summary>
    public static void OpenInExplorer(string folder)
    {
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, $"\"{folder}\"") { UseShellExecute = true });
    }
}
