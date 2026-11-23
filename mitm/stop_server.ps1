# Stop any running cache_server.py (local service)
$ErrorActionPreference = 'SilentlyContinue'
$killed = 0
Get-CimInstance Win32_Process -Filter "Name='python.exe'" | ForEach-Object {
    if ($_.CommandLine -and $_.CommandLine -like '*cache_server.py*') {
        Stop-Process -Id $_.ProcessId -Force
        $killed++
    }
}
if ($killed -gt 0) {
    Write-Host "       stopped $killed old service process(es)"
} else {
    Write-Host "       no running service"
}
