param([switch]$KeepGame)
$ErrorActionPreference='Stop'
$here=Split-Path -Parent $PSCommandPath;$root=Split-Path -Parent $here
& (Join-Path $here 'launcher_proxy_state.ps1') -Restore
$python=Join-Path $root 'python\python.exe'
$owned=@(Get-CimInstance Win32_Process -Filter "Name='python.exe'" -ErrorAction SilentlyContinue | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath -ieq $python -and $_.CommandLine -match '(?i)(?:^|[\\/\s"''])(cache_server|http1_bridge|offline_proxy)\.py(?:[\s"'']|$)'
})
foreach($process in $owned){
    $live=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.ProcessId) -ErrorAction SilentlyContinue
    if($live -and $live.ExecutablePath -ieq $python -and $live.CreationDate -eq $process.CreationDate){Stop-Process -Id $live.ProcessId -ErrorAction Stop}
}
if(-not $KeepGame){
    $record=Join-Path $root 'game_path.txt'
    if(Test-Path -LiteralPath $record){
        $exe=[IO.Path]::GetFullPath((Join-Path (Get-Content -LiteralPath $record -Raw).Trim() 'gakumas.exe'))
        foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='gakumas.exe'" -ErrorAction SilentlyContinue | Where-Object {$_.ExecutablePath -and $_.ExecutablePath -ieq $exe})){
            $live=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.ProcessId) -ErrorAction SilentlyContinue
            if($live -and $live.ExecutablePath -ieq $exe -and $live.CreationDate -eq $process.CreationDate){Stop-Process -Id $live.ProcessId -ErrorAction Stop}
        }
    }
}
Write-Host 'Selected package services stopped. Other games and external proxies preserved.'
