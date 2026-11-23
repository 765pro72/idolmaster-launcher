# 终极删除 —— 清除本机所有 gakumas 离线包（含自己）
# 用法: 双击根目录的 终极删除.bat
#       powershell -File purge_all.ps1 -DryRun   只列出，不删除
param([switch]$DryRun, [switch]$OnlySelf)

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Here
$log  = Join-Path $env:TEMP ('gkm_purge_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.log')

function Line([string]$t) {
    Write-Host $t
    if (-not $DryRun) { Add-Content -LiteralPath $log -Value $t -Encoding UTF8 -ErrorAction SilentlyContinue }
}

Line ''
Line '############################################################'
Line '#  学园偶像大师 离线包 —— 终极删除'
Line '#  将删除本机所有 GakumasUnlock 目录（包括当前这个）'
Line '############################################################'
if ($DryRun) { Line '  [DryRun] 只列出，不删除' }
Line ('  当前目录: ' + $Root)
if (-not $DryRun) { Line ('  日志: ' + $log) }
Line ''

# ---------- 1. 杀进程 ----------
Line '[1/4] 结束所有相关进程'
$killed = 0
$procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -eq 'gakumas.exe' -or
    $_.Name -eq 'UnityCrashHandler64.exe' -or
    $_.Name -like '*启动器*' -or
    ($_.Name -eq 'python.exe' -and $_.CommandLine -and
     ($_.CommandLine -like '*cache_server.py*' -or $_.CommandLine -like '*offline_proxy.py*'))
}
foreach ($p in $procs) {
    Line ('   - ' + $p.Name + '  pid=' + $p.ProcessId)
    if (-not $DryRun) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue; $killed++ }
}
if ($procs.Count -eq 0) { Line '   - 无' }
if (-not $DryRun) { Start-Sleep -Seconds 2 }

# ---------- 2. 恢复系统代理 ----------
Line ''
Line '[2/4] 恢复系统代理'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$saved = Join-Path $Here 'proxy.saved.json'
if (Test-Path $saved) {
    try {
        $s = Get-Content -LiteralPath $saved -Raw | ConvertFrom-Json
        Line ('   - 恢复 Enable=' + $s.Enable + ' Server=' + $s.Server)
        if (-not $DryRun) {
            Set-ItemProperty -Path $key -Name ProxyEnable -Value ([int]$s.Enable)
            if ($s.Server) { Set-ItemProperty -Path $key -Name ProxyServer -Value ([string]$s.Server) }
        }
    } catch { Line ('   - 解析失败: ' + $_.Exception.Message) }
} else {
    $cur = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
    if ($cur -and $cur.ProxyServer -eq '127.0.0.1:7899') {
        Line '   - 代理仍指向 7899，已关闭'
        if (-not $DryRun) { Set-ItemProperty -Path $key -Name ProxyEnable -Value 0 }
    } else { Line ('   - 无需处理 (Enable=' + $cur.ProxyEnable + ')') }
}

# ---------- 3. 删除 CA ----------
Line ''
Line '[3/4] 删除本包的 CA 证书'
$found = $false
foreach ($store in @('Cert:\CurrentUser\Root','Cert:\LocalMachine\Root')) {
    Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -like '*GKM*' } |
        ForEach-Object {
            $found = $true
            Line ('   - ' + $store + '  ' + $_.Thumbprint)
            if (-not $DryRun) {
                $scope = if ($store -like '*LocalMachine*') { @() } else { @('-user') }
                & certutil @scope -delstore Root $_.Thumbprint 2>&1 | Out-Null
            }
        }
}
if (-not $found) { Line '   - 无' }

# ---------- 4. 扫描并删除所有包目录 ----------
Line ''
Line '[4/4] 扫描全盘 GakumasUnlock 目录'
$skipRoots = @('C:\Windows','C:\Program Files','C:\Program Files (x86)','C:\ProgramData','$Recycle.Bin')
$foundDirs = @()
$gameDirs  = @()
if ($OnlySelf) {
    Line '  仅清理当前目录（-OnlySelf），不扫描其他盘'
    $foundDirs = @($Root)
} else {
$drives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
          Where-Object { $_.Root -match '^[A-Za-z]:\\$' }
foreach ($d in $drives) {
    Line ('   * 扫描 ' + $d.Root)
    Get-ChildItem -Path $d.Root -Directory -Depth 4 -Filter 'GakumasUnlock' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $full = $_.FullName
            $skip = $false
            foreach ($s in $skipRoots) { if ($full -like ($d.Root + $s + '*')) { $skip = $true } }
            if (-not $skip) {
                # 内容级保护：只要里面有游戏本体特征文件，绝不当成"包"删除
                $isGame = (Test-Path -LiteralPath (Join-Path $full 'gakumas.exe')) -or
                          (Test-Path -LiteralPath (Join-Path $full 'gakumas_Data')) -or
                          (Test-Path -LiteralPath (Join-Path $full 'GameAssembly.dll')) -or
                          (Test-Path -LiteralPath (Join-Path $full 'UnityPlayer.dll'))
                if ($isGame) { $gameDirs += $full } else { $foundDirs += $full }
            }
        }
}
}
$foundDirs = $foundDirs | Sort-Object -Unique
$gameDirs  = $gameDirs  | Sort-Object -Unique

