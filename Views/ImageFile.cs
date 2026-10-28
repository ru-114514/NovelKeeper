using System.Windows.Media.Imaging;

namespace NovelKeeper.Views;

/// <summary>画像ファイルを読む（読んだらファイルを閉じる。読めなければ例外）</summary>
internal static class ImageFile
{
    /// <param name="decodeWidth">この幅に縮めて読む（大きな画像でメモリを使いすぎない）。0 なら縮めない</param>
    /// <param name="decodeHeight">この高さに縮めて読む。0 なら縮めない</param>
    public static BitmapImage Load(string path, int decodeWidth = 0, int decodeHeight = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad; // ファイルを開いたままにしない
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.DecodePixelHeight = decodeHeight;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
