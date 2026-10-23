@echo off
chcp 65001 > nul
setlocal
cd /d "%~dp0"

rem ===== バージョン =====
rem version.txt に前回のバージョンが入っている。今回は、その最後の数字を1つ上げたものを提案する。
if not exist version.txt (echo 1.0.0)>version.txt
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v=[version](Get-Content 'version.txt' -TotalCount 1).Trim(); '{0}.{1}.{2}' -f $v.Major,$v.Minor,($v.Build+1)"`) do set NEXT=%%v
for /f "usebackq delims=" %%v in ("version.txt") do set PREV=%%v

echo 前回のバージョン： %PREV%
set VERSION=
set /p VERSION=今回のバージョンを入力してください（そのまま Enter で %NEXT%）：
if "%VERSION%"=="" set VERSION=%NEXT%
echo 今回のバージョン： %VERSION%
echo.

set OUT=publish
set OUT_LITE=publish_lite
set DIST=dist\NovelKeeper_v%VERSION%
set DIST_LITE=dist\NovelKeeper_v%VERSION%_lite

echo [1/6] テストを実行しています...
dotnet test tests\NovelKeeper.Tests --nologo -v q
if errorlevel 1 (
  echo.
  echo テストに失敗しました。配布物は作成しません。上のエラーを確認してください。
  pause
  exit /b 1
)

echo [2/6] 古いビルド結果を削除しています...
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT_LITE%" rmdir /s /q "%OUT_LITE%"
if exist "%DIST%" rmdir /s /q "%DIST%"
if exist "%DIST_LITE%" rmdir /s /q "%DIST_LITE%"

echo [3/6] exe を作成しています（数分かかります）...
rem 通常版：.NET を同梱（インストール不要・約70MB）
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -p:Version=%VERSION% ^
  -o "%OUT%"
if errorlevel 1 goto :failed
rem 軽量版：.NET を同梱しない（約1MB。.NET 10 デスクトップ ランタイムが必要）
dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none ^
  -p:Version=%VERSION% ^
  -o "%OUT_LITE%"
if errorlevel 1 goto :failed

rem ===== コード署名（任意） =====
rem 証明書がある場合だけ、先に次の環境変数を設定してから実行する：
rem   set SIGN_PFX=C:\path\to\cert.pfx
rem   set SIGN_PASS=パスワード
rem 署名すると、Windows の「PCが保護されました」の警告が出にくくなる（署名しなくても動作は同じ）
rem 署名は sign.ps1（Windows 標準の PowerShell。signtool は不要）が行う
if not defined SIGN_PFX goto :nosign
echo 署名しています...
powershell -NoProfile -ExecutionPolicy Bypass -File sign.ps1 "%OUT%\NovelKeeper.exe" "%OUT_LITE%\NovelKeeper.exe"
if errorlevel 1 goto :failed
goto :signed
:nosign
echo 署名なし（SIGN_PFX が設定されていないため）
:signed

echo [4/6] 配布用フォルダを作成しています...
mkdir "%DIST%"
mkdir "%DIST_LITE%"
copy /y "%OUT%\NovelKeeper.exe" "%DIST%\" > nul
copy /y "%OUT_LITE%\NovelKeeper.exe" "%DIST_LITE%\" > nul
copy /y "docs\使い方.txt" "%DIST%\" > nul
copy /y "docs\使い方.txt" "%DIST_LITE%\" > nul
copy /y "LICENSE" "%DIST%\" > nul
copy /y "LICENSE" "%DIST_LITE%\" > nul
rem zip の中の SHA256.txt は「ファイルが壊れていないか」の確認用。改ざんの確認には、zip の外に置く SHA256SUMS を使う
powershell -NoProfile -Command "$h = (Get-FileHash '%DIST%\NovelKeeper.exe' -Algorithm SHA256).Hash; Set-Content -Path '%DIST%\SHA256.txt' -Value ('NovelKeeper.exe  SHA256: ' + $h) -Encoding UTF8"
powershell -NoProfile -Command "$h = (Get-FileHash '%DIST_LITE%\NovelKeeper.exe' -Algorithm SHA256).Hash; Set-Content -Path '%DIST_LITE%\SHA256.txt' -Value ('NovelKeeper.exe  SHA256: ' + $h) -Encoding UTF8"

echo [5/6] zip にまとめています...
powershell -NoProfile -Command "Compress-Archive -Path '%DIST%\*' -DestinationPath 'dist\NovelKeeper_v%VERSION%.zip' -Force"
powershell -NoProfile -Command "Compress-Archive -Path '%DIST_LITE%\*' -DestinationPath 'dist\NovelKeeper_v%VERSION%_lite.zip' -Force"

echo [6/6] zip の SHA256 を書き出しています...
rem 配布するときは、この SHA256SUMS を zip とは別の場所（配布ページ・メッセージ本文など）にも載せる
powershell -NoProfile -Command "Get-FileHash 'dist\NovelKeeper_v%VERSION%.zip','dist\NovelKeeper_v%VERSION%_lite.zip' -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + (Split-Path $_.Path -Leaf) } | Set-Content -Path 'dist\SHA256SUMS_v%VERSION%.txt' -Encoding UTF8"

rem 成功したら、今回のバージョンを覚えておく（次回はこれを元に提案する）
(echo %VERSION%)>version.txt

echo.
echo 完了しました：
echo   dist\NovelKeeper_v%VERSION%.zip       （通常版・インストール不要）
echo   dist\NovelKeeper_v%VERSION%_lite.zip  （軽量版・.NET 10 デスクトップ ランタイムが必要）
type "dist\SHA256SUMS_v%VERSION%.txt"
echo.
pause
exit /b 0

:failed
echo.
echo ビルドに失敗しました。上のエラーを確認してください。
pause
exit /b 1
