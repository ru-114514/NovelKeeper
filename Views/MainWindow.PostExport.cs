using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovelKeeper.Models;
using NovelKeeper.Services;
using Microsoft.Win32;

namespace NovelKeeper.Views;

/// <summary>投稿設定タブ：画像出力</summary>
public partial class MainWindow
{
    // ================================================================
    // 画像出力（NovelKeeper_年月日.jpg。幅は 1920 固定）
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

    /// <summary>レポートエリアを、幅 1920 ピクセルの jpg にして保存する（画面の拡大・縮小には影響されない。⚙ × は入れない）</summary>
    private (int Width, int Height) SavePostImage(string path)
    {
        var host = PostReportHost;
        double w = host.ActualWidth, h = host.ActualHeight;
        if (w < 1 || h < 1) throw new InvalidOperationException("レポートエリアが表示されていません。");

        var scale = PostMaxImageWidth / w; // 出力の幅は、いつも 1920 ピクセル
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
