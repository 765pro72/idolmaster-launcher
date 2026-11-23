param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$buildOutput = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $buildOutput) { throw 'Output already exists. Use a fresh path and preserve the original executable.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler not found.' }
$sourceFiles = @('Launcher.cs','LauncherErrors.cs','GameExperience.cs','GameWordmarks.cs','HomeLayout.cs','TicketEmblems.cs','CgssEmblem.cs','CgssWordmark.cs','MillionEmblem.cs','MillionPage.cs','ScspService.cs','ScspPage.cs','CgssPage.cs','NormalizeEnvironment.cs','GameDirectoryFinder.cs','MillionResourceService.cs','MillionResourcesDialog.cs','MillionDesktopSessionService.cs','MillionDesktopLaunchDialog.cs')
$resourceFiles = @(@('launcher.ico','gakumas.ico'),@('assets\shiny.ico','shiny.ico'),@('assets\cgss.ico','cgss.ico'),@('assets\cgss-wordmark.png','cgss-wordmark.png'),@('assets\cgss-wordmark.paths','cgss-wordmark.paths'),@('assets\million-logo.png','million-logo.png'),@('assets\million.ico','million.ico'))
$arguments = @('/nologo','/warn:4','/optimize+','/codepage:65001','/utf8output','/target:winexe','/main:MainForm',('/out:'+$buildOutput),('/win32icon:'+(Join-Path $PSScriptRoot 'launcher.ico')),('/win32manifest:'+(Join-Path $PSScriptRoot 'launcher.manifest')))
foreach ($resource in $resourceFiles) { $arguments += '/resource:'+(Join-Path $PSScriptRoot $resource[0])+','+$resource[1] }
foreach ($reference in @('System.Windows.Forms.dll','System.Drawing.dll','System.Core.dll','System.Web.Extensions.dll')) { $arguments += '/r:'+$reference }
foreach ($sourceFile in $sourceFiles) { $arguments += Join-Path $PSScriptRoot $sourceFile }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Integrated launcher compilation failed.' }
if (-not (Test-Path -LiteralPath $buildOutput -PathType Leaf)) { throw 'Compiler did not create the output.' }
$buildHash = [Security.Cryptography.SHA256]::Create()
$buildStream = [IO.File]::OpenRead($buildOutput)
try { Write-Output ('SHA256 '+([BitConverter]::ToString($buildHash.ComputeHash($buildStream))).Replace('-','').ToLowerInvariant()) }
finally { $buildStream.Dispose(); $buildHash.Dispose() }
Write-Output ('PowerShell '+$PSVersionTable.PSVersion.ToString())
