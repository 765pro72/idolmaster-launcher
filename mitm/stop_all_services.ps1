param([switch]$PlanOnly,[string]$ScopeRoot)
$ErrorActionPreference='Stop'
$here=Split-Path -Parent $PSCommandPath;$currentRoot=Split-Path -Parent $here
$stopStarted=[DateTime]::UtcNow
$mutex=New-Object Threading.Mutex($false,'Local\GkmStopAllLocalServices')
$locked=$false
function IsPackage([string]$root){
    return $root -and (Test-Path -LiteralPath (Join-Path $root 'mitm\cache_server.py')) -and (Test-Path -LiteralPath (Join-Path $root 'mitm\launch_game.ps1')) -and (Test-Path -LiteralPath (Join-Path $root '学マス启动器.exe'))
}
function InScope([string]$root){
    if(-not $ScopeRoot){return $true}
    $base=[IO.Path]::GetFullPath($ScopeRoot).TrimEnd('\')+'\'
    return ([IO.Path]::GetFullPath($root).TrimEnd('\')+'\').StartsWith($base,[StringComparison]::OrdinalIgnoreCase)
}
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public static class GkmStopArguments {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string value,out int count);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
 [DllImport("wininet.dll")] static extern bool InternetSetOption(IntPtr h,int o,IntPtr b,int n);
 public static void ProxyChanged(){InternetSetOption(IntPtr.Zero,39,IntPtr.Zero,0);InternetSetOption(IntPtr.Zero,37,IntPtr.Zero,0);}
 public static string[] Split(string value){int count;var p=CommandLineToArgvW(value,out count);if(p==IntPtr.Zero)return new string[0];try{var a=new string[count];for(int i=0;i<count;i++)a[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(p,i*IntPtr.Size));return a;}finally{LocalFree(p);}}
}
"@
$serviceNames=@('cache_server.py','http1_bridge.py','offline_proxy.py')
$toolNames=@('download_master_tables.py','local_content_sync.py','update_all.py','scsp_bridge.py','scsp_legacy_migration.py')
$taskNames=@('launch_game.ps1','grab_master.ps1','capture_master.ps1','capture_args.ps1','check_master_update.ps1','restore_proxy_after.ps1','full_uninstall.ps1','purge_all.ps1')
function Inventory {
    $result=@()
    foreach($p in @(Get-CimInstance Win32_Process -ErrorAction Stop)){
        if($p.ProcessId -eq $PID -or -not $p.CommandLine){continue}
        $argsList=[GkmStopArguments]::Split([string]$p.CommandLine)
        $root=$null;$role=$null;$name=$null
        if($p.Name -in @('python.exe','pythonw.exe') -and $p.ExecutablePath){
            $directory=Split-Path -Parent $p.ExecutablePath
            if((Split-Path -Leaf $directory) -ine 'python'){continue}
            $candidate=Split-Path -Parent $directory
            if(-not (IsPackage $candidate)){continue}
            for($i=1;$i -lt $argsList.Count;$i++){
                $arg=$argsList[$i]
                if($arg -in @('-c','-m')){break}
                if($arg.StartsWith('-')){continue}
                $file=[IO.Path]::GetFileName($arg)
                if($file -notin ($serviceNames+$toolNames)){break}
                if([IO.Path]::IsPathRooted($arg)){
                    $absolute=[IO.Path]::GetFullPath($arg)
                    if($absolute -ine (Join-Path (Join-Path $candidate 'mitm') $file) -and $absolute -ine (Join-Path (Join-Path $candidate 'tools') $file)){break}
                }
                $root=$candidate;$name=$file;$role=if($file -in $serviceNames){'service'}else{'tool'};break
            }
        }elseif($p.Name -in @('powershell.exe','pwsh.exe')){
            for($i=1;$i -lt $argsList.Count-1;$i++){
                if($argsList[$i] -ine '-File'){continue}
                $file=$argsList[$i+1]
                if(-not [IO.Path]::IsPathRooted($file)){break}
                $leaf=[IO.Path]::GetFileName($file);$folder=Split-Path -Parent $file
                if((Split-Path -Leaf $folder) -ine 'mitm' -or $leaf -notin $taskNames){break}
                $candidate=Split-Path -Parent $folder
                if(IsPackage $candidate){$root=$candidate;$name=$leaf;$role='task'}
                break
            }
        }
        if($root -and (InScope $root)){$result+= [pscustomobject]@{Pid=[int]$p.ProcessId;Created=$p.CreationDate;Exe=$p.ExecutablePath;Root=$root;Role=$role;Script=$name}}
    }
    return $result
}
function StopVerified($entry){
    $live=Get-CimInstance Win32_Process -Filter ('ProcessId='+$entry.Pid) -ErrorAction SilentlyContinue
    if($live -and $live.CreationDate -eq $entry.Created -and $live.ExecutablePath -eq $entry.Exe){
        Stop-Process -Id $entry.Pid -Force -ErrorAction Stop
        Write-Output ('Stopped '+$entry.Role+' '+$entry.Script+' (pid '+$entry.Pid+')')
    }
}
function RestoreInterruptedUpdateArguments([string]$root){
    $argsFile=Join-Path $root 'launch-args.json';$gamePathFile=Join-Path $root 'game_path.txt'
    if(-not (Test-Path -LiteralPath $gamePathFile)){return}
    $backups=@(Get-ChildItem -LiteralPath $root -File -Filter 'launch-args.json.grab-bak-*' -ErrorAction SilentlyContinue|Sort-Object LastWriteTimeUtc)
    foreach($backup in $backups){
        try{
            $saved=[IO.File]::ReadAllText($backup.FullName,[Text.Encoding]::UTF8)|ConvertFrom-Json
            $exe=[string]$saved.exe_path;$gameDir=[IO.File]::ReadAllText($gamePathFile,[Text.Encoding]::UTF8).Trim().Trim('"')
            if(-not $exe -or -not [IO.Path]::IsPathRooted($exe) -or [IO.Path]::GetFullPath((Split-Path -Parent $exe)) -ine [IO.Path]::GetFullPath($gameDir)){continue}
            [IO.File]::Copy($backup.FullName,$argsFile,$true)
            foreach($duplicate in $backups){Remove-Item -LiteralPath $duplicate.FullName -Force -ErrorAction SilentlyContinue}
            Write-Output 'Restored launch arguments saved before the interrupted update.'
            return
        }catch{Write-Output 'An interrupted update argument backup could not be restored automatically.';return}
    }
}
function StopConfiguredMltdSessions($packageRoots){
    $adapters=New-Object 'System.Collections.Generic.List[string]'
    foreach($root in $packageRoots){
        if(-not $root){continue}
        foreach($leaf in @('MLTD-adapter','MLTD')){$candidate=Join-Path $root $leaf;if(-not $adapters.Contains($candidate)){$adapters.Add($candidate)}}
    }
    $settings=if(-not $ScopeRoot -and $env:LOCALAPPDATA){Join-Path $env:LOCALAPPDATA 'MLTDLauncher\adapter-location.json'}else{$null}
    if($settings -and (Test-Path -LiteralPath $settings)){
        try{$raw=[IO.File]::ReadAllText($settings,[Text.Encoding]::UTF8);$saved=$raw|ConvertFrom-Json;$directory=[string]$saved.directory;if($directory -and [IO.Path]::IsPathRooted($directory) -and -not $adapters.Contains($directory)){$adapters.Add($directory)}}catch{Write-Output 'MLTD adapter path settings are invalid; package-local stop checks will continue.'}
    }
    foreach($adapter in @($adapters|Select-Object -Unique)){
        try{
            $adapter=[IO.Path]::GetFullPath($adapter);$node=Join-Path $adapter 'runtime-node\node.exe';$entry=Join-Path $adapter 'start-mltd-install.cjs'
            $lock=Join-Path $adapter 'state\desktop-managed-session-v2.json';$sessions=Join-Path $adapter 'runtime\desktop-sessions'
            if(-not (Test-Path -LiteralPath $node -PathType Leaf) -or -not (Test-Path -LiteralPath $entry -PathType Leaf)){continue}
            if(-not (Test-Path -LiteralPath $lock -PathType Leaf) -and -not (Test-Path -LiteralPath $sessions -PathType Container)){continue}
            $start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$node;$start.Arguments='"'+$entry+'" --stop-desktop-service';$start.WorkingDirectory=$adapter
            $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
            $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$start.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
            $worker=New-Object Diagnostics.Process;$worker.StartInfo=$start
            try{
                [void]$worker.Start();$stdout=$worker.StandardOutput.ReadToEndAsync();$stderr=$worker.StandardError.ReadToEndAsync()
                if(-not $worker.WaitForExit(65000)){$worker.Kill();[void]$worker.WaitForExit();throw 'Managed MLTD service stop timed out.'}
                $output=$stdout.GetAwaiter().GetResult().Trim();$errorText=$stderr.GetAwaiter().GetResult().Trim()
                if($worker.ExitCode -ne 0){throw ('MLTD stop returned exit '+$worker.ExitCode+': '+$errorText)}
                $result=$null;try{$result=$output|ConvertFrom-Json}catch{}
                if($result.status -eq 'no-managed-service'){continue}
                if($result.stopped -eq $true){Write-Output ('Stopped managed MLTD game and service ('+$result.status+').')}
                else{throw ('MLTD stop did not confirm completion: '+$output)}
            }finally{$worker.Dispose()}
        }catch{$script:mltdStopFailed=$true;Write-Output ('MLTD stop failed: '+$_.Exception.Message)}
    }
}
try{
    if($PlanOnly){@(Inventory)|Select-Object Pid,Root,Role,Script|ConvertTo-Json -Depth 4;exit 0}
    try{$locked=$mutex.WaitOne(15000)}catch [Threading.AbandonedMutexException]{$locked=$true}
    if(-not $locked){Write-Output 'A global stop is still running; completion not yet confirmed.';exit 1}
    $known=@{};$quiet=0;$lastCount=0;$proxyRoot=$null;$script:mltdStopFailed=$false
    $initial=@(Inventory)
    foreach($entry in $initial){$known[$entry.Root]=$true;if($entry.Script -eq 'offline_proxy.py'){$proxyRoot=$entry.Root}}
    if(IsPackage $currentRoot){if(InScope $currentRoot){$known[$currentRoot]=$true}}
    foreach($root in @($known.Keys)){
        $out=Join-Path $root 'out';[IO.Directory]::CreateDirectory($out)|Out-Null
        [IO.File]::WriteAllText((Join-Path $out 'stop-request.txt'),$stopStarted.ToString('o'))
    }
    # Stop blocking workers/monitors first. Updated launch/update parents get a
    # short window to run their finally blocks and restore temporary arguments.
    foreach($entry in $initial|Where-Object {$_.Role -ne 'task' -or $_.Script -eq 'restore_proxy_after.ps1'}){StopVerified $entry}
    StopConfiguredMltdSessions (@($known.Keys)+@($currentRoot))
    Start-Sleep -Milliseconds 900
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    while([DateTime]::UtcNow -lt $deadline){
        $rows=@(Inventory);$lastCount=$rows.Count
        if($rows.Count -eq 0){$quiet++;if($quiet -ge 3){break}}
        else{
            $quiet=0
            foreach($entry in $rows){
                if(-not $known.ContainsKey($entry.Root)){
                    $known[$entry.Root]=$true;[IO.Directory]::CreateDirectory((Join-Path $entry.Root 'out'))|Out-Null
                    [IO.File]::WriteAllText((Join-Path $entry.Root 'out\stop-request.txt'),$stopStarted.ToString('o'))
                }
                if($entry.Role -ne 'task' -or $entry.Script -eq 'restore_proxy_after.ps1' -or ([DateTime]::UtcNow-$stopStarted).TotalSeconds -ge 3){StopVerified $entry}
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if(-not $ScopeRoot){
        $restoreRoot=if($proxyRoot){$proxyRoot}else{$currentRoot}
        $restore=Join-Path $restoreRoot 'mitm\launcher_proxy_state.ps1'
        try{if(Test-Path -LiteralPath $restore){& $restore -Restore}}catch{Write-Output ('Proxy restore: '+$_.Exception.Message)}
        # Never leave an enabled dead launcher endpoint; preserve external proxies.
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings';$settings=Get-ItemProperty $key
        $pieces=@(([string]$settings.ProxyServer)-split ';'|Where-Object {$_})
        $local=@($pieces|Where-Object {(($_ -split '=',2)[-1]).Trim() -match '^(?i)(?:https?://)?(?:127\.0\.0\.1|localhost|\[::1\]):7899/?$'})
        if($local.Count -gt 0 -and $local.Count -eq $pieces.Count){Set-ItemProperty $key -Name ProxyEnable -Value 0;[GkmStopArguments]::ProxyChanged()}
    }
    foreach($root in @($known.Keys)){
        RestoreInterruptedUpdateArguments ([string]$root)
        $flag=Join-Path $root 'online-capture.flag'
        if(Test-Path -LiteralPath $flag){Remove-Item -LiteralPath $flag -Force}
    }
    $remaining=@(Inventory)
    if($remaining.Count -or $script:mltdStopFailed){Write-Output ('Stop incomplete: '+$remaining.Count+' managed process(es) remain; MLTD stop failed='+$script:mltdStopFailed+'.');exit 1}
    Write-Output 'All managed Gakumas services, update workers and launch monitors are stopped. External proxies and unrelated programs were preserved.'
    exit 0
}finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}
