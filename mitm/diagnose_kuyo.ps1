# Kuyo / 汉化 冲突诊断 —— 只读，不修改任何东西
# 输出: 包根目录的 Kuyo诊断.txt
$ErrorActionPreference = 'Continue'
$Here = Split-Path -Parent $PSCommandPath
$Root = Split-Path -Parent $Here
$out  = Join-Path $Root 'Kuyo诊断.txt'
$L = New-Object System.Collections.ArrayList
function Add([string]$s) { [void]$L.Add($s) }
function Head([string]$t) { Add ''; Add ('============ ' + $t + ' ============') }

$gp = Join-Path $Root 'game_path.txt'
$gdir = if (Test-Path $gp) { (Get-Content $gp -Raw).Trim() } else { '' }

Head '0. 基本信息'
Add ('时间     : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add ('计算机   : ' + $env:COMPUTERNAME + '   用户: ' + $env:USERNAME)
Add ('游戏目录 : ' + $gdir)

Head '1. version.dll（最关键：谁替换了它）'
if ($gdir) {
    $v = Join-Path $gdir 'version.dll'
    if (Test-Path $v) {
        $fi = Get-Item $v
        Add ('  大小     : ' + $fi.Length + ' B      （DMM 原版应为 5,995,520 B）')
        Add ('  修改时间 : ' + $fi.LastWriteTime)
        Add ('  SHA256   : ' + (Get-FileHash $v -Algorithm SHA256).Hash)
        $vi = $fi.VersionInfo
        Add ('  文件说明 : ' + $vi.FileDescription)
        Add ('  产品名   : ' + $vi.ProductName)
        Add ('  公司     : ' + $vi.CompanyName)
        Add ('  文件版本 : ' + $vi.FileVersion)
        try {
            $sig = Get-AuthenticodeSignature $v
            Add ('  数字签名 : ' + $sig.Status + '   签名者: ' + $sig.SignerCertificate.Subject)
        } catch { Add ('  数字签名 : 读取失败') }
        # 扫字符串找 Kuyo / localify 痕迹
        try {
            $bytes = [System.IO.File]::ReadAllBytes($v)
            $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
            foreach ($kw in @('kuyo','Kuyo','KUYO','localify','Localify','gakumas-local')) {
                if ($ascii.Contains($kw)) { Add ('  字符串命中: ' + $kw) }
            }
        } catch { }
    } else { Add '  找不到 version.dll' }
}

Head '2. 游戏目录里的第三方 DLL（签名 = 非微软/非 DMM 的可疑）'
if ($gdir -and (Test-Path $gdir)) {
    Get-ChildItem $gdir -Filter *.dll -File -ErrorAction SilentlyContinue | ForEach-Object {
        $s = $null
        try { $s = (Get-AuthenticodeSignature $_.FullName).Status } catch { }
        $signer = ''
        try { $signer = (Get-AuthenticodeSignature $_.FullName).SignerCertificate.Subject } catch { }
        Add ('  {0,-22} {1,10} B  {2,-12} {3}' -f $_.Name, $_.Length, $s, $signer)
    }
    Add ''
    Add ('  gakumas-local 目录: ' + $(if (Test-Path (Join-Path $gdir 'gakumas-local')) { '存在（Kuyo/汉化数据）' } else { '不存在' }))
    Add ('  Plugins 目录内容:')
    $pd = Join-Path $gdir 'gakumas_Data\Plugins\x86_64'
    if (Test-Path $pd) {
        Get-ChildItem $pd -Filter *.dll -File -ErrorAction SilentlyContinue | ForEach-Object {
            Add ('    ' + $_.Name + '   ' + $_.Length + ' B   ' + $_.LastWriteTime)
        }
    }
}

Head '3. Kuyo 运行痕迹（进程 / 服务 / 驱动 / 网卡）'
$kp = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
      Where-Object { $_.Name -match 'kuyo|Kuyo|KUYO|加速|Qy' -or $_.ExecutablePath -match 'kuyo|Kuyo' }
if ($kp) { foreach ($p in $kp) { Add ('  进程 pid=' + $p.ProcessId + '  ' + $p.Name + '  ' + $p.ExecutablePath) } }
else { Add '  未发现 Kuyo 进程（可能已退出，或用了别的进程名）' }

Add ''
Add '  服务（名称/显示名含 kuyo 或 qy）:'
$svc = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'kuyo|qy' -or $_.DisplayName -match 'kuyo|加速|Kuyo' }
if ($svc) { foreach ($s in $svc) { Add ('    ' + $s.Name + '  ' + $s.DisplayName + '  ' + $s.Status) } } else { Add '    无' }

