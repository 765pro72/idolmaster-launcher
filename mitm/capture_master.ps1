# capture_master.ps1 —— 抓取官方最新的 Master/Get 响应（含新的 CryptoKey）
#
# 安全顺序（B 方案）：
#   1. 旧缓存【移动】到 pending 目录（不删除）
#   2. hybrid 启动游戏，抓新响应
#   3. 校验新响应（大小 / JSON / 表数量）
#   4. 校验通过 → 删除 pending（旧缓存正式作废）
#      校验失败 → 把 pending 移回来（旧缓存原样恢复）
#
# 关键：任何一步失败，旧缓存都会自动恢复，不会出现「旧没了、新也没来」。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File capture_master.ps1
#   powershell ... -File capture_master.ps1 -NoSync
#   powershell ... -File capture_master.ps1 -WaitSec 90

param(
    [switch]$NoSync,
    [int]$WaitSec = 40
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$Py        = Join-Path $Root 'python\python.exe'
$Server    = Join-Path $Here 'cache_server.py'
$Sync      = Join-Path $Root 'tools\local_content_sync.py'
$RecordDir = Join-Path $Here 'records'
$CacheDir  = Join-Path $Here 'cache\client.api.Master_Get'
$Stamp     = Get-Date -Format 'yyyyMMdd-HHmmss'
$Pending   = Join-Path $Here "master-capture-pending-$Stamp"
$RecFile   = Join-Path $RecordDir '00001_client.api.Master_Get.json'

function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

function Move-OldOut {
    $n = 0
    New-Item -ItemType Directory -Force -Path $Pending | Out-Null
    if (Test-Path -LiteralPath $RecFile) {
        Move-Item $RecFile (Join-Path $Pending 'records_Master_Get.json') -Force
        $n++
    }
    if (Test-Path -LiteralPath $CacheDir) {
        $sub = Join-Path $Pending 'cache'
        New-Item -ItemType Directory -Force -Path $sub | Out-Null
        Get-ChildItem $CacheDir -File -ErrorAction SilentlyContinue | ForEach-Object {
            Move-Item $_.FullName (Join-Path $sub $_.Name) -Force
            $n++
        }
    }
    return $n
}

function Restore-Old {
    $n = 0
    $r = Join-Path $Pending 'records_Master_Get.json'
    if (Test-Path -LiteralPath $r) {
        New-Item -ItemType Directory -Force -Path $RecordDir | Out-Null
        Move-Item $r $RecFile -Force
        $n++
    }
    $c = Join-Path $Pending 'cache'
    if (Test-Path -LiteralPath $c) {
        New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
        Get-ChildItem $c -File -ErrorAction SilentlyContinue | ForEach-Object {
            Move-Item $_.FullName (Join-Path $CacheDir $_.Name) -Force
            $n++
        }
    }
    return $n
}

function Test-NewResponse {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return @{ ok = $false; why = '文件不存在' } }
    $len = (Get-Item $Path).Length
    if ($len -lt 50000)     { return @{ ok = $false; why = "太小（$len B < 50000）" } }
    if ($len -gt 5000000)   { return @{ ok = $false; why = "太大（$len B）" } }
    $j = $null
    try {
        $txt = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
        $j = $txt | ConvertFrom-Json
    } catch {
        return @{ ok = $false; why = 'JSON 解析失败' }
    }
    $tables = $null
    if ($j.tables) { $tables = @($j.tables).Count }
    elseif ($j.data -and $j.data.tables) { $tables = @($j.data.tables).Count }
    if (-not $tables -or $tables -lt 100) {
        return @{ ok = $false; why = "表数量异常（$tables）" }
    }
    return @{ ok = $true; why = "OK（$([math]::Round($len/1KB,1)) KB, $tables 表）" }
}

Say ''
Say '=== 抓取官方最新 Master/Get（安全模式）===' Cyan
Say ''

# ---------- 1 ----------
Say '[1/6] 从 DMM 日志取真实 token ...'
$dmmLog = Join-Path $env:APPDATA 'dmmgameplayer5\logs\dll.log'
if (-not (Test-Path -LiteralPath $dmmLog)) {
    Say ('  找不到 DMM 日志: ' + $dmmLog) Red
    Say '  请先用 DMMGamePlayer 正常启动一次游戏。' Yellow
    exit 1
}
$hits = Select-String -Path $dmmLog -Pattern 'Execute of:: gakumas exe' -ErrorAction SilentlyContinue
if (-not $hits) { Say '  DMM 日志里没有 gakumas 启动记录。' Red; exit 1 }
$line = ($hits | Select-Object -Last 1).Line
$exePath = $null; $viewer = $null; $onetime = $null; $openId = $null; $pfToken = $null
$m = [regex]::Match($line, 'exe:\s*(?<p>.*?gakumas\.exe)');  if ($m.Success) { $exePath = $m.Groups['p'].Value.Trim() }
$m = [regex]::Match($line, '/viewer_id=(?<v>[^\s]+)');        if ($m.Success) { $viewer  = $m.Groups['v'].Value }
$m = [regex]::Match($line, '/onetime_token=(?<v>[^\s]+)');    if ($m.Success) { $onetime = $m.Groups['v'].Value }
$m = [regex]::Match($line, '/open_id=(?<v>[^\s]+)');          if ($m.Success) { $openId  = $m.Groups['v'].Value }
$m = [regex]::Match($line, '/pf_access_token=(?<v>[^\s]+)');  if ($m.Success) { $pfToken = $m.Groups['v'].Value }
if (-not $exePath -or -not $viewer) { Say '  日志里没解析到 exe 或 viewer_id。' Red; exit 1 }
Say ("  exe: " + $exePath) Green
Say ("  viewer_id: " + $viewer) Green

