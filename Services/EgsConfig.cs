using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ErogeLauncher.Services;

/// <summary>
/// 批評空間との連携設定。%AppData%\ErogeLauncher\egs_config.json に保存される。
/// 批評空間のページの作りが変わったときは、README の手順で調べてこのファイルを書き換える。
/// </summary>
public class EgsConfig
{
    /// <summary>批評空間の基本URL（相対パスはこれにつなげる）</summary>
    public string BaseUrl { get; set; } = "https://erogamescape.dyndns.org/~ap2/ero/toukei_kaiseki/";

    /// <summary>ページの文字コード（utf-8 / euc-jp など）</summary>
    public string CharSet { get; set; } = "utf-8";

    /// <summary>リクエストの最小間隔（ミリ秒）。サイトに負荷をかけないため</summary>
    public int RequestIntervalMs { get; set; } = 2000;

    // ---- 読み込み（SQLページ） ----
    public string SqlPage { get; set; } = "sql_for_erogamer_form.php";
    public string SqlFieldName { get; set; } = "sql";

    /// <summary>userreview テーブルで「プレイ済」を表す列名</summary>
    public string PlayedColumn { get; set; } = "play";

    /// <summary>userreview テーブルで「クリア日」を表す列名（なければ空欄）</summary>
    public string ClearDateColumn { get; set; } = "finish_date";

    // ---- ログイン ----
    public string LoginPage { get; set; } = "login.php";

    /// <summary>ログイン後のページにだけ出てくる文字（ログイン成功の判定に使う）</summary>
    public string LoggedInMarker { get; set; } = "ログアウト";

    // ---- 書き込み（得点登録フォーム） ----
    /// <summary>得点登録ページのURL。{gameId} が作品IDに置き換わる</summary>
    public string ReviewFormUrl { get; set; } = "contents_game.php?game_id={gameId}";

    /// <summary>フォームの「プレイ済」チェックボックスの name 属性</summary>
    public string PlayedField { get; set; } = "data[play]";

    /// <summary>チェック時に送る値（空欄ならフォームの value 属性をそのまま使う）</summary>
    public string PlayedValue { get; set; } = "";

    /// <summary>クリア日が1つの入力欄の場合の name 属性（年月日が別々なら空欄）</summary>
    public string ClearDateField { get; set; } = "data[finish_date]";
    public string ClearDateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>クリア日が年・月・日に分かれている場合の name 属性</summary>
    public string ClearDateYearField { get; set; } = "";
    public string ClearDateMonthField { get; set; } = "";
    public string ClearDateDayField { get; set; } = "";

    // ---- 自分の得点 ----
    /// <summary>userreview テーブルの得点の列名</summary>
    public string ScoreColumn { get; set; } = "tokuten";
    /// <summary>得点登録フォームの得点欄の name 属性</summary>
    public string ScoreField { get; set; } = "data[tokuten]";

    // ---- 一覧の並び順（画面で変更すると自動で保存される） ----
    public List<SortKey> SortKeys { get; set; } = new() { new SortKey { Property = "Title" } };

    // ---- プレイ時間の計測 ----
    /// <summary>操作がない時間を除外するか</summary>
    public bool ExcludeIdle { get; set; } = true;
    /// <summary>この分数以上、マウス・キーボードの操作がなければ計測を止める</summary>
    public int IdleMinutes { get; set; } = 15;
    /// <summary>ゲームの画面が前面にあるときだけ数えるか</summary>
    public bool ForegroundOnly { get; set; } = false;
    /// <summary>閉じるボタンで終了せず、通知領域に常駐するか</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Windows の起動時に自動で起動するか</summary>
    public bool AutoStart { get; set; } = false;
    /// <summary>計測の開始・終了を通知するか</summary>
    public bool NotifyOnStart { get; set; } = true;

    // ---- 初回 ----
    /// <summary>初回の案内を表示したか</summary>
    public bool WelcomeShown { get; set; }

    // ---- 一覧画面の配置 ----
    /// <summary>詳細欄を表示するか</summary>
    public bool DetailVisible { get; set; } = true;
    /// <summary>詳細欄の位置："Right"（右に縦長）/ "Bottom"（下に横長）/ "Float"（別ウィンドウで自由な位置）</summary>
    public string DetailPosition { get; set; } = "Right";
    /// <summary>右に置いたときの幅</summary>
    public double DetailWidth { get; set; } = 330;
    /// <summary>下に置いたときの高さ</summary>
    public double DetailHeight { get; set; } = 260;
    /// <summary>別ウィンドウにしたときの位置と大きさ（位置が null なら一覧の横に出す）</summary>
    public double? FloatLeft { get; set; }
    public double? FloatTop { get; set; }
    public double FloatWidth { get; set; } = 360;
    public double FloatHeight { get; set; } = 560;

