# Capture gakumas.exe login parameters before DMM's DRM wipes the process PEB.
#
# Two-stage detection per poll:
#   1) PEB direct read (NtQueryInformationProcess + ReadProcessMemory) - microseconds
#   2) fall back to Get-CimInstance - 30~60ms
# Stage 1 is what makes slow-polling machines workable; stage 2 keeps compatibility.

param(
    [int]$TimeoutSec = 300,
    [switch]$NoPause
)

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

$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = Split-Path -Parent $Here
$ArgsFile = Join-Path $Root 'launch-args.json'
$LogFile = Join-Path $Here 'capture.log'

function Write-Log([string]$msg) {
    $line = '[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss.fff'), $msg
    try { Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8 } catch { }
}

function Parse-Cmd([string]$cmdline) {
    $out = @{}
    foreach ($k in @('viewer_id', 'onetime_token', 'open_id', 'pf_access_token')) {
        $m = [regex]::Match($cmdline, '/' + $k + '=([^\s"]+)')
        if ($m.Success) { $out[$k] = $m.Groups[1].Value }
    }
    return $out
}

function Resolve-CapturedExe([string]$DetectedPath, [string]$PackageRoot) {
    if ($DetectedPath -and (Test-Path -LiteralPath $DetectedPath -PathType Leaf)) { return $DetectedPath }
    $recorded = Join-Path $PackageRoot 'game_path.txt'
    if (Test-Path -LiteralPath $recorded -PathType Leaf) {
        $directory = (Get-Content -LiteralPath $recorded -Encoding UTF8 -TotalCount 1).Trim().Trim([char]34)
        if ($directory) {
            $candidate = Join-Path $directory 'gakumas.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    # Old in-game placement remains compatible only when the exe really exists.
    $legacy = Join-Path (Split-Path -Parent $PackageRoot) 'gakumas.exe'
    if (Test-Path -LiteralPath $legacy -PathType Leaf) { return $legacy }
    return $null
}

# ---- PEB reader (compiled once) ----
$pebReady = $false
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class PebReader
{
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr baseAddress, byte[] buffer, int size, out int bytesRead);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int PROCESS_VM_READ = 0x0010;
    const int PEB_PROCESS_PARAMETERS = 0x20;
    const int RTL_USER_PROCESS_PARAMETERS_COMMANDLINE = 0x70;

    static bool ReadBytes(IntPtr hProc, IntPtr addr, int count, out byte[] data)
    {
        data = new byte[count];
        int read;
        if (!ReadProcessMemory(hProc, addr, data, count, out read) || read != count)
            return false;
        return true;
    }

    public static string GetCommandLine(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            int retLen;
            int status = NtQueryInformationProcess(hProc, 0, ref pbi, Marshal.SizeOf(pbi), out retLen);
            if (status != 0) return "ERR_NTQ_" + status;
            if (pbi.PebBaseAddress == IntPtr.Zero) return "ERR_PEB_NULL";

            byte[] ptrBuf;
            if (!ReadBytes(hProc, (IntPtr)((long)pbi.PebBaseAddress + PEB_PROCESS_PARAMETERS), 8, out ptrBuf)) return "ERR_READ_PP";
            long ppAddr = BitConverter.ToInt64(ptrBuf, 0);
            if (ppAddr == 0) return "ERR_PP_NULL";

            byte[] usBuf;
            if (!ReadBytes(hProc, (IntPtr)(ppAddr + RTL_USER_PROCESS_PARAMETERS_COMMANDLINE), 16, out usBuf)) return "ERR_READ_US";
            ushort len = BitConverter.ToUInt16(usBuf, 0);
            long bufAddr = BitConverter.ToInt64(usBuf, 8);
            if (len == 0) return "ERR_LEN0";
            if (bufAddr == 0) return "ERR_BUF_NULL";
            if (len > 8192) return "ERR_LEN_TOO_BIG_" + len;

            byte[] strBuf;
            if (!ReadBytes(hProc, (IntPtr)bufAddr, len, out strBuf)) return "ERR_READ_STR";
            string s = Encoding.Unicode.GetString(strBuf);
            return "OK:" + s;
        }
        catch (Exception ex) { return "ERR_EX_" + ex.GetType().Name; }
        finally { CloseHandle(hProc); }
    }

    /// <summary>Probe: can we even open the process?</summary>
    public static string ProbeOpen(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero)
            return "OPEN_DENIED_" + Marshal.GetLastWin32Error();
        CloseHandle(h);
        return "OPEN_OK";
    }
}
'@ -Language CSharp -ErrorAction Stop
    $pebReady = $true
} catch {
    Write-Log ('PEB reader compile failed: ' + $_.Exception.Message)
}