if ($foundDirs.Count -eq 0) {
    Line ''
    Line '  没有找到任何 GakumasUnlock 目录。'
    if (-not $DryRun) { Read-Host '按回车退出' }
    exit 0
}

Line ''
Line ('  共找到 ' + $foundDirs.Count + ' 个目录:')
$self = $Root.TrimEnd('\')
foreach ($f in $foundDirs) {
    $mark = if ($f.TrimEnd('\') -eq $self) { '  <- 当前' } else { '' }
    Line ('    ' + $f + $mark)
}

if ($gameDirs.Count -gt 0) {
    Line ''
    Line ('  已跳过 ' + $gameDirs.Count + ' 个疑似「游戏本体」的目录（内含 gakumas.exe / gakumas_Data，不会删除）:')
    foreach ($g in $gameDirs) { Line ('    x ' + $g) }
}

if ($DryRun) { Line ''; Line '  [DryRun] 结束，未删除。'; exit 0 }

Line ''
Line '############################################################'
Line '#  即将删除以上全部目录，不可恢复。'
Line '#  确认请输入 PURGE（大写），其他任意键取消。'
Line '############################################################'
$ans = Read-Host '  确认'
if ($ans -ne 'PURGE') { Line '  已取消。'; Read-Host '按回车退出'; exit 0 }

# ---------- 先还原游戏目录，再删包 ----------
# 不还原的话，删掉包以后游戏目录里仍是我们的 proxy DLL，
# 服务没了、转发不出去，游戏连在线都玩不了。
$yahaName = 'Cysharp.Net.Http.YetAnotherHttpHandler.Native'
Line ''
Line '  还原游戏目录里被替换的 yaha DLL ...'
$restored = 0
foreach ($pkg in $foundDirs) {
    $gpFile = Join-Path $pkg 'game_path.txt'
    if (-not (Test-Path $gpFile)) { continue }
    $gdir = ''
    try { $gdir = (Get-Content $gpFile -Raw).Trim() } catch { continue }
    if (-not $gdir -or -not (Test-Path $gdir)) { continue }
    $pd = Join-Path $gdir 'gakumas_Data\Plugins\x86_64'
    $liveDll = Join-Path $pd ($yahaName + '.dll')
    $origDll = Join-Path $pd ($yahaName + '.orig.dll')
    $pkgOrig = Join-Path $pkg ('hook\' + $yahaName + '.orig.dll')
    if (-not (Test-Path $liveDll)) { continue }
    $already = $false
    try {
        $hLive = (Get-FileHash $liveDll -Algorithm SHA256).Hash
        $hOrig = if (Test-Path $origDll) { (Get-FileHash $origDll -Algorithm SHA256).Hash } else { '' }
        if ($hOrig -and $hLive -eq $hOrig) { $already = $true }
    } catch { }
    if ($already) { Line ('   - ' + $gdir + ' : 已是原版，跳过'); continue }
    $src = if (Test-Path $origDll) { $origDll } elseif (Test-Path $pkgOrig) { $pkgOrig } else { '' }
    if ($src) {
        try {
            Copy-Item -LiteralPath $src -Destination $liveDll -Force
            if (Test-Path $origDll) { Remove-Item -LiteralPath $origDll -Force -ErrorAction SilentlyContinue }
            Line ('   - ' + $gdir + ' : 已还原原版')
            $restored++
        } catch {
            Line ('   - ' + $gdir + ' : 还原失败 ' + $_.Exception.Message)
        }
    } else {
        Line ('   - ' + $gdir + ' : [警告] 没有原版备份可用，请用 DMMGamePlayer 修复')
    }
}
if ($restored -eq 0) { Line '   - 没有需要还原的（或都不是本包改的）' }

$failed = @()
foreach ($f in $foundDirs) {
    if ($f.TrimEnd('\') -eq $self) { continue }   # 自己最后删
    Line ('  删除 ' + $f)
    try {
        Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction Stop
    } catch {
        # 失败重试一次（句柄可能还没释放）
        Start-Sleep -Milliseconds 800
        try { Remove-Item -LiteralPath $f -Recurse -Force -ErrorAction Stop }
        catch { $failed += ($f + '  ::  ' + $_.Exception.Message) }
    }
}

Line ''
if ($failed.Count -gt 0) {
    # 有目录没删掉：保留本脚本，方便再次运行重试
    Line '  以下目录删除失败（可能仍被进程占用）:'
    foreach ($x in $failed) { Line ('    ! ' + $x) }
    Line ('  详见日志: ' + $log)
    Line ''
    Line '  为了避免你无法重试，本次不删除当前包目录。'
    Line '  请关掉占用它的程序（启动器 / 游戏 / python）后再次运行本脚本。'
    Read-Host '按回车退出'
    exit 0
}

Line '  删除当前目录（本脚本所在）...'
$cmd = 'timeout /t 2 >nul & rd /s /q "' + $Root + '"'
Start-Process -FilePath 'cmd.exe' -ArgumentList @('/c', $cmd) -WindowStyle Hidden
Line '  全部清理完成（包含本目录）。'
exit 0
