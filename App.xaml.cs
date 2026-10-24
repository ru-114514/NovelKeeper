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

        // 手動で取り込んだバックアップがあれば、DB も設定も開く前に反映する
        var imported = DataBackup.ApplyPending(DataBackup.PendingDir, AppPaths.Database, AppPaths.Config, Path.Combine(AppPaths.Dir, "backups"));
        if (imported != null) MessageBox.Show(imported, "バックアップの取り込み");
        CheckDatabaseAtStartup();

        Config = EgsConfig.Load();
        ApplyTheme(Config.Theme); // メイン画面を開く前にテーマを決める
        HideTextBoxClearButton();

        // 自動起動の登録を、今の exe の場所で登録し直す（exe を移動した・名前が変わった場合に備えて）
        try { AutoStart.Set(Config.AutoStart); } catch (Exception ex) { Log.Warn("自動起動の登録に失敗", ex); } // 失敗しても起動は続ける
    }

    /// <summary>
    /// Fluent テーマの入力欄は、フォーカス中に右端へ「×」（入力を消すボタン）を出し、その分だけ文字の表示幅が狭くなる。
    /// 幅の狭い欄（点数など）では打っている文字が見えなくなるので、「×」を出さない。
    /// （テーマのテンプレートの部品名 "DeleteButton" を、非表示に固定する。見つからなければ何もしない）
    /// </summary>
    private static void HideTextBoxClearButton()
    {
        static void Hide(object sender)
        {
            if (sender is not TextBox box) return;
            if (box.Template == null) return;
            if (!box.IsLoaded) box.ApplyTemplate();
            if (box.Template.FindName("DeleteButton", box) is UIElement button && button.Visibility != Visibility.Collapsed)
                button.Visibility = Visibility.Collapsed; // 直接指定した値は、テーマのトリガーより優先される
        }
        EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Hide(s)));
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.GotKeyboardFocusEvent, new RoutedEventHandler((s, _) => Hide(s)));
    }

    /// <summary>
    /// 起動の最初（DB を開く前）に、作品データが壊れていないかを簡易検査する。
    /// 壊れていたら、最新の正常な自動バックアップから戻すかを聞く（確認なしでは、書き換えない）
    /// </summary>
    private static void CheckDatabaseAtStartup()
    {
        try
        {
            var db = AppPaths.Database;
            if (!File.Exists(db) || new FileInfo(db).Length == 0) return;
            var check = AutoBackup.CheckIntegrity(db);
            if (check.Ok) return;
            Log.Warn("作品データの検査で異常が見つかりました：" + check.Message);

            var healthy = AutoBackup.List(AutoBackup.BackupDir).FirstOrDefault(g => AutoBackup.CheckIntegrity(g.DbPath).Ok);
            if (healthy == null)
            {
                MessageBox.Show("作品データに異常が見つかりましたが、戻せる正常な自動バックアップがありません。\n\n" +
                                $"（{check.Message}）\n\nこのまま続けると、データが読めない・失われる恐れがあります。\n" +
                                $"{AppPaths.Dir} フォルダを残したまま、作者に連絡してください。", "NovelKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var count = AutoBackup.CountGames(healthy.DbPath);
            var ans = MessageBox.Show(
                $"作品データに異常が見つかりました。\n（{check.Message}）\n\n" +
                $"最新の正常なバックアップ（{healthy.Date:yyyy/MM/dd}、作品 {count?.ToString() ?? "?"} 件）から戻しますか？\n\n" +
                "・戻す前に、いまのファイルを backups フォルダに残します\n" +
                "・設定は変えません\n" +
                "・［いいえ］のまま続けると、異常のあるデータをそのまま使います",
                "作品データの検査", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
            if (ans != MessageBoxResult.Yes) return;

            Directory.CreateDirectory(AutoBackup.BackupDir);
            File.Copy(db, Path.Combine(AutoBackup.BackupDir, $"before_restore_{DateTime.Now:yyyyMMdd_HHmmss}.db"), overwrite: true);
            AutoBackup.StageRestore(healthy, withConfig: false, DataBackup.PendingDir);
            DataBackup.ApplyPending(DataBackup.PendingDir, db, AppPaths.Config, AutoBackup.BackupDir);
            if (Directory.Exists(DataBackup.PendingDir))
                MessageBox.Show("バックアップから戻せませんでした。詳しい内容は log.txt を確認してください。", "作品データの検査", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show($"{healthy.Date:yyyy/MM/dd} のバックアップから戻しました。", "作品データの検査");
        }
        catch (Exception ex)
        {
            Log.Warn("作品データの検査に失敗", ex); // 検査の失敗で、起動は止めない
        }
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
