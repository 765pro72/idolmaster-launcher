# Locate the folder that contains gakumas.exe.
# Prints exactly ONE path on success (stdout); exits 1 when nothing is found.
# All progress goes to stderr, so a caller can capture stdout safely.
# ASCII only on purpose: PowerShell 5.1 misreads BOM-less UTF-8 as ANSI.
#
# Design notes:
#  * NEVER uses Get-ChildItem -Recurse: a single call on a huge folder cannot be
#    interrupted and is what made installation appear to hang.
#  * Walks at most two levels below each drive root, one directory handle at a
#    time, checking the time budget between every entry.
#  * The game folder may have ANY name (e.g. <drive>\xms\gakumas); nothing is
#    hard-coded beyond a few likely spots probed first.
param([int]$BudgetSec = 30)

$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference    = 'SilentlyContinue'
$deadline = (Get-Date).AddSeconds($BudgetSec)
$script:Hit = $null

function Note([string]$m) { [Console]::Error.WriteLine($m) }
function TimeUp { return ((Get-Date) -gt $deadline) }

# Record the folder if it holds gakumas.exe. Returns a bool only: never writes
# to the pipeline, so a caller cannot accidentally swallow the result.
function Probe([string]$p) {
    if (-not $p) { return $false }
    if (Test-Path -LiteralPath (Join-Path $p 'gakumas.exe')) {
        $script:Hit = $p
        return $true
    }
    return $false
}
function Done { Write-Output $script:Hit; exit 0 }

function SubDirs([string]$path) {
    try { return [System.IO.Directory]::EnumerateDirectories($path) }
    catch { return @() }
}

# ---- 0) the path recorded next to this package -------------------------
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$rec  = Join-Path $root 'game_path.txt'
if (Test-Path -LiteralPath $rec) {
    $line = Get-Content -LiteralPath $rec -Encoding UTF8 -TotalCount 1
    if ($line) {
        $line = $line.Trim().Trim('"').TrimEnd('\')
        if (Probe $line) { Done }
    }
}

# ---- 1) a running game wins: its folder is authoritative --------------
$proc = Get-Process gakumas -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc) {
    $ep = $proc.Path
    if (-not $ep) { $ep = $proc.MainModule.FileName }
    if ($ep) { if (Probe (Split-Path -Parent $ep)) { Done } }
}

# ---- 2) DMMGamePlayer bookkeeping ------------------------------------
foreach ($base in @(
        (Join-Path $env:LOCALAPPDATA 'DMMGamePlayer'),
        (Join-Path $env:APPDATA      'DMMGamePlayer'))) {
    if (TimeUp) { break }
    if (-not (Test-Path -LiteralPath $base)) { continue }
    if (Probe (Join-Path $base 'games\gakumas')) { Done }
    $lvl1 = @(SubDirs $base)
    foreach ($a in $lvl1) {
        if (TimeUp) { break }
        if (Probe $a) { Done }
        foreach ($b in @(SubDirs $a)) {
            if (TimeUp) { break }
            if (Probe $b) { Done }
        }
    }
}

# ---- 3) fixed drives, two levels deep, interruptible -----------------
$skip = @(
    'Windows', 'Program Files', 'Program Files (x86)', 'ProgramData',
    '$Recycle.Bin', 'System Volume Information', '$WinREAgent',
    'Recovery', 'PerfLogs', 'MSOCache', 'Config.Msi', 'Windows.old'
)
$drives = @()
Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.Root -match '^[A-Za-z]:\\$') { $drives += $_.Root }
}

$tops = New-Object System.Collections.ArrayList
foreach ($d in $drives) {
    if (TimeUp) { break }
    $dt = ''
    try { $dt = "$(New-Object System.IO.DriveInfo($d)).DriveType" } catch { }
    if ($dt -eq 'Network') { Note "skip network $d"; continue }
    Note "scan $d"
    if (Probe $d) { Done }
    if (Probe (Join-Path $d 'gakumas')) { Done }
    foreach ($t in (SubDirs $d)) {
        if (TimeUp) { break }
        [void]$tops.Add($t)
    }
}

# Pass A: folders named like the game (cheap, very likely to hit)
foreach ($t in $tops) {
    if (TimeUp) { break }
    $n = ([System.IO.Path]::GetFileName($t)).ToLower()
    if ($n -notmatch 'gakumas|gakuen|idol|imas') { continue }
    if (Probe $t) { Done }
    foreach ($s in (SubDirs $t)) {
        if (TimeUp) { break }
        if (Probe $s) { Done }
    }
}

# Pass B: everything else -- this is what makes <drive>\xms\gakumas work
foreach ($t in $tops) {
    if (TimeUp) { break }
    $n = ([System.IO.Path]::GetFileName($t)).ToLower()
    if ($n -match 'gakumas|gakuen|idol|imas') { continue }
    if ($skip -contains ([System.IO.Path]::GetFileName($t))) { continue }
    if (Probe $t) { Done }
    foreach ($s in (SubDirs $t)) {
        if (TimeUp) { break }
        if (Probe $s) { Done }
    }
}

Note 'not found within budget'
exit 1
