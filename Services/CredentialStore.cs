using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ErogeLauncher.Services;

/// <summary>
/// 批評空間のログイン情報を、Windows の DPAPI で暗号化して保存する。
/// 同じ Windows ユーザーでしか復号できない。
/// </summary>
public static class CredentialStore
{
    public static void Save(string user, string password)
    {
        var plain = Encoding.UTF8.GetBytes(user + "\n" + password);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.Credential, encrypted);
    }

    /// <summary>保存したログイン情報を消す</summary>
    public static void Delete()
    {
        if (File.Exists(AppPaths.Credential)) File.Delete(AppPaths.Credential);
        EgsClient.DeleteDebugFiles(); // ログインの調査用ファイルも消す
    }

    public static (string User, string Password)? Load()
    {
        if (!File.Exists(AppPaths.Credential)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(AppPaths.Credential), null, DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(plain);
            var i = s.IndexOf('\n');
            return i < 0 ? null : (s[..i], s[(i + 1)..]);
        }
        catch (Exception ex)
        {
            Log.Warn("保存したログイン情報を読み込めませんでした", ex);
            return null;
        }
    }
}
