@echo off
chcp 65001 > nul
setlocal
cd /d "%~dp0"
set "ROOT=%CD%"

rem 配布物は、バージョンのコミットを一時フォルダに取り出して、そこで作る（作業中のファイルや git の管理外のファイルが混ざらない）。
rem そのため、付けたタグを誰が取り出して作っても、同じ exe（署名する前）ができる。確かめ方は docs\開発者向け.md の「配布物の確かめ方」。
rem 流れ：確認 → バージョンをコミット → 一時フォルダでテスト・ビルド → 成功したらタグを付ける（失敗したら、バージョンのコミットを取り消す）

rem ===== 確認 =====
rem SDK は global.json の版に固定している（通常版に同梱する .NET の版は、SDK の版で決まるため）
dotnet --version > nul 2>&1
if errorlevel 1 goto :nosdk
for /f "usebackq delims=" %%v in (`dotnet --version`) do set SDK=%%v

rem コミットしていない変更があると、配布物とタグの中身が食い違うので作らない
set DIRTY=
for /f "delims=" %%s in ('git status --porcelain') do set DIRTY=1
if defined DIRTY goto :dirty

for /f "delims=" %%b in ('git branch --show-current') do set BRANCH=%%b
if "%BRANCH%"=="main" goto :branchok
echo いまのブランチは「%BRANCH%」です（配布物は、ふつう main から作ります）。
set ANS=
set /p ANS=このまま続けますか？ [y/N]：
if /i not "%ANS%"=="y" exit /b 1
:branchok

rem ===== バージョン =====
rem version.txt に前回のバージョンが入っている。今回は、その最後の数字を1つ上げたものを提案する。
if not exist version.txt (echo 1.0.0)>version.txt
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v=[version](Get-Content 'version.txt' -TotalCount 1).Trim(); '{0}.{1}.{2}' -f $v.Major,$v.Minor,($v.Build+1)"`) do set NEXT=%%v
for /f "usebackq delims=" %%v in ("version.txt") do set PREV=%%v

echo 前回のバージョン： %PREV%
set VERSION=
set /p VERSION=今回のバージョンを入力してください（そのまま Enter で %NEXT%）：
if "%VERSION%"=="" set VERSION=%NEXT%
powershell -NoProfile -Command "if ('%VERSION%' -notmatch '^\d+\.\d+\.\d+$') { exit 1 }"
if errorlevel 1 goto :badversion
git rev-parse -q --verify "refs/tags/v%VERSION%" > nul
if not errorlevel 1 goto :tagexists
echo 今回のバージョン： %VERSION%（SDK %SDK%）
echo.

set DIST=%ROOT%\dist\NovelKeeper_v%VERSION%
set DIST_LITE=%ROOT%\dist\NovelKeeper_v%VERSION%_lite

echo [1/7] バージョンをコミットしています...
for /f "delims=" %%h in ('git rev-parse HEAD') do set BASE=%%h
rem 改行は LF にする（リポジトリと同じ形。CRLF だと git が警告を出す）
powershell -NoProfile -Command "[IO.File]::WriteAllText('%ROOT%\version.txt', '%VERSION%' + [char]10)"
if errorlevel 1 goto :failed
git diff --quiet -- version.txt
if errorlevel 1 git commit -q -m "バージョンを %VERSION% に更新" -- version.txt
if errorlevel 1 goto :failed
for /f "delims=" %%h in ('git rev-parse HEAD') do set COMMIT=%%h

echo [2/7] コミット %COMMIT:~0,7% を一時フォルダに取り出しています...
set WORK=%TEMP%\NovelKeeper_build_%COMMIT:~0,12%
if exist "%WORK%" rmdir /s /q "%WORK%"
git clone -q --no-hardlinks "%ROOT%" "%WORK%"
if errorlevel 1 goto :failed
git -C "%WORK%" checkout -q --detach %COMMIT%
if errorlevel 1 goto :failed
rem 取り出し元の場所ではなく、公開しているリポジトリを指しておく（どこで作っても同じ状態にする）
for /f "delims=" %%u in ('git remote get-url origin 2^>nul') do git -C "%WORK%" remote set-url origin "%%u"
pushd "%WORK%"

echo [3/7] テストを実行しています...
dotnet test tests\NovelKeeper.Tests --nologo -v q
if errorlevel 1 goto :testfailed

echo [4/7] exe を作成しています（数分かかります）...
rem 通常版：.NET を同梱（インストール不要・約70MB）
dotnet publish NovelKeeper.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o publish
if errorlevel 1 goto :failed
rem 軽量版：.NET を同梱しない（約1MB。.NET 10 デスクトップ ランタイムが必要）
dotnet publish NovelKeeper.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none ^
  -o publish_lite
if errorlevel 1 goto :failed

rem ===== コード署名（任意） =====
rem 証明書がある場合だけ、先に次の環境変数を設定してから実行する：
rem   set SIGN_PFX=C:\path\to\cert.pfx
rem   set SIGN_PASS=パスワード
rem 署名すると、Windows の「PCが保護されました」の警告が出にくくなる（署名しなくても動作は同じ）
rem 署名は sign.ps1（Windows 標準の PowerShell。signtool は不要）が行う
rem 署名すると exe の中身が変わるので、タグから作り直して確かめられるのは、署名する前の exe（署名を外すと同じになる）
if not defined SIGN_PFX goto :nosign
echo 署名しています...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\sign.ps1" "publish\NovelKeeper.exe" "publish_lite\NovelKeeper.exe"
if errorlevel 1 goto :failed
goto :signed
:nosign
echo 署名なし（SIGN_PFX が設定されていないため）
:signed

