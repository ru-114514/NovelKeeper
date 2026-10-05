using System.IO;
using System.Net;
using System.Net.Http;

namespace ErogeLauncher.Services;

/// <summary>
/// 作品の画像（パッケージ画像など）を取得して、PCに保存しておく。
///  - 保存場所：%AppData%\NovelKeeper\images\{批評空間ID}.jpg
///  - 一度保存した画像は、次からは通信せずに使う
///  - 画像が見つからなかった作品は、しばらく（14日）探し直さない
/// </summary>
public class GameImageService
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(14);
    private const int MinBytes = 6000; // これより小さい画像は「準備中」などの仮の画像とみなす
    private const int MaxBytes = 10_000_000; // 大きすぎるものは取得しない

    /// <summary>画像を取りに行ってよい場所（https のみ。批評空間のデータで、ほかの場所に向けられないようにする）</summary>
    private static readonly string[] AllowedDomains = { "dmm.co.jp", "dmm.com", "suruga-ya.jp" };

    private readonly EgsClient _egs;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GameImageService(EgsClient egs)
    {
        _egs = egs;
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = MaxBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) NovelKeeper/1.0");
    }

    public static string Dir
    {
        get
        {
            var dir = Path.Combine(AppPaths.Dir, "images");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string ImagePath(int egsId) => Path.Combine(Dir, $"{egsId}.jpg");
    private static string NonePath(int egsId) => Path.Combine(Dir, $"{egsId}.none");

    /// <summary>保存済みの画像があればそのパス（通信しない）</summary>
    public static string? Cached(int egsId)
    {
        var path = ImagePath(egsId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>画像のパスを返す。保存していなければ取得する。見つからなければ null</summary>
    public async Task<string?> GetAsync(int egsId, CancellationToken token)
    {
        if (Cached(egsId) is string cached) return cached;
        var none = NonePath(egsId);
        if (File.Exists(none) && DateTime.Now - File.GetLastWriteTime(none) < RetryAfter) return null;

        await _lock.WaitAsync(token); // 1作品ずつ取得する
        try
        {
            if (Cached(egsId) is string again) return again;
            token.ThrowIfCancellationRequested();

            foreach (var url in await _egs.GetImageCandidatesAsync(egsId))
            {
                token.ThrowIfCancellationRequested();
                var bytes = await TryDownloadAsync(url);
                if (bytes == null) continue;
                var path = ImagePath(egsId);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
                if (File.Exists(none)) File.Delete(none);
                return path;
            }

            await File.WriteAllTextAsync(none, DateTime.Now.ToString("s"), CancellationToken.None);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>画像を取り直す（保存済みの画像と「見つからなかった」記録を消す）</summary>
    public static void Forget(int egsId)
    {
        try
        {
            if (File.Exists(ImagePath(egsId))) File.Delete(ImagePath(egsId));
            if (File.Exists(NonePath(egsId))) File.Delete(NonePath(egsId));
        }
        catch { /* 使用中などで消せなくても問題ない */ }
    }

    private static bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        AllowedDomains.Any(d => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                                u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    /// <summary>中身が本当に画像か（JPEG / PNG / GIF / WebP の先頭の印）</summary>
    private static bool LooksLikeImage(byte[] b) =>
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||
        (b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) ||
        (b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50);

    private async Task<byte[]?> TryDownloadAsync(string url)
    {
        if (!IsAllowed(url)) return null;
        try
        {
            using var res = await _http.GetAsync(url);
            if (!IsAllowed(res.RequestMessage?.RequestUri?.ToString() ?? url)) return null; // 転送先も確認
            if (!res.IsSuccessStatusCode) return null;
            var finalUrl = res.RequestMessage?.RequestUri?.ToString() ?? url;
            if (finalUrl.Contains("now_printing", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("noimage", StringComparison.OrdinalIgnoreCase)) return null;
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync();
            return bytes.Length >= MinBytes && LooksLikeImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }
}