try {
    Set-Content -LiteralPath $LogFile -Value ('=== capture start {0} timeout={1}s peb={2} ===' -f (Get-Date).ToString('HH:mm:ss'), $TimeoutSec, $pebReady) -Encoding UTF8
} catch { }

Write-Host ''
Write-Host ' ============================================================' -ForegroundColor Cyan
Write-Host '   gakumas 登录参数抓取' -ForegroundColor Cyan
Write-Host ' ============================================================' -ForegroundColor Cyan
Write-Host ''

# 顺序检查
$before = @()
Get-Process -Name gakumas -ErrorAction SilentlyContinue | ForEach-Object { $before += $_.Id }
if ($before.Count -gt 0) {
    Write-Log ('ABORT: {0} gakumas already running' -f $before.Count)
    Write-Host ("  [错误] 检测到 {0} 个 gakumas 进程已经在运行" -f $before.Count) -ForegroundColor Red
    Write-Host ''
    Write-Host '  顺序错了：必须【先】在启动器里点「开始游戏」，【再】从 DMM 启动游戏' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '  正确步骤：' -ForegroundColor Cyan
    Write-Host '    1. 完全关掉游戏（任务管理器确认没有 gakumas.exe）'
    Write-Host '    2. 在启动器里点「开始游戏」'
    Write-Host '    3. 等它显示「请从 DMMGamePlayer 启动游戏」'
    Write-Host '    4. 再从 DMM 点「ゲームスタート」'
    Write-Host ''
    Write-Host '  如果关掉游戏后仍报这个错，等 10 秒再试（进程退出有延迟）'
    Write-Host ''
    if (-not $NoPause) { Read-Host '  按回车关闭' }
    exit 1
}

Write-Host '  现在请从 DMMGamePlayer 启动游戏（点「ゲームスタート」）' -ForegroundColor Yellow
Write-Host '  本窗口会自动检测，抓到就保存' -ForegroundColor Yellow
Write-Host ''
Write-Host "  最长等待 $TimeoutSec 秒..." -ForegroundColor DarkGray
if ($pebReady) {
    Write-Host '  探测方式: PEB 直读 + CIM 兜底' -ForegroundColor DarkGray
} else {
    Write-Host '  探测方式: 仅 CIM（PEB 不可用）' -ForegroundColor DarkYellow
}
Write-Host ''
Write-Log 'waiting for gakumas.exe to appear'

# 抓票期间只拦 gakumas.exe 的出站流量（绝不改系统代理，避免把整机网络弄断）。
# DMM 的 onetime_token 是一次性的：游戏一登录就被用掉，之后更新只剩废票。
$script:capFwRule = 'gkm-capture-block'
$script:capFwAdded = $false
try {
    Remove-NetFirewallRule -DisplayName $script:capFwRule -ErrorAction SilentlyContinue
    $gpFile = Join-Path $Root 'game_path.txt'
    $gdir = $null
    if (Test-Path -LiteralPath $gpFile) { $gdir = (Get-Content -LiteralPath $gpFile -Encoding UTF8 -TotalCount 1) }
    if ($gdir) { $gdir = $gdir.Trim().Trim([char]34) }
    $exe = $null
    if ($gdir) { $exe = Join-Path $gdir 'gakumas.exe' }
    if ($exe -and (Test-Path -LiteralPath $exe)) {
        New-NetFirewallRule -DisplayName $script:capFwRule -Direction Outbound -Program $exe -Action Block -Profile Any | Out-Null
        $script:capFwAdded = $true
        Write-Host '  [保护] 已拦截游戏出站流量，登录票不会被用掉（抓完自动解除）' -ForegroundColor DarkGray
        Write-Log 'firewall outbound block added'
    }
} catch { Write-Log ('firewall block failed: ' + $_.Exception.Message) }

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$result = $null
$attempts = 0
$pebHits = 0
$cimHits = 0
$seen = @{}
$t0 = Get-Date

