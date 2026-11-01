# NovelKeeper

ノベルゲームのランチャー＆管理ツール（WPF / .NET 10 / Windows 専用）。批評空間（ErogameScape）と、プレイ済・クリア日・得点・感想を連動する。
詳しい構成・仕組み・同期のルールは [docs/開発者向け.md](docs/開発者向け.md)、利用者向けの説明は [README.md](README.md)。

## コマンド

```bash
dotnet build NovelKeeper.csproj                             # ビルド
dotnet test tests/NovelKeeper.Tests                         # 単体テスト（xUnit）
dotnet test tests/NovelKeeper.Tests --filter SyncEngineTests # 1クラスだけ
dotnet run                                                  # アプリを起動（実データ %AppData%\NovelKeeper を使う）
```

- SDK は `global.json` で 10.0.403 に固定（`rollForward: disable`）。CI（`.github/workflows/test.yml`）も同じ版で Release ビルドとテストを回す。
- **`build.bat` は実行しない。** 対話式で、`version.txt` のコミットとタグ付けまで行う配布用スクリプト。`version.txt`（csproj がここから版を読む）も手で書き換えない。

## 設計の方針

- 判断は画面（`MainWindow` / 各 `*TabView`）やプロセスの見張り（`PlayTimeTracker`）に書かず、`Services/` のテストできるクラスに置いてテストを書く（`SyncEngine` / `PushEngine` / `ProcessMatcher` / `SessionBook` / `SummaryCalculator` / `PostLayout` など）。テストからは `InternalsVisibleTo` で internal が見える。
- 画面のテストはない。画面を変えたら、確かめる項目をユーザーに挙げて、アプリで確認してもらう。
- 一覧以外のタブは UserControl（`Views/*TabView`）。メイン画面への頼みごとは `IMainHost`、詳細設定タブだけのものは `ISettingsHost` を通す。`MainWindow` は役割ごとの partial ファイルに分かれている。
- 設定はすべて `EgsConfig`（`egs_config.json`）に持つ。項目を足すときは、古い設定ファイルとの互換（`ConfigCompatibilityTests`。`PushEngineTests.cs` の中にある）と、バックアップから戻すときの扱い（`KeptOnImport`）も考える。文字や一覧の項目を足すと `DataBackupTests.EveryTextOrListSetting_IsClassifiedForImport` が落ちるので、`KeptOnImport` か、そのテストの `safe` のどちらかに入れる（URL・パス・欄の名前は `KeptOnImport`）。
- バックアップの取り込みでは、受け取った設定・起動先・DB を信用せず、確かめてから反映する（`DataBackup`）。

## 批評空間への配慮（変えないこと）

- 通信は1件ずつ、1秒以上あける（`EgsConfig.RequestIntervalMs`。設定では短くできない。テストだけ `EffectiveIntervalMs` を 0 にする）。
- 同期は続けて押せない（`SyncThrottle`：30秒、失敗が続くと最大5分）。裏で勝手に通信しない。User-Agent で名乗る。
- 書き込みは、フォームの今の値をすべて読んでから、変えた項目だけ書き換えて送る（得点・感想を消さないため）。
- SQL ページの応答に「SQLの実行結果」の見出しがなければ例外にする（メンテナンス画面を「記録0件」と取り違えないため）。

## 注意点

- データは `%AppData%\NovelKeeper`（`games.db`・`egs_config.json`・`credential.bin`・`log.txt`・`backups/`）。パスワードは DPAPI で暗号化して持つ（`CredentialStore`）。
- テストは本物の `%AppData%\NovelKeeper` に書かない（`TestSetup` がログを一時フォルダに向けている）。DB・設定を使うテストも一時フォルダで。
- Windows Forms は通知領域のアイコンだけに使う。`System.Windows.Forms` / `System.Drawing` の暗黙の using は外してある（WPF の `MessageBox` などと名前がぶつかるため）。
- SQLite は同梱せず、Windows 標準の `winsqlite3.dll` を使う。
- 改行は LF。`.bat` / `.cmd` / `.ps1` と `docs/*.txt` は CRLF（`.gitattributes`）。改行がずれると同じタグから同じ exe ができなくなる。
- 握りつぶす例外も `Log.Warn` で残す。パスワードはログに出さない。
- 作業は `D:\NovelKeeper` の main で直接行う。worktree（`.claude/worktrees/` の作業用の写し）やブランチは作らない。CLAUDE.md もここの1つだけ。
- `graphify-out/` は graphify の生成物。コードを検索するときは除く。

## 変更したときに合わせて直すもの

- 利用者から見える動きを変えたら：`README.md` と `docs/使い方.txt`。
- ファイルを足す・分ける、テストを足したら：`docs/開発者向け.md` の「プロジェクトの構成」とテストの表。

## 書き方

- コメント・コミットメッセージ・文書は日本語。専門用語を避けた、やさしい言い回しにそろえる（例：「〜を UserControl に分ける」）。
- コミットメッセージは「1行目に何をしたか」＋空行＋「- 」の箇条書きで中身。動作を変えないときは「動作は変えていない」と書く。
