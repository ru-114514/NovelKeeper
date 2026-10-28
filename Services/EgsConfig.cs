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

    /// <summary>批評空間への通信の最小間隔（ミリ秒）。サイトに負荷をかけないため固定で、設定では変えられない</summary>
    public const int RequestIntervalMs = 1000;

    /// <summary>実際に使う間隔（テストでだけ 0 にする。設定ファイルには出さない）</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal int EffectiveIntervalMs { get; set; } = RequestIntervalMs;

    /// <summary>「批評空間と同期」が成功した最後の日時（続けて押したときに確認するため）</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>「批評空間と同期」「一括取り込み」を最後に始めた日時（成功・失敗を問わない。続けて行えない時間の起点）</summary>
    public DateTime? LastSyncAttemptAt { get; set; }

    /// <summary>「批評空間と同期」「一括取り込み」が続けて失敗した回数（失敗が続くほど、次に押せるまでの時間を延ばす）</summary>
    public int SyncFailureCount { get; set; }

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

    /// <summary>
    /// 得点登録フォームの、感想などの欄の name 属性（項目名 → name）。
    /// 批評空間の欄の名前が変わったときは、ここを書き換える（変えたい項目だけ書けばよい。書かなかった項目は既定値）。
    /// </summary>
    public Dictionary<string, string> ReviewFields { get; set; } = new(DefaultReviewFields);

    public static readonly IReadOnlyDictionary<string, string> DefaultReviewFields = new Dictionary<string, string>
    {
        ["possession"] = "data[possession]",
        ["giveup"] = "data[giveup]",
        ["total_play_time"] = "data[total_play_time]",
        ["fun_time"] = "data[time_before_understanding_fun]",
        ["okazu"] = "data[okazu]",
        ["purchase_will"] = "data[before_purchase_will]",
        ["before_tokuten"] = "data[before_tokuten]",
        ["start_date"] = "data[start_date]",
        ["platform"] = "data[platform][platform]",
        ["operation"] = "data[platform][operation]",
        ["platform_memo"] = "data[platform][memo]",
        ["hitokoto"] = "data[hitokoto]",
        ["sage"] = "data[sage]",
        ["memo"] = "data[memo]",
        ["netabare"] = "data[netabare]",
    };

    /// <summary>感想などの項目の、フォームの欄の name（設定になければ既定値）</summary>
    public string ReviewFormField(string key) =>
        ReviewFields.TryGetValue(key, out var name) && IsSet(name) ? name.Trim() : DefaultReviewFields[key];

    // ---- 一覧の並び順（画面で変更すると自動で保存される） ----
    public List<SortKey> SortKeys { get; set; } = new() { new SortKey { Property = "Title" } };
    /// <summary>並び替えの設定1〜3（一覧の「設定1〜3」ボタンで、ワンクリックで切り替える）。空のものは未設定</summary>
    public List<List<SortKey>> SortPresets { get; set; } = new() { new(), new(), new() };

    public const int SortPresetCount = 3;

    /// <summary>設定ファイルから読んだ並び替えの設定を、必ず3つ（足りなければ未設定）にそろえる</summary>
    public void NormalizeSortPresets()
    {
        SortPresets ??= new();
        while (SortPresets.Count < SortPresetCount) SortPresets.Add(new());
        if (SortPresets.Count > SortPresetCount) SortPresets.RemoveRange(SortPresetCount, SortPresets.Count - SortPresetCount);
        for (var i = 0; i < SortPresets.Count; i++) SortPresets[i] ??= new();

        NextPickFilter ??= new();
        NextPickPresets ??= new();
        while (NextPickPresets.Count < SortPresetCount) NextPickPresets.Add(null);
        if (NextPickPresets.Count > SortPresetCount) NextPickPresets.RemoveRange(SortPresetCount, NextPickPresets.Count - SortPresetCount);
    }

    /// <summary>「積みゲーに数えない」にした未プレイの作品（放置作品の見直しで除外したもの。状態は変えない。作品の ID）</summary>
    public List<long> BacklogExcludedIds { get; set; } = new();

    /// <summary>積みゲーの本数の記録（日付順。アプリを使った日に1件ずつ残る）</summary>
    public List<BacklogSnapshot> BacklogSnapshots { get; set; } = new();

    /// <summary>「次の1本」で最後に使った絞り込み条件</summary>
    public TraitFilter NextPickFilter { get; set; } = new();
    /// <summary>「次の1本」の保存した条件1〜3（空のものは null）</summary>
    public List<TraitFilter?> NextPickPresets { get; set; } = new() { null, null, null };

    // ---- プレイ時間の計測 ----
    /// <summary>操作がない時間を除外するか</summary>
    public bool ExcludeIdle { get; set; } = true;
    /// <summary>この分数以上、マウス・キーボードの操作がなければ計測を止める</summary>
    public int IdleMinutes { get; set; } = 15;
    /// <summary>放置と判断したとき、操作がなかった間に数えてしまった時間もさかのぼって除くか</summary>
    public bool SubtractIdleTime { get; set; } = true;
    /// <summary>ゲームの画面が前面にあるときだけ数えるか</summary>
    public bool ForegroundOnly { get; set; } = false;
    /// <summary>すべての作品を管理者権限で起動するか（作品ごとの設定は残るが、この間は無視する）</summary>
    public bool AlwaysRunAsAdmin { get; set; } = false;
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

    // ---- レポート画面の配置（グラフ・カレンダー・作品の3つの欄を、それぞれ好きな場所に置ける） ----
    /// <summary>欄の名前（"chart" / "calendar" / "games"）→ 置き場所などの状態</summary>
    public Dictionary<string, SummaryPanelState> SummaryPanels { get; set; } = new();
    /// <summary>左の置き場所の幅</summary>
    public double SummaryLeftWidth { get; set; } = 320;
    /// <summary>右の置き場所の幅</summary>
    public double SummaryRightWidth { get; set; } = 340;
    /// <summary>下の置き場所の高さ</summary>
    public double SummaryBottomHeight { get; set; } = 260;

    // ---- プレイの目標（0 は未設定） ----
    /// <summary>年間の目標：プレイ時間（時間）</summary>
    public int GoalYearHours { get; set; }
    /// <summary>年間の目標：クリア数（本）</summary>
    public int GoalYearClears { get; set; }
    /// <summary>月間の目標：プレイ時間（時間）</summary>
    public int GoalMonthHours { get; set; }
    /// <summary>月間の目標：クリア数（本）</summary>
    public int GoalMonthClears { get; set; }

    /// <summary>一覧の列の幅（見出し → 幅）。見出しの境目をドラッグして変えると保存される</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>一覧の列の並び順と表示・非表示（見出しの名前で記録）。空なら初期状態</summary>
    public List<ColumnSetting> Columns { get; set; } = new();

    /// <summary>列の並び順を、現在の初期の並びに整え直したか（2 未満なら、起動時に1回だけ整え直す）</summary>
    public int ColumnOrderVersion { get; set; }

    /// <summary>詳細欄に作品の画像を表示するか（オンだと、作品を選んだときに画像を取得する。初期はオン。オフでも、自分で差し替えた画像は表示する）</summary>
    public bool ShowImages { get; set; } = true;

    /// <summary>投稿設定：期間を1年にするか（false は1か月）</summary>
    public bool PostOneYear { get; set; }

    /// <summary>投稿設定：パネルのレイアウト（カラム数と、パネルの置き場所・設定）</summary>
    public PostLayout PostLayout { get; set; } = PostLayout.CreateDefault();

    /// <summary>投稿設定のデザイン：背景色（#RRGGBB。空なら、「背景: 明るい / 暗い」の色）</summary>
    public string? PostBackColor { get; set; }

    /// <summary>投稿設定のデザイン：背景の画像ファイル（空なら、画像なし）</summary>
    public string? PostBackImage { get; set; }

    /// <summary>投稿設定のデザイン：パネルの色（#RRGGBB。空なら標準）</summary>
    public string? PostCardColor { get; set; }

    /// <summary>投稿設定のデザイン：パネルを透かす割合（0〜100。0 は透かさない。背景の画像を見せたいときに上げる）</summary>
    public int PostPanelTransparency { get; set; }

    /// <summary>投稿設定のデザイン：全体のフォント（空なら標準）</summary>
    public string? PostFontFamily { get; set; }

    /// <summary>投稿設定のデザイン：全体の文字の色（#RRGGBB。空なら標準）</summary>
    public string? PostTextColor { get; set; }

    /// <summary>自動バックアップをするか</summary>
    public bool AutoBackupEnabled { get; set; } = true;
    /// <summary>自動バックアップの間隔（日。1 か 7）</summary>
    public int AutoBackupIntervalDays { get; set; } = 1;
    /// <summary>自動バックアップで残す世代数（3〜14）</summary>
    public int AutoBackupKeep { get; set; } = AutoBackup.DefaultKeep;
    /// <summary>自動バックアップの保存先フォルダ（空なら、アプリのフォルダの backups）</summary>
    public string AutoBackupDir { get; set; } = "";

    /// <summary>任意評価基準を使うか（オフでも、設定した項目と作品ごとの項目別の点数は残す）</summary>
    public bool ScoreItemsEnabled { get; set; }

    /// <summary>任意評価基準の項目（最高点の合計はちょうど 100）。基準1（これまでの設定はここに入っている）</summary>
    public List<ScoreItem> ScoreItems { get; set; } = new();
    /// <summary>基準2・基準3の項目（使わなければ空）</summary>
    public List<ScoreItem> ScoreItems2 { get; set; } = new();
    public List<ScoreItem> ScoreItems3 { get; set; } = new();
    /// <summary>基準1〜3の名前（空なら「基準1」などの初期名）</summary>
    public List<string> ScoreTemplateNames { get; set; } = new();

    /// <summary>基準 index（0〜2）の項目</summary>
    public List<ScoreItem> ScoreItemsOf(int index) => index switch { 0 => ScoreItems, 1 => ScoreItems2, _ => ScoreItems3 };

    public void SetScoreItems(int index, List<ScoreItem> items)
    {
        if (index == 0) ScoreItems = items; else if (index == 1) ScoreItems2 = items; else ScoreItems3 = items;
    }

    public string ScoreTemplateName(int index) =>
        index < ScoreTemplateNames.Count && !string.IsNullOrWhiteSpace(ScoreTemplateNames[index])
            ? ScoreTemplateNames[index].Trim() : ScoreItemSettings.DefaultName(index);

    /// <summary>投稿設定：画像の背景を暗くするか（false は明るい）</summary>
    public bool PostDarkBackground { get; set; }

    /// <summary>投稿設定：画像の見出し（自由入力）</summary>
    public string PostTitle { get; set; } = "ノベルゲーム　プレイレポート";

    /// <summary>投稿設定：起点の日より前にクリアした作品を、作品の欄から除くか</summary>
    public bool PostExcludeClearedBefore { get; set; } = true;
    /// <summary>投稿設定：起点の日より前にクリア済みでも、再プレイとして集計に含める作品（作品の ID）</summary>
    public List<long> PostReplayGameIds { get; set; } = new();
    /// <summary>投稿設定で「隠す」にした作品（クリア済みでなくても、プレイ記録の集計から外す。作品の ID）</summary>
    public List<long> PostHiddenGameIds { get; set; } = new();
    /// <summary>投稿設定のデザイン：パネルの項目名（見出し）の大きさ（0 なら標準の 13）</summary>
    public int PostLabelSize { get; set; }

    /// <summary>投稿設定：マイベストブランドの選び方（PostBrandMode の値）</summary>
    public int PostBrandMode { get; set; }

    /// <summary>投稿設定：「任意のブランド」で選んだブランド</summary>
    public string? PostBrandName { get; set; }

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

    /// <summary>設定ファイルの場所（既定は %AppData%\NovelKeeper\egs_config.json。テストでは別の場所を指定する）</summary>
    [System.Text.Json.Serialization.JsonIgnore] internal string? FilePath { get; set; }

    private string FileName => FilePath ?? AppPaths.Config;

    /// <summary>設定を読み込む。ファイルがなければ初期値で作成する。壊れていたら控えを残して初期値に戻す</summary>
    public static EgsConfig Load(string? path = null)
    {
        var file = path ?? AppPaths.Config;
        if (File.Exists(file))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<EgsConfig>(File.ReadAllText(file), JsonOptions);
                if (loaded != null)
                {
                    loaded.FilePath = path;
                    loaded.NormalizeSortPresets();
                    loaded.Save(); // 新しく増えた項目を書き足す
                    return loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
            {
                var broken = file + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
                try { File.Copy(file, broken, overwrite: true); }
                catch (Exception copyEx) { Log.Warn("壊れた設定ファイルの控えを作れませんでした", copyEx); } // 控えが作れなくても続ける
                ResetNotice = "設定ファイルが壊れていたため、設定を初期状態に戻しました。\n" +
                              "（作品の一覧・プレイ時間・ログイン情報はそのままです）\n\n壊れたファイルの控え：\n" + broken;
            }
        }
        var cfg = new EgsConfig { FilePath = path };
        cfg.Save();
        return cfg;
    }

    /// <summary>
    /// 設定を保存する。いったん別のファイルに書いてから置き換えるので、
    /// 保存中にPCが止まっても、設定ファイルが壊れない
    /// </summary>
    public void Save()
    {
        var tmp = FileName + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FileName, overwrite: true);
    }
}

/// <summary>並び替えの条件1つ分</summary>
/// <summary>レポート画面の欄1つ分の状態</summary>
public class SummaryPanelState
{
    /// <summary>"Main"（メイン）/ "Left"（左に縦長）/ "Right"（右に縦長）/ "Bottom"（下に横長）/ "Float"（別ウィンドウ）</summary>
    public string Position { get; set; } = "Main";
    public bool Visible { get; set; } = true;
    /// <summary>別ウィンドウにしたときの位置と大きさ（位置が null なら画面の右寄りに出す）</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 420;
}

public class SortKey
{
    public string Property { get; set; } = "Title";
    public bool Descending { get; set; }

    /// <summary>2つの並び順が同じか（条件の項目・向き・順番がすべて同じ）</summary>
    public static bool SameOrder(IReadOnlyList<SortKey> a, IReadOnlyList<SortKey> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Property == p.Second.Property && p.First.Descending == p.Second.Descending);
}

/// <summary>一覧の列1つ分の設定</summary>
public class ColumnSetting
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
}
