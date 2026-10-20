# exe にコード署名をする（build.bat から呼ばれる）。Windows 標準の PowerShell だけで動き、signtool（Windows SDK）は不要。
#
# 使うもの（環境変数）：
#   SIGN_PFX        署名用の証明書（.pfx）のパス（必須）
#   SIGN_PASS       .pfx のパスワード（なければ未設定でよい）
#   SIGN_TIMESTAMP  タイムスタンプサーバーの URL。未設定なら http://timestamp.digicert.com。
#                   none にすると付けない（付けないと、証明書の期限が切れた時点で署名が無効になるので、通常は付ける）
param([Parameter(Mandatory)][string[]]$Path)

$ErrorActionPreference = 'Stop'

$pfx = $env:SIGN_PFX
if (-not $pfx -or -not (Test-Path -LiteralPath $pfx)) { throw "SIGN_PFX の証明書が見つかりません: $pfx" }
$timestamp = if ($env:SIGN_TIMESTAMP -eq 'none') { $null } elseif ($env:SIGN_TIMESTAMP) { $env:SIGN_TIMESTAMP } else { 'http://timestamp.digicert.com' }

$password = if ($env:SIGN_PASS) { ConvertTo-SecureString $env:SIGN_PASS -AsPlainText -Force } else { $null }
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $pfx).Path, $password)
if (-not $cert.HasPrivateKey) { throw '証明書に秘密鍵が含まれていません。' }

foreach ($file in $Path) {
    $params = @{ FilePath = $file; Certificate = $cert; HashAlgorithm = 'SHA256' }
    if ($timestamp) { $params.TimestampServer = $timestamp }
    $result = Set-AuthenticodeSignature @params
    # 署名が付いたかを確かめる（自己署名などで「信頼されていない」状態でも、署名自体は付いている）
    if (-not $result.SignerCertificate) { throw "署名に失敗しました: $file ($($result.StatusMessage))" }
    Write-Host "署名しました: $file（$($result.Status)）"
}
