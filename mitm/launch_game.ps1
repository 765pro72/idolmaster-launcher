# Launch gakumas.exe directly, bypassing DMMGamePlayer.
#  -Capture  : read the running game's command line and save it to launch-args.json
#  -Official : 官方账号模式。从 DMMGamePlayer 自己的日志里取出上次启动用的
#              viewer_id / open_id / pf_access_token，直接用它们启动游戏，
#              完全不碰代理、证书和本地服务 —— 等价于跳过 DMM 登录界面。
#  (default) : start the game using the saved launch-args.json
param(
    [switch]$Capture,
    [switch]$NoOfflineProxy,
    [switch]$OnlineProxy,
    [switch]$SkipCA,
    [switch]$Official,
    [string]$UpdateReceiptPath
)

# Update cleanup records contain process identity only, never login arguments.
function Save-UpdateProcess($processId) {
    if (-not $UpdateReceiptPath) { return }
    $receiptBase = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\out')) + '\'
    if (-not [IO.Path]::GetFullPath($UpdateReceiptPath).StartsWith($receiptBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid update receipt path.'
    }
    $process = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $processId) -ErrorAction SilentlyContinue
    if ($process) {
        @{pid=[int]$process.ProcessId; created=$process.CreationDate.ToUniversalTime().ToString('o'); exe=$process.ExecutablePath} |
            ConvertTo-Json -Compress | Add-Content -LiteralPath $UpdateReceiptPath -Encoding UTF8
    }
}

