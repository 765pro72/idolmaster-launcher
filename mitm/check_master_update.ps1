# 保留旧的「检查更新.bat」入口；与启动器使用相同的本地同步流程。
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$taskEntry = Join-Path $PSScriptRoot 'grab_master.ps1'
if ($DryRun) { & $taskEntry -DryRun } else { & $taskEntry }
exit $LASTEXITCODE
