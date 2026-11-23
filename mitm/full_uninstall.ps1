# 完整卸载 —— 由启动器的「卸载解锁组件」调用
# 目标：点完这个，网络和游戏都恢复原样，不留任何残留。
# 不删除包目录本身（用户可能还要用启动器）。
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

$ErrorActionPreference = 'SilentlyContinue'
$Here = Split-Path -Parent $PSCommandPath          # ...\mitm
$Root = Split-Path -Parent $Here                   # 包根
$log  = Join-Path $env:TEMP ('gkm_uninstall_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.log')

function Step([string]$t) { Write-Host $t }
function Log([string]$t) {
    Write-Host $t
    Add-Content -LiteralPath $log -Value $t -Encoding UTF8 -ErrorAction SilentlyContinue
}

Log ''
Log '=== 卸载解锁组件 ==='

# ---------- 1. 停止所有相关进程 ----------
Log '[1/6] 停止游戏与本地服务'
foreach ($p in (Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)) {
    $isTarget = $false
    if ($p.Name -eq 'gakumas.exe') { $isTarget = $true }
    if ($p.Name -eq 'UnityCrashHandler64.exe') { $isTarget = $true }
    if ($p.Name -eq 'python.exe' -and $p.CommandLine -and
        ($p.CommandLine -like '*cache_server.py*' -or $p.CommandLine -like '*offline_proxy.py*')) { $isTarget = $true }
    if ($isTarget) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        Log ('  停止 ' + $p.Name + ' pid=' + $p.ProcessId)
    }
}
Start-Sleep -Milliseconds 800

# ---------- 2. 还原游戏目录里的 yaha DLL ----------
Log '[2/6] 还原游戏文件'
$gpFile = Join-Path $Root 'game_path.txt'
$yahaName = 'Cysharp.Net.Http.YetAnotherHttpHandler.Native'
$gdir = if (Test-Path $gpFile) { (Get-Content $gpFile -Raw).Trim() } else { '' }
if ($gdir -and (Test-Path $gdir)) {
    $pd = Join-Path $gdir 'gakumas_Data\Plugins\x86_64'
    $liveDll = Join-Path $pd ($yahaName + '.dll')
    $origDll = Join-Path $pd ($yahaName + '.orig.dll')
    $pkgOrig = Join-Path $Root ('hook\' + $yahaName + '.orig.dll')
    if (Test-Path $liveDll) {
        $src = if (Test-Path $origDll) { $origDll } elseif (Test-Path $pkgOrig) { $pkgOrig } else { '' }
        if ($src) {
            Copy-Item -LiteralPath $src -Destination $liveDll -Force
            Log '  已还原官方 DLL'
            if (Test-Path $origDll) {
                Remove-Item -LiteralPath $origDll -Force
                Log '  已删除备份 DLL'
            }
        } else {
            Log '  [警告] 找不到原版备份，请用 DMMGamePlayer 修复游戏'
        }
        # 顺手删掉日志残留
        $yl = Join-Path $pd 'yaha-proxy.log'
        if (Test-Path $yl) { Remove-Item -LiteralPath $yl -Force; Log '  已删除 yaha 日志' }
    }
} else {
    Log '  没有游戏路径记录，跳过'
}

# ---------- 3. 恢复系统代理 ----------
Log '[3/6] 恢复系统代理'
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$saved = Join-Path $Here 'proxy.saved.json'
$cur = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
if (Test-Path $saved) {
    try {
        $s = Get-Content -LiteralPath $saved -Raw | ConvertFrom-Json
        if ($s.Server) { Set-ItemProperty -Path $k -Name ProxyServer -Value ([string]$s.Server) }
        Set-ItemProperty -Path $k -Name ProxyEnable -Value ([int]$s.Enable)
        Log ('  已恢复为 Enable=' + $s.Enable + ' Server=' + $s.Server)
        Remove-Item -LiteralPath $saved -Force
    } catch { Log '  proxy.saved.json 解析失败，改为直接关闭代理' }
} elseif ($cur -and $cur.ProxyServer -eq '127.0.0.1:7899') {
    Set-ItemProperty -Path $k -Name ProxyEnable -Value 0
    Log '  代理指向本包端口，已关闭'
} else {
    Log ('  无需处理 (ProxyEnable=' + $cur.ProxyEnable + ')')
}
if ($cur -and $cur.AutoConfigURL -and $cur.AutoConfigURL -match '127\.0\.0\.1') {
    Remove-ItemProperty -Path $k -Name AutoConfigURL -ErrorAction SilentlyContinue
    Log '  已清理 AutoConfigURL'
}
# 刷新 WinINET，避免部分程序仍用缓存
try {
    $sig = '[DllImport("wininet.dll", SetLastError=true)] public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);'
    Add-Type -MemberDefinition $sig -Name Uninst -Namespace GkmOff -ErrorAction SilentlyContinue | Out-Null
    [void][GkmOff.Uninst]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0)
    [void][GkmOff.Uninst]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0)
} catch { }

# ---------- 4. 删除 CA 证书 ----------
# 用 X509Store：certutil -user -delstore 在 CurrentUser\Root 上会失败
Log '[4/6] 删除本地 CA 证书'
$n = 0
foreach ($scope in @('CurrentUser','LocalMachine')) {
    try {
        $st = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', $scope)
        $st.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $found = @($st.Certificates | Where-Object { $_.Subject -like '*GKM*' })
        foreach ($c in $found) {
            try {
                $st.Remove($c)
                Log ('  删除 ' + $scope + ' ' + $c.Thumbprint)
                $n++
            } catch { Log ('  删除失败: ' + $_.Exception.Message) }
        }
        $st.Close()
    } catch { Log ('  ' + $scope + ' 失败: ' + $_.Exception.Message) }
}
if ($n -eq 0) { Log '  没有找到本包的证书' }

# ---------- 5. 结束残留的端口占用 ----------
Log '[5/6] 清理残留端口'
foreach ($port in @(7899,18443,18444)) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($c) {
        Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
        Log ('  ' + $port + ' 已释放 (pid ' + $c.OwningProcess + ')')
    }
}

# ---------- 6. 清理运行时文件 ----------
Log '[6/6] 清理运行时文件'
foreach ($rel in @('launch-args.json','game_path.txt','mitm\mitm.log','mitm\proxy_out','mitm\__pycache__')) {
    $p = Join-Path $Root $rel
    if (Test-Path $p) {
        Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
        Log ('  删除 ' + $rel)
    }
}

Log ''
Log '=== 卸载完成，网络和游戏都已恢复 ==='
Log ('日志: ' + $log)
exit 0
