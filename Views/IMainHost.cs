using System.Windows;
using NovelKeeper.Data;
using NovelKeeper.Models;
using NovelKeeper.Services;

namespace NovelKeeper.Views;

/// <summary>
/// タブの画面（UserControl）から、メイン画面に頼むこと。
/// タブの画面は、メイン画面の部品を直接さわらず、ここにあるものだけを使う（メイン画面が実装する）
/// </summary>
internal interface IMainHost
{
    /// <summary>メッセージや保存画面の持ち主にするウィンドウ</summary>
    Window Window { get; }

    Database Db { get; }
    EgsConfig Config { get; }
    EgsClient Egs { get; }

    /// <summary>作品の画像（取得とキャッシュ。メイン画面と共有する）</summary>
    GameImageService Images { get; }

    /// <summary>下のステータス欄に表示する</summary>
    void SetStatus(string text);

    /// <summary>「一覧」タブにいま表示している作品（絞り込み・並び順のまま）</summary>
    IReadOnlyList<Game> ShownGames();

    /// <summary>「一覧」タブに切り替えて、その作品を選ぶ（絞り込みで隠れているときは「すべて」に戻す）</summary>
    void ShowGameInList(long gameId);

    /// <summary>「一覧」タブを読み直す（優先作品の「特例」列などを合わせる）</summary>
    void ReloadList();

    /// <summary>批評空間と通信中か（同期・取り込み・反映・ログイン）</summary>
    bool EgsBusy { get; }

    /// <summary>
    /// 批評空間との通信を始める（ログイン用：詳細欄は止めず、通信中でもメッセージは出さない）。
    /// すでに通信中なら false。true のときは、終わったら EndEgsWork を呼ぶ
    /// </summary>
    bool BeginEgsWork();

    /// <summary>批評空間との通信を終える</summary>
    void EndEgsWork();

    /// <summary>「批評空間から一括取り込み」を行う</summary>
    void ImportAll();

    /// <summary>「批評空間と同期」を行う（一覧の上の同期ボタンと同じ）</summary>
    void Sync();

    /// <summary>実行ファイルの自動検出の画面を開く（閉じたら一覧とゲーム起動設定タブを読み直す）</summary>
    void AutoDetect();

    /// <summary>アプリを終了する（常駐もやめる）</summary>
    void ExitApp();

    /// <summary>アプリを終了している途中か（別ウィンドウを閉じるときに、隠すだけにしないため）</summary>
    bool IsExiting { get; }

    /// <summary>メイン画面が表示されていて、レポートのタブを開いているか</summary>
    bool IsSummaryActive { get; }
}

/// <summary>
/// 詳細設定タブ（SettingsTabView）だけが使う、メイン画面への頼みごと。
/// 設定の保存はタブで行い、一覧・詳細欄への反映をメイン画面に頼む
/// </summary>
internal interface ISettingsHost : IMainHost
{
    /// <summary>一覧の行を描き直す（管理者権限の紫など、行の見た目だけ変わったとき）</summary>
    void RefreshListRows();

    /// <summary>詳細欄の「管理者権限で起動します」の表示を、選んでいる作品に合わせる</summary>
    void UpdateAdminBadge();

    /// <summary>テーマを切り替えたあとに、一覧の発売前・購入前の色を付け直す</summary>
    void ThemeChanged();

    /// <summary>詳細欄の位置・表示を、設定に合わせる</summary>
    void ApplyDetailLayout();

    /// <summary>「購入前の作品を整理…」の画面を開く</summary>
    void CleanupBeforePurchase();

    /// <summary>一覧の表示倍率を 100% に戻す</summary>
    void ResetZoom();

    /// <summary>詳細欄の作品の画像を出し直す（画像の表示のオン・オフを変えたとき）</summary>
    void RefreshImage();

    /// <summary>保存した作品の画像をすべて消す（自分で差し替えた画像は残す）</summary>
    void ClearImages();

    /// <summary>評価基準の設定ウィンドウを開く</summary>
    void OpenScoreCriteria();

    /// <summary>「項目ごとの採点を使う」を切り替える。切り替えた後の状態を返す（やめたときは元の状態）</summary>
    bool SetScoreItemsEnabled(bool on);

    /// <summary>一覧の列の設定（今ある列に合わせて整えたもの）</summary>
    IReadOnlyList<ColumnSetting> ColumnSettings();

    /// <summary>常に表示する列（隠せない）か</summary>
    bool IsLockedColumn(string key);

    /// <summary>列の設定を保存して、一覧に反映する</summary>
    void SaveColumns(List<ColumnSetting> columns);

    /// <summary>列の表示と並び順を初期状態に戻す</summary>
    void ResetColumns();
}
