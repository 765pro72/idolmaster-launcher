# Check official catalog first, then validate and transactionally import content.
param([int]$GameWaitSec=70, [switch]$NoGame, [switch]$DryRun, [switch]$RollbackMaster, [switch]$NoCapture, [int]$WaitSec=180)
$ErrorActionPreference = 'Stop'

$gkmOperationStarted=[DateTime]::UtcNow
function Assert-GkmOperationActive {
    $signal=Join-Path (Split-Path -Parent $PSScriptRoot) 'out\stop-request.txt'
    if(Test-Path -LiteralPath $signal){
        throw [OperationCanceledException]::new('Local service operation cancelled by Stop Local Services.')
    }
}


# 2026-11-01：任何 Start-Process 之前必须修复重复环境变量键，否则抛 HTTPS_PROXY/https_proxy。
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
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPython = Join-Path $taskRoot 'python\python.exe'
$taskScript = Join-Path $taskRoot 'tools\local_content_sync.py'
if (-not (Test-Path -LiteralPath $taskPython)) { Write-Error '缺少内置 Python，请检查解压是否完整。'; exit 1 }
if (-not (Test-Path -LiteralPath $taskScript)) { Write-Error '缺少本地同步组件，请检查解压是否完整。'; exit 1 }
# 新启动器使用 UTF-8；兼容仍在运行的旧启动器的 GBK 管道。
$env:PYTHONUTF8 = '1'
$taskEncoding = if ($env:GKM_LAUNCHER_UTF8 -eq '1') { 'utf-8' } else { 'gbk' }
$env:GKM_SYNC_OUTPUT_ENCODING = $taskEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding($taskEncoding)
New-Item -ItemType Directory -Path (Join-Path $taskRoot 'out') -Force | Out-Null
try {
    $updateLock = [IO.File]::Open((Join-Path $taskRoot 'out\official-update.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
} catch {
    Write-Output 'Another content update is in progress. No changes were made.'
    exit 2
}
$taskArguments = @('-B', $taskScript, '--root', $taskRoot)
if ($DryRun) { $taskArguments += '--dry-run' }
if ($RollbackMaster) { $taskArguments += '--rollback-master' }
$taskExit = 0
if ($DryRun -or $RollbackMaster -or $NoCapture -or $NoGame) {
    & $taskPython @taskArguments
    exit $LASTEXITCODE
}
# A successful local import does NOT establish that the official catalog is current.


# ---------- sync 失败时，从官方补抓 Master/Get ----------
# 原理：
#   优先使用 DMM 启动记录中的真实 pf_access_token 与 onetime_token，不强制进程常驻。
#   票据缺失或官方认证失败时再刷新 DMM 会话；票据及认证请求均不打印。
#   服务必须用 online 模式（不是 hybrid）：否则 System/Check 会返回 forge 里的旧响应，
#   其中的 JWT 早已过期，游戏拿它拉商品会 Not Found -> 弹「初始化失败」-> 永远不发 Auth/Login。
if (-not $DryRun -and -not $RollbackMaster -and -not $NoCapture -and -not $NoGame) {

    Write-Output ''
    Write-Output '=== 从官方补抓 Master/Get（online 模式，复用已有真实票据）==='

    $dmmLog = Join-Path $env:APPDATA 'dmmgameplayer5\logs\dll.log'
    $oExe = ''; $oViewer = ''; $oOpenId = ''; $oPf = ''; $oOt = ''
    if (Test-Path -LiteralPath $dmmLog) {
        try {
            $last = Select-String -Path $dmmLog -Pattern 'Execute of:: gakumas exe' -ErrorAction SilentlyContinue |
                    Select-Object -Last 1 -ExpandProperty Line
            if ($last -match 'exe:\s*(?<e>.*?gakumas\.exe).*?/viewer_id=(?<v>[^\s]+).*?/open_id=(?<o>[^\s]+).*?/pf_access_token=(?<t>[^\s]+)') {
                $oExe    = ($matches.e -replace '\\\\', '\')
                $oViewer = $matches.v
                $oOpenId = $matches.o
                $oPf     = $matches.t
                if ($last -match '/onetime_token=(?<ot>[^\s"\\]+)') { $oOt = $matches.ot }
            }
        } catch { }
    }
    if (-not $oExe -or -not $oPf) {
        Write-Output ''
        Write-Output '没能从 DMMGamePlayer 日志里取到登录凭据。'
        Write-Output ('  日志: ' + $dmmLog)
        Write-Output '  请先用 DMMGamePlayer 正常启动一次学园（只需一次），再点「更新游戏内容」。'
        exit 5
    }
    if (-not $oOt -or $oOt -match '^0+$') {
        Write-Output 'DMM 启动记录缺少真实 onetime_token。请从 DMM 启动一次所选学园客户端，然后关闭游戏并重试。'
        exit 5
    }
    if (-not (Get-Process DMMGamePlayer -ErrorAction SilentlyContinue)) {
        Write-Output 'DMMGamePlayer 未运行，先尝试已有真实票据。若官方登录失败，再通过 DMM 刷新会话。'
    }
    Write-Output 'Official login credentials and non-placeholder launch ticket found (values hidden).'
    $configuredDir = (Get-Content -LiteralPath (Join-Path $taskRoot 'game_path.txt') -Raw).Trim().Trim('"')
    if ([IO.Path]::GetFullPath((Split-Path -Parent $oExe)) -ne [IO.Path]::GetFullPath($configuredDir)) {
        Write-Output 'The DMM login belongs to a different game installation. Launch the selected installation with DMM first.'
        exit 5
    }
    if (Test-Path -LiteralPath (Join-Path $taskRoot 'online-capture.flag')) {
        Write-Output 'An online capture marker already exists. Inspect the previous update before retrying.'
        exit 2
    }
    if (Get-Process gakumas -ErrorAction SilentlyContinue) {
        Write-Output 'Close the game before updating. No process was stopped.'
        exit 2
    }
    # Switch only this installation's known services; reject foreign listeners.
    $listeners = @(Get-NetTCPConnection -LocalPort 18443,18444,18446,7899 -State Listen -ErrorAction SilentlyContinue)
    $ownedServices = @()
    foreach ($ownerId in @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)) {
        $service = Get-CimInstance Win32_Process -Filter ("ProcessId=" + [int]$ownerId)
        if (-not $service -or $service.ExecutablePath -ne $taskPython -or
            $service.CommandLine -notmatch '(?i)(?:^|[\\/\s"''])?(cache_server|http1_bridge|offline_proxy)\.py(?:[\s"'']|$)') {
            Write-Output 'An update port belongs to another program. No shared service was stopped.'
            exit 2
        }
        $ownedServices += $service
    }
    # An old proxy watcher must not restore registry values midway through capture.
# GKM_PROXY_RECOVERY_20261114
& (Join-Path $PSScriptRoot 'launcher_proxy_state.ps1') -Restore
    $watcherPath = Join-Path $PSScriptRoot 'restore_proxy_after.ps1'
    $ownedWatchers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($watcherPath, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    foreach ($service in @($ownedWatchers) + @($ownedServices)) {
        $live = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $service.ProcessId)
        if ($live -and $live.ExecutablePath -eq $service.ExecutablePath -and $live.CreationDate -eq $service.CreationDate) {
            # Restore the saved proxy while its original listener is still alive.
            $proxyKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
            $proxyState = Get-ItemProperty -Path $proxyKey
            if ($proxyState.ProxyServer -eq '127.0.0.1:7899') {
                $savedFile = Join-Path $PSScriptRoot 'proxy.saved.json'
                if (-not (Test-Path -LiteralPath $savedFile)) {
                    throw 'Cannot restore the previous proxy: saved configuration is missing. Local service retained.'
                }
                $savedProxy = Get-Content -LiteralPath $savedFile -Raw | ConvertFrom-Json
                Set-ItemProperty -Path $proxyKey -Name ProxyServer -Value ([string]$savedProxy.Server)
                Set-ItemProperty -Path $proxyKey -Name ProxyEnable -Value ([int]$savedProxy.Enable)
                if (-not ('GkmUpd.GkmUpdateWinInet' -as [type])) {
                    Add-Type -MemberDefinition '[DllImport("wininet.dll")] public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);' -Name GkmUpdateWinInet -Namespace GkmUpd
                }
                [void][GkmUpd.GkmUpdateWinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
                [void][GkmUpd.GkmUpdateWinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
            }
            Stop-Process -Id $live.ProcessId -Force -ErrorAction Stop
        }
    }
    if (Get-NetTCPConnection -LocalPort 18443,18444,18446,7899 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Update ports are not free after stopping owned services.'
    }
    $captureStarted = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $receipt = Join-Path $taskRoot ('out\update-processes-' + [guid]::NewGuid().ToString('N') + '.jsonl')
    New-Item -ItemType Directory -Path (Split-Path -Parent $receipt) -Force | Out-Null
    $downloadScript = Join-Path $taskRoot 'tools\download_master_tables.py'
    $gameCache = Join-Path $taskRoot 'gamecache'
    if (-not (Test-Path -LiteralPath $gameCache)) {
        $gameCache = Join-Path $env:LOCALAPPDATA '..\LocalLow\BANDAI NAMCO Entertainment Inc_\gakumas\EB0A191797624DD3A48FA681D3061212'
    }

    # 备份 launch-args.json，写入真实官方票据；在线更新禁止使用离线零值占位符。
    $argsFile = Join-Path $taskRoot 'launch-args.json'
    $argsBak  = $argsFile + '.grab-bak-' + [guid]::NewGuid().ToString('N')
    $hadArgs = Test-Path -LiteralPath $argsFile
    if (Test-Path -LiteralPath $argsFile) {
        Copy-Item -LiteralPath $argsFile -Destination $argsBak -Force -ErrorAction Stop
    }
    $argObj = [ordered]@{
        exe_path        = $oExe
        viewer_id       = $oViewer
        onetime_token   = $oOt
        open_id         = $oOpenId
        pf_access_token = $oPf
    }

    # online-capture.flag -> launch_game.ps1 据此设 GKM_MODE=online
    $captureFlag = Join-Path $taskRoot 'online-capture.flag'

    $launchScript = Join-Path $PSScriptRoot 'launch_game.ps1'
    if (-not (Test-Path -LiteralPath $launchScript)) {
        Write-Output '缺少 launch_game.ps1；现有数据未改动。'
        Remove-Item -LiteralPath $captureFlag -Force -ErrorAction SilentlyContinue
        exit 5
    }

    $lgLog  = Join-Path $env:TEMP ('gkm-launch-' + [guid]::NewGuid().ToString('N') + '.log')

    $activityScript = Join-Path $PSScriptRoot 'activity_cache.py'
    function Get-ActivityCaptureStatus {
        $activityStatus = & $taskPython -B $activityScript --root $taskRoot --since $captureStarted
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Message = ($activityStatus -join [Environment]::NewLine) }
    }
    $lgErr  = $lgLog + '.err'
    $found  = $null

    function Find-NewMasterRecord {
        Assert-GkmOperationActive
        & $taskPython -B $downloadScript --root $taskRoot --cache $gameCache --since $captureStarted --probe | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -eq 4) { $script:captureRejected = $true }
        return ($LASTEXITCODE -eq 0)
    }

    try {
        ($argObj | ConvertTo-Json) | Set-Content -LiteralPath $argsFile -Encoding UTF8
        Set-Content -LiteralPath $captureFlag -Value 'capture' -Encoding UTF8
        Assert-GkmOperationActive
        $lgProc = Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', ('"' + $launchScript + '"'), '-OnlineProxy', '-UpdateReceiptPath', ('"' + $receipt + '"')) `
            -WorkingDirectory $PSScriptRoot -RedirectStandardOutput $lgLog -RedirectStandardError $lgErr -WindowStyle Hidden -PassThru
        Write-Output '  已启动游戏（online 模式），等待 Master/Get ...'
        $pos = 0
        $deadline = (Get-Date).AddSeconds($WaitSec)
        $idle = 0
        while ((Get-Date) -lt $deadline) {
            Assert-GkmOperationActive
            Start-Sleep -Milliseconds 800
            $cand = Find-NewMasterRecord
            if ($script:captureRejected) { break }
            if ($cand) { $found = $cand; break }
            if ($lgProc -and $lgProc.HasExited -and -not (Get-Process gakumas -ErrorAction SilentlyContinue)) {
                $idle++
                if ($idle -ge 3) { $found = Find-NewMasterRecord; break }
            }
        }
        if ($found) {
            Write-Output '  已取得 Master/Get。进入主页后，点开需要离线使用的活动页以加载详情（最多等待 180 秒）。'
            $actDeadline = (Get-Date).AddSeconds(180)
            $lastActivityMessage = $null
            while ((Get-Date) -lt $actDeadline) {
                Assert-GkmOperationActive
                $activityStatus = Get-ActivityCaptureStatus
                if ($activityStatus.Message -and $activityStatus.Message -ne $lastActivityMessage) {
                    Write-Output $activityStatus.Message
                    $lastActivityMessage = $activityStatus.Message
                }
                if ($activityStatus.ExitCode -ne 2 -and $activityStatus.ExitCode -ne 3) { break }
                if (-not (Get-Process gakumas -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Seconds 2
            }
        } else {
            Write-Output '  未收到 Master/Get，跳过活动数据等待；现有本地内容保持不变。'
        }
    } catch {
        Write-Output ('启动失败: ' + $_.Exception.Message)
    } finally {
        Remove-Item -LiteralPath $lgLog, $lgErr -Force -ErrorAction SilentlyContinue
        if ($lgProc -and -not $lgProc.HasExited) {
            Stop-Process -InputObject $lgProc -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $receipt) {
            foreach ($line in Get-Content -LiteralPath $receipt) {
                try {
                    $owned = $line | ConvertFrom-Json
                    $live = Get-CimInstance Win32_Process -Filter ("ProcessId=" + [int]$owned.pid)
                    if ($live -and $live.ExecutablePath -eq $owned.exe -and
                        $live.CreationDate.ToUniversalTime().ToString('o') -eq $owned.created) {
                        Stop-Process -Id $live.ProcessId -Force -ErrorAction Stop
                    }
                } catch { Write-Output 'Owned process cleanup could not be confirmed; inspect update receipt.' }
            }
        }
        # A DRM relay can replace the launched parent before it is observed, so the
        # receipt alone may miss the real game. Stop any gakumas created during this
        # capture; games that existed before it are never touched.
        foreach ($g in @(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue)) {
            try {
                if (([DateTimeOffset]$g.CreationDate).ToUnixTimeSeconds() -lt $captureStarted) { continue }
                if (-not $g.ExecutablePath) { continue }
                Stop-Process -Id $g.ProcessId -Force -ErrorAction Stop
                Write-Output ('  已关闭本次更新启动的游戏 (pid ' + $g.ProcessId + ')')
            } catch { }
        }
        Start-Sleep -Milliseconds 400
        Remove-Item -LiteralPath $captureFlag -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $argsBak) {
            Copy-Item -LiteralPath $argsBak -Destination $argsFile -Force -ErrorAction Stop
            Remove-Item -LiteralPath $argsBak -Force -ErrorAction SilentlyContinue
        } elseif (-not $hadArgs) {
            Remove-Item -LiteralPath $argsFile -Force -ErrorAction SilentlyContinue
        }
        # 2026-11-11: 更新流程结束必须自己收掉本地服务，否则解压目录会被 python.exe 占住、
        # 用户删不掉文件夹。只停本包托管的 python，不碰其它进程。
        try {
            $stopScript = Join-Path $taskRoot 'mitm\stop_proxy.ps1'
            if (Test-Path -LiteralPath $stopScript) {
                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $stopScript -KeepGame | Out-Null
                Write-Output '  本地服务已收尾（系统代理已还原、服务进程已停）'
            }
        } catch { }
    }

    if (-not $found) {
        if ($script:captureRejected) {
            Write-Output 'Official System/Check rejected the client. See out/official-update-check.json; existing content was retained.'
            exit 4
        }
        Write-Output ''
        Write-Output '没抓到新的 Master/Get。可能原因：'
        Write-Output '  · 游戏没登录成功 -> 用 DMMGamePlayer 启动一次以刷新 pf_access_token'
        Write-Output '  · hook 尺寸与当前版本不符 -> 换匹配的 yaha_proxy.dll 再试'
        exit 1
    }

    Write-Output ''
    Write-Output 'Fresh official master received. Downloading and verifying its complete table set.'
    & $taskPython -B $downloadScript --root $taskRoot --cache $gameCache --since $captureStarted
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # 抓到了，重新同步
    Write-Output ''
    Write-Output '=== 用新表同步 ==='
    $retryArgs = @('-B', $taskScript, '--root', $taskRoot, '--cache', $gameCache)
    & $taskPython @retryArgs
    $taskExit = $LASTEXITCODE
    if ($taskExit -eq 0) {
        Write-Output ''
        & $taskPython -B $activityScript --root $taskRoot --since $captureStarted --save
        if ($LASTEXITCODE -in 2, 3) {
            Write-Output '  活动数据未抓全，已有的离线详情仍可使用。'
        } elseif ($LASTEXITCODE -ne 0) {
            Write-Output '  保存活动数据失败。'
        }
        Write-Output '新 master 已抓取并同步完成，现在可以直接「开始游戏」。'
    }
}

exit $taskExit
