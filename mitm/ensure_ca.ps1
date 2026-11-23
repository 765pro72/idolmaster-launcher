# Ensure the local MITM CA is trusted for the current user (no admin needed).
# UTF-8 with BOM: PS 5.1 reads BOM-less UTF-8 as GBK and chokes on CJK.
if ($env:GKM_SKIP_CA -eq '1') {
    Write-Host '       [CA] 已按 GKM_SKIP_CA=1 跳过证书安装'
    exit 0
}
$Here = Split-Path -Parent $PSCommandPath
$ca = Join-Path $Here 'certs\ca.pem'
if (-not (Test-Path -LiteralPath $ca)) {
    Write-Host '       [CA] certs\ca.pem 不存在，无法安装证书' -ForegroundColor Red
    exit 1
}

# GKM_CA_EXACT_20261114
$pem = [IO.File]::ReadAllText($ca)
$match = [regex]::Match($pem,'-----BEGIN CERTIFICATE-----(.*?)-----END CERTIFICATE-----',[Text.RegularExpressions.RegexOptions]::Singleline)
if (-not $match.Success) { throw 'Invalid bundled CA certificate' }
$expected = New-Object Security.Cryptography.X509Certificates.X509Certificate2(,[Convert]::FromBase64String(($match.Groups[1].Value -replace '\s','')))
if ($expected.NotBefore -gt (Get-Date) -or $expected.NotAfter -lt (Get-Date)) { throw 'Bundled CA is outside its validity period; check system time' }
# 注意：实际证书 Subject 是 'CN=GKM Local CA 2, O=GKM Local MITM, C=JP'
# 旧代码用 -eq 'CN=GKM Local CA 2' 精确匹配，永远不命中 -> 每次启动都重装
# Query the actual Windows store; nested PS 5.1 can lack the Cert: provider
# when PSModulePath was inherited from another PowerShell distribution.
function Test-ExactCaTrusted {
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $expected.Thumbprint, $false)
        return ($matches.Count -gt 0)
    } finally { $store.Close() }
}
if (Test-ExactCaTrusted) { exit 0 }

Write-Host '       正在安装本地 CA 证书（会弹 Windows 安全警告，请点「是」）...'
$out = & certutil.exe -addstore -user Root $ca 2>&1
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Host ('       [CA] 安装失败 (exit ' + $code + ')：') -ForegroundColor Red
    $out | Select-Object -Last 6 | ForEach-Object { Write-Host ('         ' + $_) }
    Write-Host '       可以手动双击 certs\ca.pem -> 安装证书 -> 当前用户 -> 受信任的根证书'
    exit 1
}

# 复查一次，确保真的装上了
if (-not (Test-ExactCaTrusted)) {
    Write-Host '       [CA] certutil 返回成功，但证书不在根证书库里' -ForegroundColor Red
    exit 1
}
Write-Host '       [CA] 证书已装好'
exit 0
