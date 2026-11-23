param([switch]$ProbeOnly)
# Only watches the selected game's executable and only stops this package's proxy.
$ErrorActionPreference='Stop'
$here=Split-Path -Parent $PSCommandPath;$root=Split-Path -Parent $here
$gameFile=Join-Path $root 'game_path.txt'
if(-not (Test-Path -LiteralPath $gameFile)){return}
$gameDir=(Get-Content -LiteralPath $gameFile -Raw).Trim()
$game=[IO.Path]::GetFullPath((Join-Path $gameDir 'gakumas.exe'))
if (-not ('GkmProxyProcessPath' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class GkmProxyProcessPath {
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr process);
    public static string Read(int pid) {
        IntPtr process=OpenProcess(0x1000,false,pid);
        if(process==IntPtr.Zero)return null;
        try {uint size=32768;var path=new StringBuilder((int)size);
            return QueryFullProcessImageName(process,0,path,ref size)?path.ToString():null;
        } finally {CloseHandle(process);}
    }
}
"@
}
function SelectedGameRunning {
    $unresolved=$false
    $rows=@(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue)
    foreach($row in $rows) {
        $image=[string]$row.ExecutablePath
        if(-not $image){$image=[GkmProxyProcessPath]::Read([int]$row.ProcessId)}
        if($image -and $image -ieq $game){return $true}
        # A protected DRM child can be visible by name but hide its image path.
        # An inconclusive observation must never tear down a live game's proxy.
        if(-not $image){$unresolved=$true}
    }
    return $unresolved
}
if($ProbeOnly){Write-Output ([bool](SelectedGameRunning));return}
$deadline=[DateTime]::UtcNow.AddSeconds(90)
while(-not (SelectedGameRunning)){if([DateTime]::UtcNow -ge $deadline){return};Start-Sleep -Milliseconds 500}
while(SelectedGameRunning){Start-Sleep -Seconds 2}
Start-Sleep -Seconds 2
if(SelectedGameRunning){return}
& (Join-Path $here 'launcher_proxy_state.ps1') -Restore
# A newer instance of the same game may have started during recovery.
if(SelectedGameRunning){return}
$connection=Get-NetTCPConnection -LocalPort 7899 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if($connection){
    $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$connection.OwningProcess) -ErrorAction SilentlyContinue
    $python=Join-Path $root 'python\python.exe'
    if($process -and $process.ExecutablePath -ieq $python -and $process.CommandLine -match '(?i)(?:^|[\\/\s"''])offline_proxy\.py(?:[\s"'']|$)'){
        $live=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.ProcessId) -ErrorAction SilentlyContinue
        if($live -and $live.CreationDate -eq $process.CreationDate -and $live.ExecutablePath -ieq $python){Stop-Process -Id $live.ProcessId -ErrorAction Stop}
    }
}
