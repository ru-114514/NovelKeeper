using System.Windows;
using System.Windows.Controls;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// 「ログイン設定」タブ：批評空間のユーザーIDとパスワードを保存して、ログインを試す。
/// メイン画面とは IMainHost を通してやりとりする
/// </summary>
public partial class LoginTabView : UserControl
{
    private IMainHost _host = null!;

    public LoginTabView()
    {
        InitializeComponent();
    }

    /// <summary>メイン画面とつなぐ（InitLoginTab より先に、1回だけ呼ぶ）</summary>
    internal void Init(IMainHost host)
    {
        _host = host;
    }

    internal void InitLoginTab()
    {
        LoginUserBox.Text = CredentialStore.Load()?.User ?? "";
        UpdateLoginState();
    }

    /// <summary>
    /// Enter キー：ユーザーID・パスワードの欄なら「保存してログイン」を押して true を返す。
    /// メイン画面の Enter の処理（1行の入力欄では次の欄へ進む）より先に、メイン画面から呼ぶ
    /// </summary>
    internal bool TryHandleEnter(DependencyObject focused)
    {
        if (!(ReferenceEquals(focused, LoginUserBox) || ReferenceEquals(focused, LoginPassBox)) || !LoginSaveButton.IsEnabled) return false;
        LoginSave_Click(this, new RoutedEventArgs());
        return true;
    }

    private void UpdateLoginState()
    {
        var cred = CredentialStore.Load();
        LoginStateText.Text = cred == null
            ? "ログイン情報：未設定"
            : $"ログイン情報：登録済み（ID：{cred.Value.User}）";
    }

    private async void LoginSave_Click(object sender, RoutedEventArgs e)
    {
        if (_host.EgsBusy)
        {
            LoginResultText.Text = "批評空間と通信中です。終わってからもう一度押してください。";
            return;
        }
        var user = LoginUserBox.Text.Trim();
        var pass = LoginPassBox.Password;
        if (user == "" || pass == "")
        {
            LoginResultText.Text = "ユーザーIDとパスワードを入力してください。";
            return;
        }

        // ログインの間は通信中にする（ログインの途中で同期などが始まって、ログイン状態が入れ替わらないように）
        if (!_host.BeginEgsWork()) return;
        CredentialStore.Save(user, pass);
        LoginPassBox.Clear();
        LoginSaveButton.IsEnabled = false;
        LoginResultText.ClearValue(TextBlock.ForegroundProperty);
        LoginResultText.Text = "ログインを試しています…";
        try
        {
            await _host.Egs.LoginAsync(user, pass);
            LoginResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3C, 0xB3, 0x71));
            LoginResultText.Text = "ログインに成功しました。次は「批評空間連携」タブで、作品を取り込んでください。";
        }
        catch (Exception ex)
        {
            LoginResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x53, 0x4B));
            LoginResultText.Text = "保存はしましたが、ログインに失敗しました：\n" + ex.Message;
        }
        finally
        {
            _host.EndEgsWork();
            LoginSaveButton.IsEnabled = true;
            UpdateLoginState();
        }
    }

    private void LoginDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_host.EgsBusy)
        {
            LoginResultText.Text = "批評空間と通信中です。終わってからもう一度押してください。";
            return;
        }
        if (MessageBox.Show("保存しているログイン情報を削除しますか？", "ログイン情報の削除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        CredentialStore.Delete();
        _host.Egs.Logout(); // ログイン状態も捨てる
        EgsClient.DeleteDebugFiles();
        LoginUserBox.Text = "";
        LoginPassBox.Clear();
        LoginResultText.Text = "ログイン情報を削除しました。";
        UpdateLoginState();
    }
}