echo [5/7] 配布用フォルダを作成しています...
if exist "%DIST%" rmdir /s /q "%DIST%"
if exist "%DIST_LITE%" rmdir /s /q "%DIST_LITE%"
mkdir "%DIST%"
mkdir "%DIST_LITE%"
copy /y "publish\NovelKeeper.exe" "%DIST%\" > nul
copy /y "publish_lite\NovelKeeper.exe" "%DIST_LITE%\" > nul
copy /y "docs\使い方.txt" "%DIST%\" > nul
copy /y "docs\使い方.txt" "%DIST_LITE%\" > nul
copy /y "LICENSE" "%DIST%\" > nul
copy /y "LICENSE" "%DIST_LITE%\" > nul
popd
rem zip の中の SHA256.txt は「ファイルが壊れていないか」の確認用。改ざんの確認には、zip の外に置く SHA256SUMS を使う
rem BOM を付けない（付けると sha256sum などで1行目が読めない）
powershell -NoProfile -Command "foreach ($d in '%DIST%','%DIST_LITE%') { $h = (Get-FileHash \"$d\NovelKeeper.exe\" -Algorithm SHA256).Hash; [IO.File]::WriteAllText(\"$d\SHA256.txt\", \"NovelKeeper.exe  SHA256: $h`r`n\", (New-Object Text.UTF8Encoding $false)) }"
if errorlevel 1 goto :failed

echo [6/7] zip にまとめています...
powershell -NoProfile -Command "Compress-Archive -Path '%DIST%\*' -DestinationPath '%ROOT%\dist\NovelKeeper_v%VERSION%.zip' -Force"
if errorlevel 1 goto :failed
powershell -NoProfile -Command "Compress-Archive -Path '%DIST_LITE%\*' -DestinationPath '%ROOT%\dist\NovelKeeper_v%VERSION%_lite.zip' -Force"
if errorlevel 1 goto :failed

rem 配布するときは、この SHA256SUMS を zip とは別の場所（配布ページ・メッセージ本文など）にも載せる
rem zip は作るたびに日時が入って変わるが、中の exe はタグから作り直すと同じになる。そのため exe の値も載せる（sha256sum -c で確かめられる形）
powershell -NoProfile -Command "Set-Location '%ROOT%\dist'; $files = 'NovelKeeper_v%VERSION%.zip','NovelKeeper_v%VERSION%_lite.zip','NovelKeeper_v%VERSION%/NovelKeeper.exe','NovelKeeper_v%VERSION%_lite/NovelKeeper.exe'; $lines = $files | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_ }; [IO.File]::WriteAllText('%ROOT%\dist\SHA256SUMS_v%VERSION%.txt', ($lines -join \"`n\") + \"`n\", (New-Object Text.UTF8Encoding $false))"
if errorlevel 1 goto :failed

echo [7/7] タグ v%VERSION% を付けています...
git tag -a "v%VERSION%" -m "NovelKeeper %VERSION%" %COMMIT%
if errorlevel 1 goto :failed
rmdir /s /q "%WORK%"

echo.
echo 完了しました（コミット %COMMIT:~0,7%・タグ v%VERSION%）：
echo   dist\NovelKeeper_v%VERSION%.zip       （通常版・インストール不要）
echo   dist\NovelKeeper_v%VERSION%_lite.zip  （軽量版・.NET 10 デスクトップ ランタイムが必要）
type "%ROOT%\dist\SHA256SUMS_v%VERSION%.txt"
echo.
echo 公開するときは、コミットとタグを送ってください：
echo   git push origin %BRANCH% v%VERSION%
echo.
pause
exit /b 0

:nosdk
echo global.json で指定した .NET SDK が見つかりません。
type global.json
echo.
echo 指定の版の SDK を入れてから、もう一度実行してください（例：winget install Microsoft.DotNet.SDK.10）。
echo SDK を新しくするときは、global.json の version も書き換えてコミットしてください。
pause
exit /b 1

:dirty
echo コミットしていない変更があります。配布物とタグの中身がずれないよう、先にコミットしてください。
git status --short
pause
exit /b 1

:badversion
echo バージョンは「1.2.3」の形で入力してください。
pause
exit /b 1

:tagexists
echo タグ v%VERSION% はもうあります。別のバージョンを入力してください。
pause
exit /b 1

:testfailed
echo.
echo テストに失敗しました。配布物は作成しません。上のエラーを確認してください。
call :undo
pause
exit /b 1

:failed
echo.
echo ビルドに失敗しました。上のエラーを確認してください。
call :undo
pause
exit /b 1

rem 失敗したときは、バージョンのコミットを取り消す（作業中のファイルには触れない。タグは最後に付けるので、まだない）
:undo
if not "%CD%"=="%ROOT%" popd
set HEADNOW=
for /f "delims=" %%h in ('git rev-parse HEAD') do set HEADNOW=%%h
if defined COMMIT if "%HEADNOW%"=="%COMMIT%" if not "%BASE%"=="%COMMIT%" (
  git reset -q %BASE%
  git checkout -q %BASE% -- version.txt
  echo バージョンのコミットを取り消しました。
)
if not defined COMMIT git checkout -q -- version.txt
if defined WORK if exist "%WORK%" rmdir /s /q "%WORK%"
goto :eof