while ((Get-Date) -lt $deadline) {
    $attempts++
    $now = @(Get-Process -Name gakumas -ErrorAction SilentlyContinue)
    foreach ($p in $now) {
        if ($before -contains $p.Id) { continue }

        # ---- 1) PEB 直读（最快）----
        $cl = $null
        $pebNote = ''
        if ($pebReady) {
            $raw = [PebReader]::GetCommandLine($p.Id)
            if ($raw -and $raw.StartsWith('OK:')) {
                $body = $raw.Substring(3)
                if ($body -match 'viewer_id=') {
                    $cl = $body
                    $pebHits++
                    $pebNote = 'PEB_OK'
                } else {
                    $pebNote = 'PEB_NO_VIEWER(len=' + $body.Length + ')'
                }
            } else {
                $pebNote = if ($raw) { $raw } else { 'PEB_NULL' }
            }
        }

        # ---- 2) CIM 兜底 ----
        if (-not $cl) {
            try {
                $ci = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $p.Id) -ErrorAction SilentlyContinue
                if ($ci -and $ci.CommandLine -and $ci.CommandLine -match 'viewer_id=') {
                    $cl = $ci.CommandLine
                    $cimHits++
                }
            } catch { }
        }

        if ($cl) {
            $parsed = Parse-Cmd $cl
            if ($parsed['viewer_id']) {
                $exePath = $null
                try { $exePath = $p.Path } catch { }
                if (-not $exePath) {
                    try {
                        $ci2 = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $p.Id) -ErrorAction SilentlyContinue
                        if ($ci2) { $exePath = $ci2.ExecutablePath }
                    } catch { }
                }
                $exePath = Resolve-CapturedExe $exePath $Root
                if (-not $exePath) {
                    Write-Log 'Game executable could not be resolved; no invalid launch file was saved.'
                    continue
                }

                $obj = [ordered]@{
                    exe_path        = $exePath
                    viewer_id       = $parsed['viewer_id']
                    onetime_token   = $parsed['onetime_token']
                    open_id         = $parsed['open_id']
                    pf_access_token = $parsed['pf_access_token']
                }
                ($obj | ConvertTo-Json) | Set-Content -LiteralPath $ArgsFile -Encoding UTF8
                Write-Log ('OK pid={0} viewer={1} cmdlen={2}' -f $p.Id, $parsed['viewer_id'], $cl.Length)
                $result = $obj
                break
            }
        }

        if (-not $seen.ContainsKey($p.Id)) {
            $seen[$p.Id] = $true
            $probe = ''
            if ($pebReady) {
                try { $probe = [PebReader]::ProbeOpen($p.Id) } catch { $probe = 'PROBE_EX' }
            }
            Write-Log ('pid={0} present but CommandLine empty | peb={1} | {2}' -f $p.Id, $pebNote, $probe)
        }
    }
    if ($result) { break }
    if (-not $pebReady) { Start-Sleep -Milliseconds 2 }
}

$elapsed = [int]((Get-Date) - $t0).TotalMilliseconds
Write-Log ('end: result={0} elapsed={1}ms attempts={2} pebHits={3} cimHits={4}' -f $result, $elapsed, $attempts, $pebHits, $cimHits)

# 解除抓票期间的防火墙拦截（无论成败都解除）
try {
    Remove-NetFirewallRule -DisplayName 'gkm-capture-block' -ErrorAction SilentlyContinue
    if ($script:capFwAdded) {
        Write-Host '  [保护] 已解除游戏出站拦截' -ForegroundColor DarkGray
        Write-Log 'firewall outbound block removed'
    }
    $script:capFwAdded = $false
} catch { }

Write-Host ''
if ($result) {
    Write-Host '  [成功] 参数已保存' -ForegroundColor Green
    Write-Host ''
    Write-Host ("  viewer : {0}" -f $result['viewer_id'])
    Write-Host ("  耗时   : {0} ms（轮询 {1} 次，PEB={2} CIM={3}）" -f $elapsed, $attempts, $pebHits, $cimHits)
    Write-Host ''
    Write-Host '  以后在启动器里点「开始游戏」就能直接进游戏，不用开 DMM' -ForegroundColor Cyan
    Write-Host '  注意：token 会过期（几小时到一天）。过期后删掉 launch-args.json 再点「开始游戏」即可' -ForegroundColor DarkGray
}
else {
    Write-Host ("  [超时] {0} 秒内没抓取到参数" -f $TimeoutSec) -ForegroundColor Red
    Write-Host ''
    Write-Host ("  共看到 {0} 个新进程，但 CommandLine 都读不到" -f $seen.Count)
    Write-Host ''
    Write-Host '  日志: mitm\capture.log'
    Write-Host '  那个文件会记录 PEB / CIM 各命中几次，发作者能直接定位'
}

Write-Host ''
if (-not $NoPause) { Read-Host '  按回车关闭' }