# ---------- 2 ----------
Say ''
Say '[2/6] 停掉本地服务 ...'
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'python.exe' -and $_.CommandLine -match 'cache_server|offline_proxy' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2
Say '  已停' Green

# ---------- 3 ----------
Say ''
Say '[3/6] 旧缓存移到 pending（不删除，失败可恢复）...'
$moved = 0
try { $moved = Move-OldOut } catch { Say ('  移动失败: ' + $_.Exception.Message) Red }
Say ("  已移走 " + $moved + " 个文件 -> " + $Pending) Green

# ---------- 4 ----------
Say ''
Say '[4/6] 以 hybrid 模式启动服务 ...'
$env:GKM_MODE           = 'hybrid'
$env:GKM_MATCH          = 'exact'
$env:GKM_STATIC_USER    = '1'
$env:GKM_MITM_PORT      = '18443'
$env:GKM_H2C_PORT       = '18444'
# 上游端口不写死：优先沿用启动前保存的真实系统代理，其次读注册表，最后才退回 7897。
$upstreamProxy = $null
$savedProxy = Join-Path $Here 'proxy.saved.json'
if (Test-Path -LiteralPath $savedProxy) {
    try {
        $savedInfo = Get-Content -LiteralPath $savedProxy -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($savedInfo.Server) { $upstreamProxy = [string]$savedInfo.Server }
    } catch { }
}
if (-not $upstreamProxy) {
    try {
        $ie = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop
        if ($ie.ProxyServer) { $upstreamProxy = [string]$ie.ProxyServer }
    } catch { }
}
if (-not $upstreamProxy) { $upstreamProxy = '127.0.0.1:7897' }
$env:GKM_UPSTREAM_PROXY = $upstreamProxy
Say ('  上游代理: ' + $upstreamProxy) Gray
$env:PYTHONIOENCODING   = 'utf-8'
$env:PYTHONUNBUFFERED   = '1'
$svc = Start-Process -FilePath $Py -ArgumentList '-B', $Server -WorkingDirectory $Here -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 6
$up = Get-NetTCPConnection -LocalPort 18443 -State Listen -ErrorAction SilentlyContinue
if (-not $up) {
    Say '  服务没起来，恢复旧缓存并退出。' Red
    if (-not $svc.HasExited) { Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue }
    $r = Restore-Old
    Remove-Item $Pending -Recurse -Force -ErrorAction SilentlyContinue
    Say ('  已恢复 ' + $r + ' 个旧文件') Green
    exit 3
}
Say '  服务已就绪 (18443, hybrid)' Green

# ---------- 5 ----------
Say ''
Say '[5/6] 启动游戏（真实 token），等它请求 Master/Get ...'
$gameArgs = @("/viewer_id=$viewer")
if ($onetime) { $gameArgs += "/onetime_token=$onetime" }
if ($openId)  { $gameArgs += "/open_id=$openId" }
if ($pfToken) { $gameArgs += "/pf_access_token=$pfToken" }

$gp = $null
try {
    $gp = Start-Process -FilePath $exePath -ArgumentList $gameArgs -WorkingDirectory (Split-Path -Parent $exePath) -PassThru -ErrorAction Stop
    Say ("  游戏 pid=" + $gp.Id + "，最多等 " + $WaitSec + " 秒 ...") Green
} catch {
    Say ('  启动游戏失败: ' + $_.Exception.Message) Red
}

$found = $false
if ($gp) {
    for ($i = 0; $i -lt $WaitSec; $i++) {
        Start-Sleep -Seconds 1
        if (Test-Path -LiteralPath $RecFile) { $found = $true; break }
        if ($gp.HasExited) { Say '  游戏提前退出' Yellow; break }
        if ($i % 10 -eq 9) { Say ("  ...已等 " + ($i+1) + " 秒") DarkGray }
    }
}

# ---------- 6 ----------
Say ''
Say '[6/6] 校验新响应 ...'
$verdict = @{ ok = $false; why = '未抓到' }
if ($found) { $verdict = Test-NewResponse -Path $RecFile }
Say ('  ' + $verdict.why) $(if ($verdict.ok) { 'Green' } else { 'Yellow' })

if ($gp -and -not $gp.HasExited) { try { $gp.CloseMainWindow() | Out-Null } catch { } }
Start-Sleep -Seconds 3
Get-Process gakumas -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (-not $svc.HasExited) { Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

if ($verdict.ok) {
    Remove-Item $Pending -Recurse -Force -ErrorAction SilentlyContinue
    Say '  新表有效，旧缓存已作废' Green
} else {
    if (Test-Path -LiteralPath $RecFile) { Remove-Item $RecFile -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $CacheDir) {
        Get-ChildItem $CacheDir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
    $r = Restore-Old
    Remove-Item $Pending -Recurse -Force -ErrorAction SilentlyContinue
    Say ('  抓取失败，已恢复 ' + $r + ' 个旧文件（和之前一模一样）') Yellow
    exit 4
}

if (-not $NoSync) {
    Say ''
    Say '=== 用新表同步 ===' Cyan
    & $Py -B $Sync --root $Root
    exit $LASTEXITCODE
}

Say ''
Say '抓取完成（未同步）。' Green
exit 0
