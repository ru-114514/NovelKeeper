using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ErogeLauncher.Services;

namespace ErogeLauncher;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    /// <summary>「--tray」付きで起動された（Windows の起動時など）＝画面を出さずに常駐する</summary>
    public static bool StartInTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 二重起動するとプレイ時間を二重に数えてしまうので、1つだけ起動できるようにする
        _singleInstance = new Mutex(true, "NovelKeeper_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("すでに起動しています。\nタスクバー右下（通知領域）のアイコンから開いてください。",
                "NovelKeeper");
            Environment.Exit(0);
            return;
        }

        UseSystemSqlite();
        StartInTray = e.Args.Contains("--tray");
        base.OnStartup(e);

        // 想定外のエラーは、画面に出すだけでなく log.txt にも残す
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("未処理の例外", (Exception)args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("未処理のタスク例外", args.Exception); args.SetObserved(); };

        Config = EgsConfig.Load();
        ApplyTheme(Config.Theme); // メイン画面を開く前にテーマを決める

        // 自動起動の登録を、今の exe の場所で登録し直す（exe を移動した・名前が変わった場合に備えて）
        try { AutoStart.Set(Config.AutoStart); } catch (Exception ex) { Log.Warn("自動起動の登録に失敗", ex); } // 失敗しても起動は続ける
    }

    /// <summary>設定（egs_config.json）。起動時に1回だけ読み込み、画面と計測で共有する</summary>
    public static EgsConfig Config { get; private set; } = null!;

    /// <summary>
    /// SQLite 本体（winsqlite3.dll）は、必ず Windows のシステムフォルダのものを読み込む。
    /// exe と同じフォルダ（ダウンロードフォルダなど）に同じ名前の偽物が置かれていても、それを使わないため。
    /// </summary>
    private static void UseSystemSqlite()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_winsqlite3).Assembly, (name, _, _) =>
                name.StartsWith("winsqlite3", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "winsqlite3.dll"))
                    : IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("winsqlite3 の読み込み先を指定できませんでした（すでに設定されている場合など）", ex);
        }
    }

    /// <summary>
    /// テーマを切り替える。"Dark"（ダーク）/ "Light"（ライト）/ "System"（PCの設定に合わせる）
    /// .NET 9 以降の Fluent テーマ（Windows 11 風の見た目）を使う。
    /// </summary>
    public static void ApplyTheme(string theme)
    {
        Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark,
        };

        // ボタン同士がくっつかないよう、テーマのボタンのスタイルに余白を足したものを使う
        Current.Resources.Remove(typeof(Button));
        var baseStyle = Current.TryFindResource(typeof(Button)) as Style;
        var style = new Style(typeof(Button), baseStyle);
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(2)));
        Current.Resources[typeof(Button)] = style;
    }

    // 想定外のエラーでアプリが落ちないよう、内容を表示して続行する
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("画面での未処理の例外", e.Exception);
        MessageBox.Show($"{e.Exception.Message}\n\n詳しい内容は次のファイルに記録しました：\n{Log.Path}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