Add ''
Add '  网卡（虚拟网卡通常是加速器 TUN）:'
Get-NetAdapter -ErrorAction SilentlyContinue | ForEach-Object {
    Add ('    ' + $_.Name + '  [' + $_.InterfaceDescription + ']  ' + $_.Status)
}

Add ''
Add '  系统代理:'
$k = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
$pr = Get-ItemProperty -Path $k -ErrorAction SilentlyContinue
Add ('    ProxyEnable = ' + $pr.ProxyEnable + '   ProxyServer = ' + $pr.ProxyServer)
Add ('    自动配置脚本 AutoConfigURL = ' + $pr.AutoConfigURL)

Head '4. Windows 崩溃报告里的「加载模块列表」（决定性证据）'
$werDirs = @(
    'C:\ProgramData\Microsoft\Windows\WER\ReportArchive',
    'C:\ProgramData\Microsoft\Windows\WER\ReportQueue'
)
$found = @()
foreach ($d in $werDirs) {
    if (Test-Path $d) {
        Get-ChildItem $d -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'gakumas' } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 3 |
            ForEach-Object { $found += $_ }
    }
}
if (-not $found) {
    Add '  没找到 gakumas 的 WER 报告目录（需要管理员权限，或者报告已被清理）'
} else {
    foreach ($dir in $found) {
        Add ''
        Add ('  --- 报告: ' + $dir.Name + '   (' + $dir.LastWriteTime + ')')
        $wer = Get-ChildItem $dir.FullName -Filter '*.wer' -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $wer) { Add '      没有 .wer 文件'; continue }
        $lines = Get-Content $wer.FullName -ErrorAction SilentlyContinue
        # 关键字段
        foreach ($ln in $lines) {
            if ($ln -match '^(AppName|AppPath|ModName|ModVer|Offset|ExceptionCode|Sig\[' ) {
                Add ('      ' + $ln.Trim())
            }
        }
        Add '      --- 加载的非系统模块（重点看有没有 Kuyo / 汉化相关）---'
        $idx = 0
        foreach ($ln in $lines) {
            if ($ln -match '^LoadedModule\[(\d+)\]=(.+)$') {
                $path = $matches[2]
                if ($path -notmatch '^[A-Za-z]:\\Windows\\' -and $path -notmatch '^C:\\Windows\\') {
                    Add ('      ' + $path)
                }
                $idx++
            }
        }
        Add ('      (模块总数: ' + $idx + ')')
    }
}

