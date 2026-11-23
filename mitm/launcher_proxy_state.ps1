param([switch]$Restore)
$ErrorActionPreference='Stop'
$reg='HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$here=Split-Path -Parent $PSCommandPath
$root=Split-Path -Parent $here
function ProxyParts([string]$value){
    @($value -split ';' | ForEach-Object { $s=($_ -split '=',2)[-1].Trim();if($s){$s} })
}
function IsOwnedEndpoint([string]$value){
    return $value -match '^(?i)(?:https?://)?(?:127\.0\.0\.1|localhost|\[::1\]):7899/?$'
}
$current=Get-ItemProperty -LiteralPath $reg
$parts=@(ProxyParts ([string]$current.ProxyServer))
$owned=@($parts | Where-Object {IsOwnedEndpoint $_})
if(-not $Restore){[pscustomobject]@{Enabled=[int]$current.ProxyEnable;OwnedEndpoint=($owned.Count -gt 0);Mixed=($owned.Count -gt 0 -and $owned.Count -ne $parts.Count)};return}
# A user changing Clash must win over a delayed launcher watcher.
if($owned.Count -eq 0){Write-Output 'Proxy preserved: current endpoint is not the launcher proxy.';return}
if($owned.Count -ne $parts.Count){throw 'Mixed launcher and external proxies. Change the system proxy in Clash; no settings were overwritten.'}
$savedFile=Join-Path $here 'proxy.saved.json'
$enable=0;$server=''
if(Test-Path -LiteralPath $savedFile){
    $saved=Get-Content -LiteralPath $savedFile -Raw | ConvertFrom-Json
    if([int]$saved.Enable -notin @(0,1)){throw 'Invalid saved proxy state; no settings were overwritten.'}
    $prior=@(ProxyParts ([string]$saved.Server))
    if(@($prior | Where-Object {IsOwnedEndpoint $_}).Count){throw 'Saved proxy points back to the launcher. Re-enable the system proxy in Clash first.'}
    $enable=[int]$saved.Enable;$server=[string]$saved.Server
    if($enable -eq 1 -and $prior.Count -eq 0){throw 'Saved enabled proxy has no endpoint. Re-enable the system proxy in Clash.'}
    foreach($endpoint in $prior){
        if($enable -eq 1 -and $endpoint -match '^(?i)(?:https?://)?(127\.0\.0\.1|localhost):([0-9]{1,5})/?$'){
            $testClient=New-Object Net.Sockets.TcpClient
            try{if(-not $testClient.ConnectAsync($Matches[1],[int]$Matches[2]).Wait(1000) -or -not $testClient.Connected){throw 'Saved local proxy is not listening. Re-enable the system proxy in Clash.'}}finally{$testClient.Dispose()}
        }
    }
}
$directory=Join-Path $root 'out\proxy-recovery'
[IO.Directory]::CreateDirectory($directory)|Out-Null
$backup=Join-Path $directory ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N')+'.json')
@{Enable=[int]$current.ProxyEnable;Server=[string]$current.ProxyServer;Time=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $backup -Encoding UTF8
$fresh=Get-ItemProperty -LiteralPath $reg
if([string]$fresh.ProxyServer -ne [string]$current.ProxyServer -or [int]$fresh.ProxyEnable -ne [int]$current.ProxyEnable){throw 'Proxy changed during recovery; current user selection preserved.'}
Set-ItemProperty -LiteralPath $reg -Name ProxyServer -Value $server
Set-ItemProperty -LiteralPath $reg -Name ProxyEnable -Value $enable
if(-not ('GkmProxyRecovery.Native' -as [type])){Add-Type -Namespace GkmProxyRecovery -Name Native -MemberDefinition '[DllImport("wininet.dll")] public static extern bool InternetSetOption(IntPtr h,int o,IntPtr b,int l);'}
[void][GkmProxyRecovery.Native]::InternetSetOption([IntPtr]::Zero,39,[IntPtr]::Zero,0)
[void][GkmProxyRecovery.Native]::InternetSetOption([IntPtr]::Zero,37,[IntPtr]::Zero,0)
Write-Output 'Launcher proxy restored. Clash/user-selected proxies were not replaced.'
