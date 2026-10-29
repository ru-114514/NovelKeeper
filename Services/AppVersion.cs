namespace NovelKeeper.Services;

/// <summary>アプリのバージョン（version.txt の番号が、ビルドで埋め込まれる。「1.6.1」の形）</summary>
public static class AppVersion
{
    public static string Text { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
