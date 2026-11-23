# 一键诊断 —— 收集排查所需信息，输出到包根目录的 诊断报告.txt
# 双击根目录的 诊断.bat 即可，不需要管理员权限
$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $PSCommandPath      # ...\mitm
$Root = Split-Path -Parent $Here               # 包根
$out  = Join-Path $Root '诊断报告.txt'
$L = New-Object System.Collections.ArrayList

function Add([string]$s) { [void]$L.Add($s) }
function Head([string]$t) { Add ''; Add ('=== ' + $t + ' ===') }

Head '基本信息'
Add ('时间      : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add ('计算机    : ' + $env:COMPUTERNAME)
Add ('用户      : ' + $env:USERNAME)
Add ('系统      : ' + (Get-CimInstance Win32_OperatingSystem).Caption + '  ' + [Environment]::OSVersion.Version)
Add ('64位进程  : ' + [Environment]::Is64BitProcess)
Add ('包根目录  : ' + $Root)

Head '关键文件（大小 + 哈希前 12 位）'
$keys = @(
  '学マス启动器.exe',
  'hook\yaha_proxy.dll',
  'hook\Cysharp.Net.Http.YetAnotherHttpHandler.Native.orig.dll',
  'hook\gkm_inject.exe',
  'mitm\launch_game.ps1',
  'mitm\ensure_ca.ps1',
  'mitm\offline_proxy.py',
  'mitm\cache_server.py',
  'mitm\purge_all.ps1',
  'mitm\uninstall.ps1'
)
foreach ($k in $keys) {
    $p = Join-Path $Root $k
    if (Test-Path -LiteralPath $p) {
        $fi = Get-Item -LiteralPath $p
        $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.Substring(0,12)
        Add ('OK    {0,-64} {1,9} B  {2}' -f $k, $fi.Length, $h)
    } else {
        Add ('缺失  ' + $k)
    }
}

Head 'hook 目录实际内容'
$hk = Join-Path $Root 'hook'
if (Test-Path $hk) {
    Get-ChildItem $hk -File | ForEach-Object { Add ('  {0,-64} {1,9} B' -f $_.Name, $_.Length) }
} else { Add '  hook 目录不存在' }

Head '游戏目录完整性'
$gp = Join-Path $Root 'game_path.txt'
if (Test-Path $gp) {
    $gdir = (Get-Content $gp -Raw).Trim()
    Add ('game_path.txt : ' + $gdir)
    if (Test-Path $gdir) {
        foreach ($f in @('gakumas.exe','gakumas.exe._','version.dll','baselib.dll','GameAssembly.dll','UnityPlayer.dll')) {
            $p = Join-Path $gdir $f
            if (Test-Path $p) {
                Add ('    {0,-22} {1,12} B' -f $f, (Get-Item $p).Length)
            } else {
                Add ('    {0,-22} 缺失  <<<<<<' -f $f)
            }
        }
        foreach ($d in @('gakumas_Data','gakumas_Data\il2cpp_data\Metadata','gakumas_Data\StreamingAssets')) {
            $p = Join-Path $gdir $d
            Add ('    {0,-40} {1}' -f $d, $(if (Test-Path $p) { '存在' } else { '缺失  <<<<<<' }))
        }
        $pd = Join-Path $gdir 'gakumas_Data\Plugins\x86_64'
        Add ('  Plugins 目录: ' + $pd)
        if (Test-Path $pd) {
            foreach ($f in @('Cysharp.Net.Http.YetAnotherHttpHandler.Native.dll','Cysharp.Net.Http.YetAnotherHttpHandler.Native.orig.dll')) {
                $p = Join-Path $pd $f
                if (Test-Path $p) {
                    Add ('    {0,-58} {1,9} B  {2}' -f $f, (Get-Item $p).Length, (Get-FileHash $p -Algorithm SHA256).Hash.Substring(0,12))
                } else { Add ('    缺失 ' + $f) }
            }
            $yl = Join-Path $pd 'yaha-proxy.log'
            if (Test-Path $yl) { Add ('    yaha-proxy.log 大小: ' + (Get-Item $yl).Length + ' B，最后写入: ' + (Get-Item $yl).LastWriteTime) }
        }
    } else { Add '  游戏目录不存在（路径失效）' }
} else { Add '  没有 game_path.txt' }

Head 'DMMGamePlayer 安装痕迹（判断游戏是否被正常初始化过）'
$dmmCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'DMMGamePlayer'),
    (Join-Path $env:APPDATA 'DMMGamePlayer'),
    'C:\Program Files\DMMGamePlayer',
    'C:\Program Files (x86)\DMMGamePlayer'
)
$hitDmm = $false
foreach ($d in $dmmCandidates) {
    if (Test-Path $d) { Add ('  存在: ' + $d); $hitDmm = $true }
}
if (-not $hitDmm) { Add '  未发现 DMMGamePlayer 安装痕迹  <<<<<< 若游戏从未通过官方客户端启动过，DRM 可能未初始化' }
$regDmm = 'HKCU:\Software\DMM'
if (Test-Path $regDmm) { Add ('  注册表存在: ' + $regDmm) } else { Add '  注册表 HKCU\Software\DMM 不存在' }

