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

    /// <summary>アプリを終了する（常駐もやめる）</summary>
    void ExitApp();

    /// <summary>アプリを終了している途中か（別ウィンドウを閉じるときに、隠すだけにしないため）</summary>
    bool IsExiting { get; }

    /// <summary>メイン画面が表示されていて、レポートのタブを開いているか</summary>
    bool IsSummaryActive { get; }
}
