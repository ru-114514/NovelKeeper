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

/// <summary>投稿設定タブ：デザイン設定</summary>
public partial class MainWindow
{
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
}