Head '最近的应用程序错误（gakumas 崩溃记录）'
try {
    $evts = Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-7)} -MaxEvents 400 -ErrorAction Stop
    $hit = $evts | Where-Object { $_.Message -match 'gakumas' -or $_.ProviderName -match 'Application Error|\.NET Runtime|Windows Error Reporting' }
    if ($hit) {
        $n = 0
        foreach ($e in $hit) {
            if ($n -ge 8) { break }
            Add ('  [' + $e.TimeCreated + '] ' + $e.ProviderName + ' id=' + $e.Id)
            $msg = ($e.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6) -join ' | '
            Add ('    ' + $msg)
            $n++
        }
    } else { Add '  最近 7 天没有 gakumas 相关崩溃记录' }
} catch { Add ('  读取事件日志失败: ' + $_.Exception.Message) }

Head '系统区域设置（日文游戏的常见坑）'
try {
    $ci = Get-Culture
    Add ('  Culture = ' + $ci.Name + '   系统语言 = ' + (Get-WinSystemLocale).Name)
    Add ('  非 Unicode 程序语言 = ' + (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\Language' -ErrorAction SilentlyContinue).Default)
} catch { Add '  读取失败' }

Head '杀毒软件'
try {
    $av = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop
    if ($av) { foreach ($a in $av) { Add ('  ' + $a.displayName + '   state=' + $a.productState) } }
    else { Add '  未注册杀毒产品' }
} catch { Add ('  读取失败: ' + $_.Exception.Message) }

Head 'CA 证书'
$certs = Get-ChildItem Cert:\CurrentUser\Root -ErrorAction SilentlyContinue | Where-Object { $_.Subject -like '*GKM*' }
if ($certs) {
    foreach ($c in $certs) {
        Add ('  已安装  ' + $c.Subject)
        Add ('          Thumbprint=' + $c.Thumbprint)
        Add ('          有效期至=' + $c.NotAfter)
    }
} else { Add '  当前用户根存储里没有 GKM Local CA' }
$lm = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue | Where-Object { $_.Subject -like '*GKM*' }
Add ('  机器级存储: ' + $lm.Count + ' 个')

Head '系统代理'
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$pr = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
Add ('  ProxyEnable = ' + $pr.ProxyEnable)
Add ('  ProxyServer = ' + $pr.ProxyServer)
$sv = Join-Path $Here 'proxy.saved.json'
if (Test-Path $sv) { Add ('  proxy.saved.json = ' + (Get-Content $sv -Raw)) } else { Add '  proxy.saved.json 不存在' }

Head '端口监听'
foreach ($p in @(7899,18443,18444)) {
    $conn = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    if ($conn) {
        $proc = Get-Process -Id $conn[0].OwningProcess -ErrorAction SilentlyContinue
        Add ('  {0}: LISTEN  (pid {1} {2})' -f $p, $conn[0].OwningProcess, $proc.ProcessName)
    } else { Add ('  {0}: 未监听' -f $p) }
}

Head '相关进程'
$ps = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -eq 'gakumas.exe' -or $_.Name -like '*启动器*' -or
    ($_.Name -eq 'python.exe' -and $_.CommandLine -match 'cache_server|offline_proxy')
}
if ($ps) {
    foreach ($p in $ps) { Add ('  pid={0,-6} {1,-24} {2}' -f $p.ProcessId, $p.Name, $p.CommandLine) }
} else { Add '  没有相关进程' }

Head 'mitm.log 最后 40 行'
$ml = Join-Path $Here 'mitm.log'
if (Test-Path $ml) {
    Get-Content $ml -Tail 40 | ForEach-Object { Add ('  ' + $_) }
} else { Add '  mitm.log 不存在' }

Head 'yaha-proxy.log 最后 30 行'
if (Test-Path $gp) {
    $yl = Join-Path (Get-Content $gp -Raw).Trim() 'gakumas_Data\Plugins\x86_64\yaha-proxy.log'
    if (Test-Path $yl) {
        Get-Content $yl -Tail 30 | ForEach-Object { Add ('  ' + $_) }
    } else { Add '  yaha-proxy.log 不存在（说明 yaha 代理没被加载）' }
}

Head 'offline_proxy 日志最后 30 行（含 TLS/SYNTH 判定）'
$po = Join-Path $Here 'proxy_out\mitm_capture.log'
if (Test-Path $po) {
    Get-Content $po -Tail 30 | ForEach-Object { Add ('  ' + $_) }
} else { Add '  proxy_out\mitm_capture.log 不存在' }

Head '启动器崩溃记录'
$pc = Join-Path $Root 'panel_crash.txt'
if (Test-Path $pc) {
    Add ('  存在，最后写入 ' + (Get-Item $pc).LastWriteTime)
    Get-Content $pc -Tail 15 | ForEach-Object { Add ('  ' + $_) }
} else { Add '  无' }

$L | Out-File -LiteralPath $out -Encoding UTF8
Write-Host ''
Write-Host '============================================'
Write-Host '  诊断完成，报告已生成:'
Write-Host ('  ' + $out)
Write-Host '============================================'
Write-Host ''
Write-Host '  请把这个文件发给对方。'
Write-Host ''
Read-Host '按回车退出'
