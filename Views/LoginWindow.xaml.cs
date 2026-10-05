using System.Windows;
using System.Windows.Media;
using ErogeLauncher.Services;

namespace ErogeLauncher.Views;

public partial class LoginWindow : Window
{
    private readonly EgsClient _egs;

    public LoginWindow(EgsClient egs)
    {
        InitializeComponent();
        _egs = egs;
        UserBox.Text = CredentialStore.Load()?.User ?? "";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        var pass = PassBox.Password;
        if (user == "" || pass == "")
        {
            ResultText.Text = "ユーザーIDとパスワードを入力してください。";
            return;
        }

        CredentialStore.Save(user, pass);
        SaveButton.IsEnabled = false;
        ResultText.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty); // テーマの標準色
        ResultText.Text = "ログインを試しています…";
        try
        {
            await _egs.LoginAsync(user, pass);
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71)); // 緑（どちらのテーマでも見やすい色）
            ResultText.Text = "ログインに成功しました。";
        }
        catch (Exception ex)
        {
            ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x4B)); // 赤
            ResultText.Text = "保存はしましたが、ログインに失敗しました：\n" + ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