    /// <summary>一覧の列の幅（見出し → 幅）。見出しの境目をドラッグして変えると保存される</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>一覧の列の並び順と表示・非表示（見出しの名前で記録）。空なら初期状態</summary>
    public List<ColumnSetting> Columns { get; set; } = new();

    /// <summary>詳細欄に作品の画像を表示するか</summary>
    public bool ShowImages { get; set; } = true;

    /// <summary>詳細欄で開いているグループ（プレイ時間・購入／動作環境／感想）</summary>
    public List<string> ExpandedSections { get; set; } = new();

    /// <summary>購入前（購入予定など）の作品も一覧に表示するか</summary>
    public bool ShowBeforePurchase { get; set; } = false;

    /// <summary>一覧の表示倍率（1.0 = 100%）。Ctrl＋マウスホイールで変更</summary>
    public double ListZoom { get; set; } = 1.0;

    // ---- 見た目 ----
    /// <summary>テーマ："Dark"（ダーク）/ "Light"（ライト）/ "System"（PCの設定に合わせる）</summary>
    public string Theme { get; set; } = "Dark";

    // ---- その他 ----
    public string GamePageUrl { get; set; } = "game.php?game={gameId}";

    // ---- 実行ファイルの自動検出 ----
    /// <summary>ゲームをインストールしているフォルダ（複数指定可）</summary>
    public List<string> GameFolders { get; set; } = new(); // 初回の自動検出で指定してもらう

    /// <summary>Steam のライブラリも探すか</summary>
    public bool ScanSteam { get; set; } = true;

    /// <summary>自動検出でショートカットも判定に使うか（初期状態では使わない）</summary>
    public bool UseShortcuts { get; set; } = false;

    /// <summary>ショートカットを置いているフォルダ（直下だけを探す。サブフォルダは個別に指定）</summary>
    public List<string> ShortcutFolders { get; set; } = DefaultShortcutFolders();

    /// <summary>スタートメニューのショートカットも探すか</summary>
    public bool ScanStartMenu { get; set; } = true;

    private static List<string> DefaultShortcutFolders()
    {
        return new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
    }

    // ================================================================

    public static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith("TODO");

    public string Url(string pathOrUrl, int? gameId = null)
    {
        var s = gameId is int id ? pathOrUrl.Replace("{gameId}", id.ToString()) : pathOrUrl;
        return s.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s : BaseUrl + s;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 念のため、数値の NaN や無限大が入っても保存できるようにする
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま書き出す
    };

    /// <summary>設定ファイルが壊れていて、初期値に戻したときのメッセージ（1回だけ表示する）</summary>
    public static string? ResetNotice { get; set; }

    /// <summary>設定を読み込む。ファイルがなければ初期値で作成する。壊れていたら控えを残して初期値に戻す</summary>
    public static EgsConfig Load()
    {
        if (File.Exists(AppPaths.Config))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<EgsConfig>(File.ReadAllText(AppPaths.Config), JsonOptions);
                if (loaded != null)
                {
                    loaded.Save(); // 新しく増えた項目を書き足す
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
            {
                var broken = AppPaths.Config + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
                try { File.Copy(AppPaths.Config, broken, overwrite: true); } catch { /* 控えが作れなくても続ける */ }
                ResetNotice = "設定ファイルが壊れていたため、設定を初期状態に戻しました。\n" +
                              "（作品の一覧・プレイ時間・ログイン情報はそのままです）\n\n壊れたファイルの控え：\n" + broken;
            }
        }
        var cfg = new EgsConfig();
        cfg.Save();
        return cfg;
    }

    /// <summary>
    /// 設定を保存する。いったん別のファイルに書いてから置き換えるので、
    /// 保存中にPCが止まっても、設定ファイルが壊れない
    /// </summary>
    public void Save()
    {
        var tmp = AppPaths.Config + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, AppPaths.Config, overwrite: true);
    }
}

/// <summary>並び替えの条件1つ分</summary>
public class SortKey
{
    public string Property { get; set; } = "Title";
    public bool Descending { get; set; }
}

/// <summary>一覧の列1つ分の設定</summary>
public class ColumnSetting
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
}