# Normalize inherited environment names before any Start-Process or Env: access.
# Repairs duplicated NO_PROXY/no_proxy without changing their effective value.
if (-not ('GkmEnvironmentRepair' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Runs before ProcessStartInfo.EnvironmentVariables is first accessed.
// Reads the raw Win32 environment block because case-insensitive managed maps
// can throw while importing duplicated NO_PROXY/no_proxy entries.
public static class GkmEnvironmentRepair
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern IntPtr GetEnvironmentStringsW();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
    static extern bool FreeEnvironmentStringsW(IntPtr block);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetEnvironmentVariableW(string name, string value);

    public static List<KeyValuePair<string,string>> Snapshot()
    {
        IntPtr block=GetEnvironmentStringsW();
        if(block==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        List<KeyValuePair<string,string>> rows=new List<KeyValuePair<string,string>>();
        try
        {
            int offset=0;
            while(true)
            {
                string row=Marshal.PtrToStringUni(IntPtr.Add(block,offset));
                if(String.IsNullOrEmpty(row))break;
                offset+=(row.Length+1)*2;
                int split=row.IndexOf('=');
                // Preserve special Win32 =C: working-directory entries.
                if(split<=0)continue;
                rows.Add(new KeyValuePair<string,string>(row.Substring(0,split),row.Substring(split+1)));
            }
        }
        finally{FreeEnvironmentStringsW(block);}
        return rows;
    }

    public static int Repair()
    {
        Dictionary<string,List<KeyValuePair<string,string>>> groups=new Dictionary<string,List<KeyValuePair<string,string>>>(StringComparer.OrdinalIgnoreCase);
        foreach(KeyValuePair<string,string> row in Snapshot())
        {
            List<KeyValuePair<string,string>> group;
            if(!groups.TryGetValue(row.Key,out group)){group=new List<KeyValuePair<string,string>>();groups.Add(row.Key,group);}
            group.Add(row);
        }
        int changed=0;
        foreach(List<KeyValuePair<string,string>> group in groups.Values)
        {
            if(group.Count<2)continue;
            KeyValuePair<string,string> chosen=group[0];
            foreach(KeyValuePair<string,string> row in group)
                if(row.Key==row.Key.ToUpperInvariant()){chosen=row;break;}
            // A Win32 delete can remove only one occurrence from a malformed
            // block. Delete once for each raw occurrence, then retain one.
            foreach(KeyValuePair<string,string> row in group)
                if(!SetEnvironmentVariableW(row.Key,null))throw new System.ComponentModel.Win32Exception();
            if(!SetEnvironmentVariableW(chosen.Key,chosen.Value))throw new System.ComponentModel.Win32Exception();
            changed++;
        }
        return changed;
    }
}

"@
}
[void][GkmEnvironmentRepair]::Repair()
# Use the bundled interpreter's own libraries, independent of global Python setup.
[Environment]::SetEnvironmentVariable('PYTHONHOME', $null, 'Process')
[Environment]::SetEnvironmentVariable('PYTHONPATH', $null, 'Process')

$ErrorActionPreference = 'Stop'

$gkmOperationStarted=[DateTime]::UtcNow
function Assert-GkmOperationActive {
    $signal=Join-Path (Split-Path -Parent $PSScriptRoot) 'out\stop-request.txt'
    if(Test-Path -LiteralPath $signal){
        throw [OperationCanceledException]::new('Local service operation cancelled by Stop Local Services.')
    }
}

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path      # ...\mitm
$Root = Split-Path -Parent $Here                             # package root
$ArgsFile = Join-Path $Root 'launch-args.json'
# GKM_PROXY_RECOVERY_20261114
if ($Official -or $NoOfflineProxy -or $OnlineProxy) { & (Join-Path $Here 'launcher_proxy_state.ps1') -Restore }

# ================= 官方模式：跳过 DMM，直接进官服 =================
if ($Official) {
    # 切到官方之前，如果系统代理还指着本地离线服务，必须先恢复，
    # 否则官方请求会被 7899 劫持，表现为「登录失败 / 通信错误」。
    $reg = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $cur = Get-ItemProperty -Path $reg -ErrorAction SilentlyContinue
    if ($cur -and $cur.ProxyServer -eq '127.0.0.1:7899') {
        $saved = Join-Path $Here 'proxy.saved.json'
        if (Test-Path -LiteralPath $saved) {
            try {
                $s = Get-Content -LiteralPath $saved -Raw | ConvertFrom-Json
                if ($s.Server) { Set-ItemProperty -Path $reg -Name ProxyServer -Value ([string]$s.Server) }
                Set-ItemProperty -Path $reg -Name ProxyEnable -Value ([int]$s.Enable)
            } catch { Set-ItemProperty -Path $reg -Name ProxyEnable -Value 0 }
        } else {
            Set-ItemProperty -Path $reg -Name ProxyEnable -Value 0
        }
        try {
            $sig = '[DllImport("wininet.dll", SetLastError=true)] public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);'
            Add-Type -MemberDefinition $sig -Name Off -Namespace GkmOff -ErrorAction SilentlyContinue | Out-Null
            [void][GkmOff.Off]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
            [void][GkmOff.Off]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
        } catch { }
        Write-Host '       [官方] 已恢复系统代理（不再指向本地服务）'
    }
    Write-Host '       [官方] 正在从 DMM 日志读取上一次的登录参数 ...'
    $dmmLog = Join-Path $env:APPDATA 'dmmgameplayer5\logs\dll.log'
    $exePath = ''; $viewer = ''; $openId = ''; $pfToken = ''
    if (Test-Path -LiteralPath $dmmLog) {
        $last = Select-String -Path $dmmLog -Pattern 'Execute of:: gakumas exe' -ErrorAction SilentlyContinue |
                Select-Object -Last 1 -ExpandProperty Line
        if ($last -match 'exe:\s*(?<exe_path>.*?gakumas\.exe).*?/viewer_id=(?<vid>[^\s]+).*?/open_id=(?<oid>[^\s]+).*?/pf_access_token=(?<tok>[^\s]+)') {
            # DMM 日志是 JSON 格式，路径里的反斜杠是转义过的（E:\\Gakuen\\...），必须还原
            $exePath = ($matches.exe_path -replace '\\\\', '\')
            $viewer  = $matches.vid
            $openId  = $matches.oid
            $pfToken = $matches.tok
        }
    }
    if (-not $exePath -or -not $viewer) {
        Write-Host ''
        Write-Host '       [错误] 没能从 DMM 日志里取到登录参数。' -ForegroundColor Red
        Write-Host ('              日志位置: ' + $dmmLog) -ForegroundColor Red
        Write-Host '              请先用 DMMGamePlayer 正常启动一次游戏，再回来点「开始游戏」。' -ForegroundColor Red
        Write-Host '              （只需要成功启动过一次，之后都可以跳过 DMM）' -ForegroundColor Red
        exit 5
    }
    if (-not (Test-Path -LiteralPath $exePath)) {
        Write-Host ('       [错误] 日志里的游戏路径不存在: ' + $exePath) -ForegroundColor Red
        exit 5
    }
    Write-Host ('       [官方] 游戏: ' + $exePath)
    Write-Host ('       [官方] viewer_id=' + $viewer)
    Write-Host '       [官方] 不启用本地代理 / 证书 / 离线服务'
    $wd = Split-Path -Parent $exePath
    $realArgs = @(
        ('/viewer_id=' + $viewer),
        ('/open_id=' + $openId),
        ('/pf_access_token=' + $pfToken)
    )
    $gp = $null
    try {
        Assert-GkmOperationActive
        $gp = Start-Process -FilePath $exePath -ArgumentList $realArgs -WorkingDirectory $wd -PassThru -ErrorAction Stop
    } catch {
        Write-Host ('       [错误] 无法启动游戏: ' + $_.Exception.Message) -ForegroundColor Red
        exit 6
    }
    Start-Sleep -Seconds 3
    if ($gp -and $gp.HasExited) {
        Write-Host ('       [错误] 游戏启动后立即退出 (exit code ' + $gp.ExitCode + ')') -ForegroundColor Red
        Write-Host '              token 可能已过期，请用 DMMGamePlayer 重新启动一次刷新它。' -ForegroundColor Red
        exit 7
    }
    Write-Host ('       [官方] 游戏已启动 (pid ' + $gp.Id + ')')
    exit 0
}
# ================================================================

function Get-RunningGame {
    Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue |
        Select-Object -First 1
}

function Parse-Args([string]$cmdline) {
    $out = @{}
    foreach ($k in @('viewer_id', 'onetime_token', 'open_id', 'pf_access_token')) {
        $m = [regex]::Match($cmdline, '/' + $k + '=([^\s"]+)')
        if ($m.Success) { $out[$k] = $m.Groups[1].Value }
    }
    return $out
}

if ($Capture) {
    $proc = Get-RunningGame
    if (-not $proc) {
        Write-Host '       no running gakumas.exe found'
        exit 1
    }
    $parsed = Parse-Args $proc.CommandLine
    if (-not $parsed['viewer_id']) {
        Write-Host '       could not find viewer_id in command line'
        exit 1
    }
    $exePath = $proc.ExecutablePath
    if (-not $exePath) { $exePath = $proc.Path }
    $obj = [ordered]@{
        exe_path        = $exePath
        viewer_id       = $parsed['viewer_id']
        onetime_token   = $parsed['onetime_token']
        open_id         = $parsed['open_id']
        pf_access_token = $parsed['pf_access_token']
    }
    ($obj | ConvertTo-Json) | Set-Content -LiteralPath $ArgsFile -Encoding UTF8
    Write-Host "       saved -> $ArgsFile"
    Write-Host "       viewer_id = $($parsed['viewer_id'])"
    exit 0
}

# ---------- default: launch ----------
# 缺参数 / 参数过期时，同步抓一次，抓到就直接接着启动，不用再点一次按钮。
function Invoke-Capture {
    param([string]$Reason)
    Write-Host ''
    Write-Host ' ============================================================'
    Write-Host ('  ' + $Reason)
    Write-Host ' ============================================================'
    Write-Host ''
    Write-Host '  接下来会打开一个抓取窗口，照着里面的提示做：'
    Write-Host '    1) 打开 DMMGamePlayer'
    Write-Host '    2) 点学マス 的「ゲームスタート」把游戏启动起来'
    Write-Host '    3) 窗口里出现 OK pid=... 之后，把那个游戏关掉'
    Write-Host ''
    Write-Host '  抓完会自动继续启动，不用再点第二次。'
    Write-Host ''
    Start-Process powershell -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$Here\capture_args.ps1`"",
        '-TimeoutSec', '600', '-NoPause'
    ) -Wait | Out-Null
    return (Test-Path -LiteralPath $ArgsFile)
}

$needCapture = $false
$captureReason = ''

# 2026-10-23：离线服务会伪造 Auth/Login（等价 SCSP 的 fixed_offline_auth），
# 真实 DMM 票完全不需要。参数文件缺失时直接用占位值，不再抓取。
if (-not (Test-Path -LiteralPath $ArgsFile)) {
    $gpFile = Join-Path (Split-Path -Parent $Here) 'game_path.txt'
    $gdir = $null
    if (Test-Path -LiteralPath $gpFile) { $gdir = (Get-Content -LiteralPath $gpFile -Encoding UTF8 -TotalCount 1).Trim().Trim([char]34) }
    # $gdir 可能为 $null（没有 game_path.txt）——不能直接 Join-Path
    $exeGuess = ''
    if ($gdir) { $exeGuess = Join-Path $gdir 'gakumas.exe' }
    if (-not $exeGuess -or -not (Test-Path -LiteralPath $exeGuess)) {
        # 自动探测：常见位置优先，再浅层搜各盘的 gakumas 目录
        $found = $null
        $roots = @()
        foreach ($drv in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
            if ($drv.Root) { $roots += $drv.Root }
        }
        foreach ($r in $roots) {
            foreach ($sub in @('Gakuen IDOLM@STER\gakumas', 'gakumas', 'Games\gakumas', 'Game\gakumas', 'SteamLibrary\steamapps\common\gakumas')) {
                $cand = Join-Path (Join-Path $r $sub) 'gakumas.exe'
                if (Test-Path -LiteralPath $cand) { $found = $cand; break }
            }
            if ($found) { break }
        }
        if (-not $found) {
            foreach ($r in $roots) {
                try {
                    $hit = Get-ChildItem -LiteralPath $r -Directory -Filter 'gakumas' -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($hit -and (Test-Path -LiteralPath (Join-Path $hit.FullName 'gakumas.exe'))) {
                        $found = Join-Path $hit.FullName 'gakumas.exe'
                        break
                    }
                } catch { }
            }
        }
    if (-not $found) {
        Write-Host "  [搜索] 正在全盘查找 gakumas.exe（最多 30 秒）..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddSeconds(30)
        foreach ($r in $roots) {
            if ($found) { break }
            if ((Get-Date) -gt $deadline) { break }
            try {
                $hit = Get-ChildItem -LiteralPath $r -Filter "gakumas.exe" -Recurse -Depth 4 -File -ErrorAction SilentlyContinue |
                       Select-Object -First 1
                if ($hit) { $found = $hit.FullName }
            } catch { }
        }
        if ($found) { Write-Host ("  [搜索] 找到: {0}" -f $found) -ForegroundColor Green }
        elseif ((Get-Date) -gt $deadline) { Write-Host "  [搜索] 已超时" -ForegroundColor Yellow }
    }

        if ($found) {
            $exeGuess = $found
            $newDir = Split-Path -Parent $found
            try { Set-Content -LiteralPath $gpFile -Value $newDir -Encoding UTF8 } catch { }
            Write-Host ("  [自动] 找到游戏目录: {0}" -f $newDir) -ForegroundColor DarkGray
        } else {
            Write-Host '  [错误] 没有找到 gakumas.exe，请在 game_path.txt 里写上游戏目录' -ForegroundColor Yellow
        }
    }
    $stub = [ordered]@{
        exe_path        = $exeGuess
        viewer_id       = '999999999'
        onetime_token   = ('0' * 32)
        open_id         = 'offline000000000'
        pf_access_token = ('0' * 102)
    }
    try {
        ($stub | ConvertTo-Json) | Set-Content -LiteralPath $ArgsFile -Encoding UTF8
        Write-Host '  [提示] 已生成离线占位参数（不需要 DMM 登录票）' -ForegroundColor DarkGray
    } catch { }
}

$a = Get-Content -LiteralPath $ArgsFile -Raw | ConvertFrom-Json
if (-not $a.exe_path -or -not (Test-Path -LiteralPath $a.exe_path)) {
    Write-Host "       找不到 gakumas.exe，请手动选择游戏目录" -ForegroundColor Yellow
    $picked = $null
    try {
        Add-Type -AssemblyName System.Windows.Forms | Out-Null
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = "请选择 gakumas.exe 所在的文件夹"
        $dlg.ShowNewFolderButton = $false
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { $picked = $dlg.SelectedPath }
    } catch {
        Write-Host "       无法弹出选择框：$($_.Exception.Message)" -ForegroundColor Red
    }
    if ($picked) {
        $exe2 = Join-Path $picked "gakumas.exe"
        if (Test-Path -LiteralPath $exe2) {
            Set-Content -LiteralPath (Join-Path $Root "game_path.txt") -Value $picked -Encoding UTF8
            $stub2 = [ordered]@{
                exe_path        = $exe2
                viewer_id       = "999999999"
                onetime_token   = ("0" * 32)
                open_id         = "offline000000000"
                pf_access_token = ("0" * 102)
            }
            ($stub2 | ConvertTo-Json) | Set-Content -LiteralPath $ArgsFile -Encoding UTF8
            Write-Host "       已保存: $exe2" -ForegroundColor Green
            $a = Get-Content -LiteralPath $ArgsFile -Raw | ConvertFrom-Json
        } else {
            Write-Host "       该目录下没有 gakumas.exe" -ForegroundColor Red
            exit 3
        }
    } else {
        Write-Host "       未选择。可手动在包根目录建 game_path.txt 写入游戏目录。" -ForegroundColor Yellow
        exit 3
    }
}

# ---------- upstream HTTP proxy for online capture ----------
function Get-GkmUpstreamProxy {
    $servers=New-Object 'System.Collections.Generic.List[string]'
    $registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    try {
        $current=Get-ItemProperty -LiteralPath $registry
        $value=[string]$current.ProxyServer
        if($value -and $value -notmatch '(?i)(?:127\.0\.0\.1|localhost|\[::1\]):7899'){$servers.Add($value)}
    } catch { }
    try {
        $savedPath=Join-Path $Here 'proxy.saved.json'
        if(Test-Path -LiteralPath $savedPath){
            $saved=[IO.File]::ReadAllText($savedPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
            $value=[string]$saved.Server
            if($value -and $value -notmatch '(?i)(?:127\.0\.0\.1|localhost|\[::1\]):7899'){$servers.Add($value)}
        }
    } catch { }
    foreach($server in $servers){
        $https=New-Object 'System.Collections.Generic.List[string]'
        $http=New-Object 'System.Collections.Generic.List[string]'
        $plain=New-Object 'System.Collections.Generic.List[string]'
        foreach($part in ($server -split ';')){
            if($part -match '^\s*(?<kind>[^=]+)=(?<endpoint>.+?)\s*$'){
                $kind=$Matches['kind'].Trim().ToLowerInvariant();$endpoint=$Matches['endpoint'].Trim()
                if($kind -eq 'https'){$https.Add($endpoint)}elseif($kind -eq 'http'){$http.Add($endpoint)}
            }elseif($part.Trim()){$plain.Add($part.Trim())}
        }
        foreach($candidate in (@($https)+@($http)+@($plain))){
            if($candidate -match '(?i)^https://'){continue}
            $candidate=$candidate -replace '(?i)^http://',''
            $candidate=$candidate.TrimEnd('/')
            $match=[regex]::Match($candidate,'^(?:\[[0-9a-fA-F:]+\]|[A-Za-z0-9.-]+):([0-9]{1,5})$')
            if($match.Success){$port=[int]$match.Groups[1].Value;if($port -gt 0 -and $port -le 65535){return $candidate}}
        }
    }
    return '127.0.0.1:7897'
}

# ---------- make sure the local MITM service is up ----------
# Without this the hook DLL forwards every request to 127.0.0.1:18443 and the
# game shows "通信エラー" because nothing is listening there.
function Test-ServiceUp {
    return [bool](Get-NetTCPConnection -LocalPort 18443 -State Listen -ErrorAction SilentlyContinue)
}

function Start-LocalService {
    Assert-GkmOperationActive
    $py = Join-Path $Root 'python\python.exe'
    if (-not (Test-Path -LiteralPath $py)) {
        Write-Host "       python not found: $py"
        return $false
    }
    if (Test-Path -LiteralPath (Join-Path $Root 'online-capture.flag')) {
        # 在线抓取模式：真实转发官服，并把 Produce 响应落盘供分析
        $env:GKM_MODE          = 'online'
        $env:GKM_DUMP_PRODUCE  = '1'
        $env:GKM_UPSTREAM_PROXY = Get-GkmUpstreamProxy
        Write-Host '       [capture] ONLINE mode - forwarding to official server' -ForegroundColor Yellow
    } else {
        $env:GKM_MODE           = 'offline'
        $env:GKM_UPSTREAM_PROXY = '127.0.0.1:1'
        Remove-Item Env:GKM_DUMP_PRODUCE -ErrorAction SilentlyContinue
    }
    $env:GKM_STATIC_USER    = '1'
    $env:GKM_MATCH          = 'exact'
    $env:GKM_MITM_PORT      = '18443'
    $env:GKM_H2C_PORT       = '18446'
    $env:PYTHONIOENCODING   = 'utf-8'
    $env:PYTHONUNBUFFERED   = '1'
    Assert-GkmOperationActive
    Start-Process -FilePath $py -ArgumentList 'cache_server.py' `
        -WorkingDirectory $Here -WindowStyle Hidden -PassThru | ForEach-Object { Save-UpdateProcess $_.Id }
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-ServiceUp) { break }
    }
    # http1_bridge: hook 发来的是 HTTP/1.1 gRPC，翻译成 h2c 交给 cache_server(18446)
    $bridgePy = Join-Path $Here 'http1_bridge.py'
    $bridgeUp = [bool](Get-NetTCPConnection -LocalPort 18444 -State Listen -ErrorAction SilentlyContinue)
    if (-not $bridgeUp -and (Test-Path -LiteralPath $bridgePy)) {
        Assert-GkmOperationActive
        Start-Process -FilePath $py -ArgumentList 'http1_bridge.py' `
            -WorkingDirectory $Here -WindowStyle Hidden -PassThru | ForEach-Object { Save-UpdateProcess $_.Id }
        for ($i = 0; $i -lt 40; $i++) {
            Start-Sleep -Milliseconds 300
            if (Get-NetTCPConnection -LocalPort 18444 -State Listen -ErrorAction SilentlyContinue) { break }
        }
    }
    if (Test-ServiceUp) { return $true }
    return $false
}

if (-not (Test-ServiceUp)) {
    Write-Host '       local service is not running, starting it ...'
    if (-not (Start-LocalService)) {
        Write-Host '       [错误] 本地服务启动失败，请点面板的「重启本地服务」看具体报错'
        exit 5
    }
    Write-Host '       local service is up (grpc 18443 / h2c 18446 / bridge 18444)'
} else {
    Write-Host '       local service already running'
}

# ---------- offline https proxy (Octo / Firebase / DMM) ----------
# 让游戏不再依赖外网：本地代理接管系统代理，按夹具应答；游戏退出后自动还原。
function ProxyUp { return [bool](Get-NetTCPConnection -LocalPort 7899 -State Listen -ErrorAction SilentlyContinue) }

function Start-OfflineProxy {
    Assert-GkmOperationActive
    $py = Join-Path $Root 'python\python.exe'
    if (-not (Test-Path -LiteralPath $py)) { return $false }
    $env:GKM_FIXTURE_DIR = Join-Path $Here 'offline'
    $previousUpstream=[Environment]::GetEnvironmentVariable('GKM_UPSTREAM_PROXY','Process')
    if($OnlineProxy){$mode='forward';$env:GKM_UPSTREAM_PROXY=Get-GkmUpstreamProxy}else{$mode='replay';Remove-Item Env:GKM_UPSTREAM_PROXY -ErrorAction SilentlyContinue}
    $pout = Join-Path $Here 'proxy_out'
    $pargs = 'offline_proxy.py ' + $mode + ' "' + $pout + '"'
    Assert-GkmOperationActive
    try { Start-Process -FilePath $py -ArgumentList $pargs -WorkingDirectory $Here -WindowStyle Hidden | Out-Null }
    finally { if($null -ne $previousUpstream){$env:GKM_UPSTREAM_PROXY=$previousUpstream}else{Remove-Item Env:GKM_UPSTREAM_PROXY -ErrorAction SilentlyContinue} }
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 300; if (ProxyUp) { return $true } }
    return $false
}

function Save-SystemProxy {
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $p = Get-ItemProperty -Path $k
    $f = Join-Path $Here 'proxy.saved.json'
    if ($p.ProxyServer -eq '127.0.0.1:7899' -and (Test-Path -LiteralPath $f)) {
        return (Get-Content -LiteralPath $f -Raw | ConvertFrom-Json)
    }
    $state = [pscustomobject]@{ Enable = [int]$p.ProxyEnable; Server = [string]$p.ProxyServer }
    $state | ConvertTo-Json -Compress | Set-Content -LiteralPath $f -Encoding ASCII
    return $state
}

function Set-SystemProxy([string]$server) {
    $k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    Set-ItemProperty -Path $k -Name ProxyServer -Value $server
    Set-ItemProperty -Path $k -Name ProxyEnable -Value 1
    try {
        $sig = '[DllImport("wininet.dll", SetLastError=true)] public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);'
        Add-Type -MemberDefinition $sig -Name W -Namespace Gkm -ErrorAction SilentlyContinue | Out-Null
        [void][Gkm.W]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
        [void][Gkm.W]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
    } catch { }
}

function Start-ProxyWatcher {
    $w = Get-Command powershell -ErrorAction SilentlyContinue
    if (-not $w) { return }
    $wargs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $Here 'restore_proxy_after.ps1') + '"'
    Start-Process powershell -ArgumentList $wargs -WindowStyle Hidden | Out-Null
}

$proxyReady = $false
if ($NoOfflineProxy) {
    Write-Host '       update mode: skip offline proxy takeover'
} else {
try {
    # 确保本地 MITM 根证书被当前用户信任（无需管理员）
    # 用户可能在证书窗口点了「否」。这里自动再弹一次；两次都失败就中止启动，
    # 免得游戏起来后只报「初始化失败」，用户根本不知道是自己点了「否」。
    $caOk = $false
    for ($caTry = 1; $caTry -le 2; $caTry++) {
        try {
            $caOut = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Here 'ensure_ca.ps1')
            $caCode = $LASTEXITCODE
            if ($caOut) { $caOut | ForEach-Object { Write-Host $_ } }
            if ($caCode -eq 0) { $caOk = $true; break }
        } catch {
            Write-Host ('       [警告] ensure_ca.ps1 执行异常: ' + $_.Exception.Message) -ForegroundColor Yellow
        }
        if ($caTry -eq 1) {
            Write-Host ''
            Write-Host '       证书没有安装成功，正在再弹一次…' -ForegroundColor Yellow
            Write-Host '       请在弹出的窗口里点「是」。' -ForegroundColor Yellow
            Write-Host ''
        }
    }
    if (-not $caOk -and $SkipCA) {
        Write-Host ''
        Write-Host '       [CA] 已指定 -SkipCA：跳过证书检查，强制继续启动。' -ForegroundColor Yellow
        Write-Host '             （gRPC 会走 yaha 的免验证通道，但 DMM/Firebase 可能连不上）' -ForegroundColor Yellow
        $caOk = $true
    }
    if (-not $caOk) {
        Write-Host ''
        Write-Host ' ============================================================' -ForegroundColor Red
        Write-Host '  [错误] 本地证书没有安装，游戏无法联网，已停止启动。' -ForegroundColor Red
        Write-Host ''
        Write-Host '  请再点一次「开始游戏」，并在弹出的证书窗口点「是」。' -ForegroundColor Red
        Write-Host '  如果窗口一直不出现，手动运行这个文件：' -ForegroundColor Red
        Write-Host ('    ' + (Join-Path $Here 'ensure_ca.ps1')) -ForegroundColor Red
        Write-Host ''
        Write-Host '  确认自己不需要它、仍要强行启动：' -ForegroundColor DarkGray
        Write-Host ('    powershell -File "' + $MyInvocation.MyCommand.Path + '" -SkipCA') -ForegroundColor DarkGray
        Write-Host ' ============================================================' -ForegroundColor Red
        exit 4
    }
    if (ProxyUp) {
        $proxyReady = $true
        # 7899 可能被另一份包占用；这种情况两个实例会抢同一个服务
        try {
            $conn = Get-NetTCPConnection -LocalPort 7899 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($conn) {
                $pr = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $conn.OwningProcess) -ErrorAction SilentlyContinue
                if ($pr -and $pr.CommandLine -and ($pr.CommandLine -notlike ('*' + $Root + '*'))) {
                    Write-Host '       [警告] 7899 已被另一个程序占用（可能是另一份离线包）:' -ForegroundColor Yellow
                    Write-Host ('              ' + $pr.CommandLine) -ForegroundColor DarkGray
                }
            }
        } catch { }
    }
    else {
        Write-Host '       starting offline https proxy ...'
        $proxyReady = Start-OfflineProxy
    }
    if ($proxyReady) {
        $saved = Save-SystemProxy
        Set-SystemProxy '127.0.0.1:7899'
        Start-ProxyWatcher
        if($OnlineProxy){Write-Host '       online TLS forwarding proxy ready (system proxy -> 127.0.0.1:7899)'}else{Write-Host ('       offline proxy ready (system proxy ' + $saved.Server + ' -> 127.0.0.1:7899')}
    } else {
        Write-Host '       [warn] offline proxy not available, keep current system proxy'
    }
} catch {
    Write-Host ('       [warn] offline proxy setup failed: ' + $_.Exception.Message)
}
}
if($OnlineProxy -and -not $proxyReady){Write-Host '       [错误] 在线更新需要本地 TLS 转发代理；未启动成功，已停止游戏启动。' -ForegroundColor Red;exit 5}

# 清理上次抓参数可能残留的防火墙拦截（否则游戏下载 master 会被挡住）
try { Remove-NetFirewallRule -DisplayName 'gkm-capture-block' -ErrorAction SilentlyContinue } catch { }

# ---------- 日志滚动：三个日志都是追加写，不控制会无界增长 ----------
# yaha-proxy.log 每个请求会写几十行 header，一晚上能涨几十 MB。
try {
    $logSpecs = @(
        @{ p = (Join-Path (Split-Path -Parent $a.exe_path) 'gakumas_Data\Plugins\x86_64\yaha-proxy.log'); mb = 8 },
        @{ p = (Join-Path $Here 'mitm.log'); mb = 4 },
        @{ p = (Join-Path $Here 'proxy_out\mitm_capture.log'); mb = 8 }
    )
    foreach ($s in $logSpecs) {
        if ((Test-Path -LiteralPath $s.p) -and ((Get-Item -LiteralPath $s.p).Length -gt ($s.mb * 1MB))) {
            $old = $s.p + '.1'
            Move-Item -LiteralPath $s.p -Destination $old -Force -ErrorAction SilentlyContinue
            Write-Host ('       [日志] ' + (Split-Path $s.p -Leaf) + ' 超过 ' + $s.mb + ' MB，已滚动为 .1')
        }
    }
} catch { }

# ---------- 确保 yaha 代理在位（游戏更新会把 Plugins 里的 DLL 换回官方） ----------
# 两种情况要处理：
#   1) orig.dll 缺失 -> 代理无法转发，登录必报 1003
#   2) live 被游戏更新覆盖回原版 -> 离线功能静默失效（不报错，只是变回在线）
try {
    $pluginDir = Join-Path (Split-Path -Parent $a.exe_path) 'gakumas_Data\Plugins\x86_64'
    $liveDll   = Join-Path $pluginDir 'Cysharp.Net.Http.YetAnotherHttpHandler.Native.dll'
    $origDll   = Join-Path $pluginDir 'Cysharp.Net.Http.YetAnotherHttpHandler.Native.orig.dll'
    $pkgOrig   = Join-Path $Root 'hook\Cysharp.Net.Http.YetAnotherHttpHandler.Native.orig.dll'
    $pkgProxy  = Join-Path $Root 'hook\yaha_proxy.dll'

    if (Test-Path -LiteralPath $liveDll) {
        $hLive = ''
        try { $hLive = (Get-FileHash -LiteralPath $liveDll -Algorithm SHA256).Hash } catch { }
        $hProxy = ''
        if (Test-Path -LiteralPath $pkgProxy) {
            try { $hProxy = (Get-FileHash -LiteralPath $pkgProxy -Algorithm SHA256).Hash } catch { }
        }
        $isProxy = ($hProxy -and $hLive -eq $hProxy)

        if (-not $isProxy) {
            # live 不是代理版 —— 可能是游戏更新覆盖了，也可能用户手动还原过
            if ((Test-Path -LiteralPath $origDll) -and $hLive -ne '') {
                $hOrig = ''
                try { $hOrig = (Get-FileHash -LiteralPath $origDll -Algorithm SHA256).Hash } catch { }
                if ($hLive -ne $hOrig) {
                    # live 既不是代理也不是旧原版 => 游戏更新换成了新原版，先更新备份
                    Copy-Item -LiteralPath $liveDll -Destination $origDll -Force
                    Write-Host '       [fix] 游戏文件已更新，刷新原版备份 (.orig.dll)'
                }
            }
            if (Test-Path -LiteralPath $pkgProxy) {
                Copy-Item -LiteralPath $pkgProxy -Destination $liveDll -Force
                Write-Host '       [fix] 已重新部署 yaha 代理（游戏更新会覆盖它）'
            }
        }

        if (-not (Test-Path -LiteralPath $origDll)) {
            if (Test-Path -LiteralPath $pkgOrig) {
                Copy-Item $pkgOrig $origDll -Force
                Write-Host '       [fix] 已补回 yaha 原版备份 (.orig.dll)'
            } else {
                Write-Host '       [警告] 缺少 yaha 原版备份，登录会报 1003' -ForegroundColor Yellow
            }
        }
    }
} catch {
    Write-Host ('       [warn] yaha 代理检查失败: ' + $_.Exception.Message) -ForegroundColor Yellow
}

$gameArgs = @(
    "/viewer_id=$($a.viewer_id)",
    "/onetime_token=$($a.onetime_token)",
    "/open_id=$($a.open_id)",
    "/pf_access_token=$($a.pf_access_token)"
)
$workDir = Split-Path -Parent $a.exe_path
if (-not (Test-Path -LiteralPath $a.exe_path)) {
    Write-Host ('       [错误] 找不到游戏主程序: ' + $a.exe_path) -ForegroundColor Red
    Write-Host '              请用启动器的「游戏文件夹」重新指定一次。' -ForegroundColor Red
    exit 6
}
$gp = $null
$launchStarted = Get-Date
$priorGameIds = @(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessId)
if ($priorGameIds.Count -gt 0) {
    Write-Host '       游戏已经在运行，请先关闭现有游戏窗口再启动。'
    exit 2
}
try {
    Assert-GkmOperationActive
    $gp = Start-Process -FilePath $a.exe_path -ArgumentList $gameArgs -WorkingDirectory $workDir -PassThru -ErrorAction Stop
} catch {
    Write-Host ('       [错误] 无法启动游戏: ' + $_.Exception.Message) -ForegroundColor Red
    exit 6
}
if ($gp) { Save-UpdateProcess $gp.Id }
# 等 3 秒确认进程真的活着——原来这里直接打印 game started，
# 游戏闪退时也会显示"已启动"，排查时极具误导性。
Start-Sleep -Seconds 3
if ($gp -and $gp.HasExited) {
    # DMM DRM can replace its short-lived parent with a protected game child.
    # Recognize only a child of the exact process this invocation launched.
    $newGames = @(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $priorGameIds -notcontains $_.ProcessId -and $_.CreationDate -ge $launchStarted.AddSeconds(-1) })
    $drmChild = $newGames | Where-Object { $_.ParentProcessId -eq $gp.Id } | Select-Object -First 1
    # A DRM relay can itself exit before CIM sees the intermediate parent.
    # A single newly created game is sufficient for launch detection only;
    # this does not claim that login or game initialization has succeeded.
    if (-not $drmChild -and $newGames.Count -eq 1) { $drmChild = $newGames[0] }
    if ($drmChild) {
        # Record every DRM-replaced game, not only the direct child: the relay often
        # exits before CIM can observe the parent link, and an unrecorded game leaks.
        Save-UpdateProcess $drmChild.ProcessId
        Write-Host ('       game started (pid ' + $drmChild.ProcessId + ', DRM child)')
        exit 0
    }
    Write-Host ''
    Write-Host ('       [错误] 游戏启动后立即退出 (exit code ' + $gp.ExitCode + ')') -ForegroundColor Red
    Write-Host '              常见原因：' -ForegroundColor Red
    Write-Host '                1) 这台机器从没用 DMMGamePlayer 正常启动过这个游戏（DRM 未初始化）' -ForegroundColor Red
    Write-Host '                   -> 先用官方 DMMGamePlayer 启动一次，能进标题画面即可' -ForegroundColor Red
    Write-Host '                2) 游戏目录不完整，缺少 gakumas.exe._ 或 version.dll' -ForegroundColor Red
    Write-Host '                3) 杀毒软件拦截了 gakumas.exe' -ForegroundColor Red
    exit 7
}
$liveGames = @(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $priorGameIds -notcontains $_.ProcessId -and $_.CreationDate -ge $launchStarted.AddSeconds(-1) })
foreach ($g in $liveGames) { Save-UpdateProcess $g.ProcessId }
Write-Host ('       game started (pid ' + $gp.Id + ')')
Write-Host "       viewer_id = $($a.viewer_id)"
exit 0