Head '5. 汉化文件定位（Kuyo 到底往游戏目录塞了什么）'
if ($gdir -and (Test-Path $gdir)) {
    Add '  --- 5.1 游戏根目录文件，按修改时间倒序（最近被改的在最上面）---'
    Get-ChildItem $gdir -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 40 |
        ForEach-Object {
            Add ('  {0:yyyy-MM-dd HH:mm}  {1,12} B  {2}' -f $_.LastWriteTime, $_.Length, $_.Name)
        }

    Add ''
    Add '  --- 5.2 游戏根目录下的子目录 ---'
    Get-ChildItem $gdir -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object {
            $cnt = (Get-ChildItem $_.FullName -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
            Add ('  {0:yyyy-MM-dd HH:mm}  {1,7} 文件  {2}' -f $_.LastWriteTime, $cnt, $_.Name)
        }

    Add ''
    Add '  --- 5.3 已知汉化/注入目录名探测 ---'
    foreach ($n in @('gakumas-local','local','localify','hanyu','hanhua','汉化','trans','translation','BepInEx','Kuyo','kuyo','melonloader','doorstop')) {
        $p = Join-Path $gdir $n
        if (Test-Path $p) {
            $cnt = (Get-ChildItem $p -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
            Add ('    命中: ' + $n + '  (' + $cnt + ' 文件, ' + (Get-Item $p).LastWriteTime + ')')
        }
    }

    Add ''
    Add '  --- 5.4 gakumas-local 内部（GakumasLocalify 结构）---'
    $gl = Join-Path $gdir 'gakumas-local'
    if (Test-Path $gl) {
        Get-ChildItem $gl -File -ErrorAction SilentlyContinue |
            ForEach-Object { Add ('    ' + $_.Name + '   ' + $_.Length + ' B   ' + $_.LastWriteTime) }
        foreach ($f in @('version.txt','log.txt','config.json')) {
            $p = Join-Path $gl $f
            if (Test-Path $p) {
                Add ('    --- ' + $f + ' ---')
                Get-Content $p -TotalCount 12 -ErrorAction SilentlyContinue | ForEach-Object { Add ('      ' + $_) }
            }
        }
    } else { Add '    不存在（他用的可能不是 GakumasLocalify，而是 Kuyo 自有格式）' }

    Add ''
    Add '  --- 5.5 游戏目录里所有 dll（按修改时间倒序，找被替换的）---'
    Get-ChildItem $gdir -Filter *.dll -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        ForEach-Object { Add ('  {0:yyyy-MM-dd HH:mm}  {1,12} B  {2}' -f $_.LastWriteTime, $_.Length, $_.Name) }
    $pd2 = Join-Path $gdir 'gakumas_Data\Plugins\x86_64'
    if (Test-Path $pd2) {
        Get-ChildItem $pd2 -Filter *.dll -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
            ForEach-Object { Add ('  [Plugins] {0:yyyy-MM-dd HH:mm}  {1,12} B  {2}' -f $_.LastWriteTime, $_.Length, $_.Name) }
    }
}

Head '6. Kuyo 客户端安装位置（汉化包本体可能在这）'
$kuyoPaths = @(
    (Join-Path $env:LOCALAPPDATA 'Kuyo'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Kuyo'),
    (Join-Path $env:APPDATA 'Kuyo'),
    'C:\Program Files\Kuyo',
    'C:\Program Files (x86)\Kuyo',
    'D:\Kuyo'
)
foreach ($p in $kuyoPaths) {
    if (Test-Path $p) {
        Add ('  存在: ' + $p)
        Get-ChildItem $p -Recurse -File -Depth 2 -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'k(m|uyo)|汉化|local|trans|\.dll$' } |
            Select-Object -First 25 |
            ForEach-Object { Add ('    ' + $_.FullName + '   ' + $_.Length + ' B') }
    }
}
$kp2 = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
       Where-Object { $_.ExecutablePath -match 'kuyo|Kuyo' }
foreach ($p in $kp2) {
    Add ('  进程安装路径: ' + $p.ExecutablePath)
    $dir = Split-Path $p.ExecutablePath -Parent
    if ($dir -and (Test-Path $dir)) {
        Get-ChildItem $dir -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Add ('    子目录: ' + $_.Name) }
    }
}

Head '7. 直接结论提示'
if ($gdir) {
    $v = Join-Path $gdir 'version.dll'
    if (Test-Path $v) {
        $sz = (Get-Item $v).Length
        if ($sz -ne 5995520) {
            Add ('  version.dll = ' + $sz + ' B，与 DMM 原版 5,995,520 B 不一致 -> 被第三方替换过')
        } else {
            Add '  version.dll 大小与 DMM 原版一致'
        }
    }
}

$L | Out-File -LiteralPath $out -Encoding UTF8
Write-Host ''
Write-Host '============================================'
Write-Host '  诊断完成:'
Write-Host ('  ' + $out)
Write-Host '============================================'
Write-Host ''
Write-Host '  重点看第 4 节「加载的非系统模块」：'
Write-Host '  如果里面出现 Kuyo / 汉化相关的 DLL，就是它注入导致崩溃。'
Write-Host ''
Read-Host '按回车退出'
