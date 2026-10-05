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
set DIST=dist\NovelKeeper_v%VERSION%

echo [1/4] 古いビルド結果を削除しています...
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%DIST%" rmdir /s /q "%DIST%"

echo [2/4] exe を作成しています（数分かかります）...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -p:Version=%VERSION% ^
  -o "%OUT%"
if errorlevel 1 (
  echo.
  echo ビルドに失敗しました。上のエラーを確認してください。
  pause
  exit /b 1
)

echo [3/4] 配布用フォルダを作成しています...
mkdir "%DIST%"
copy /y "%OUT%\NovelKeeper.exe" "%DIST%\" > nul
copy /y "docs\使い方.txt" "%DIST%\" > nul
powershell -NoProfile -Command "$h = (Get-FileHash '%DIST%\NovelKeeper.exe' -Algorithm SHA256).Hash; Set-Content -Path '%DIST%\SHA256.txt' -Value ('NovelKeeper.exe  SHA256: ' + $h) -Encoding UTF8"

echo [4/4] zip にまとめています...
powershell -NoProfile -Command "Compress-Archive -Path '%DIST%\*' -DestinationPath 'dist\NovelKeeper_v%VERSION%.zip' -Force"

rem 成功したら、今回のバージョンを覚えておく（次回はこれを元に提案する）
(echo %VERSION%)>version.txt

echo.
echo 完了しました： dist\NovelKeeper_v%VERSION%.zip
type "%DIST%\SHA256.txt"
echo.
pause
