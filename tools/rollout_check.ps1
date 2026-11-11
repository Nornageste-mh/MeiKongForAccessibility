<#
    铺开前侦察：只看、不改。

    用途：把《妹控计划》这套改造铺到其它逐作仓之前，先量一遍每个仓库「离 Framework HEAD 有多远」，
    免得逐个手敲 git status / 哈希对比 / grep 配置默认值。

    对每个仓库报：
      1. git：分支、与 origin/main 的 ahead/behind（没有 upstream 或 fetch 过旧会标 ?）
      2. 平台层（L1）：Framework/tools/l1-files.txt 里每个文件 —— 同 / 漂 / 缺
         「同」的判据：把文本里的 A11yFramework 换成该仓 namespace 后，忽略换行符差异逐字节相同
      3. 契约层：contract/A11yHost.cs 是否漂移 / 缺失
      4. 交互形态：CfgMenuNav 默认值（true / false / 没有这项）；有没有 docs/交互形态判定.md
      5. 工具与文档：sync_l1.py / l1-files.txt / compile_check / lint_repeat / check-staged /
         package_check / smoke / docs/无障碍补丁流水线.md / docs/project_status.md
      6. 版本号：csproj 的 <Version> 与 Plugin 的 BepInPlugin 第三参

    用法：
        cd <本仓库>
        .\tools\rollout_check.ps1                       # 用内置的姊妹仓列表
        .\tools\rollout_check.ps1 -Repos 'D:\DSHWorkSpace\work\SisterOtherParanoiaForAccessibility'
        .\tools\rollout_check.ps1 -FrameworkDir 'D:\DSHWorkSpace\Framework' -Json out.json

    注意：脚本不 fetch、不 pull、不改任何文件；ahead/behind 是本地的 origin/main 引用，
    要最新数字先在各仓自己 fetch。
#>
[CmdletBinding()]
param(
    [string]$FrameworkDir = '',
    [string[]]$Repos = @(),
    [string]$Json = ''
)

$ErrorActionPreference = 'Stop'

function Say($m) { Write-Host $m }
function Header($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# ---------- 定位 Framework ----------
$candidates = @()
if ($FrameworkDir) { $candidates += $FrameworkDir }
$candidates += @(
    (Join-Path (Split-Path $PSScriptRoot -Parent) '..\Framework'),
    'D:\DSHWorkSpace\Framework'
)
$fw = $null
foreach ($c in $candidates) {
    if (-not $c) { continue }
    $full = [System.IO.Path]::GetFullPath($c)
    if ((Test-Path (Join-Path $full 'tools\l1-files.txt')) -and (Test-Path (Join-Path $full 'platform'))) { $fw = $full; break }
}
if (-not $fw) { Write-Host "找不到 Framework（要有 tools\l1-files.txt 和 platform\）。用 -FrameworkDir 指定。" -ForegroundColor Red; exit 1 }
Say "Framework：$fw"

# 用 .NET 读 UTF-8 再按 \n 切：l1-files.txt 是 UTF-8 无 BOM + LF，
# 而 Windows PowerShell 5.1 的 Get-Content 默认按 ANSI 解码，DBCS 错位会把换行吞掉（踩过一次）。
$l1Raw = [System.IO.File]::ReadAllText((Join-Path $fw 'tools\l1-files.txt'))
$l1 = @($l1Raw -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
Say ("L1 清单：" + $l1.Count + " 个文件 —— " + ($l1 -join ', '))

$contractSrc = Join-Path $fw 'contract\A11yHost.cs'

# ---------- 姊妹仓列表 ----------
if ($Repos.Count -eq 0) {
    $root = 'D:\DSHWorkSpace'
    $Repos = @(
        (Join-Path $root 'MeiKongForAccessibility'),
        (Join-Path $root 'belfry_a11y'),
        (Join-Path $root 'justlilith_a11y'),
        (Join-Path $root 'manosaba_a11y'),
        (Join-Path $root 'noexistence_a11y'),
        (Join-Path $root 'transparenther_a11y'),
        (Join-Path $root 'work\SisterOtherParanoiaForAccessibility')
    )
}

# ---------- 文本归一化：namespace 换成该仓自己的，换行统一 ----------
function Normalize([string]$text, [string]$ns) {
    if ($null -eq $text) { return $null }
    $t = $text -replace '\r\n', "`n"
    if ($ns) { $t = $t.Replace('A11yFramework', $ns) }
    return $t
}

function Get-Namespace([string]$file) {
    if (-not (Test-Path -LiteralPath $file)) { return '' }
    $m = Select-String -LiteralPath $file -Pattern '^\s*namespace\s+([A-Za-z0-9_.]+)' -List
    if ($m) { return $m.Matches[0].Groups[1].Value }
    return ''
}

function Git-Info([string]$repo) {
    $out = [ordered]@{ branch = '?'; ahead = '?'; behind = '?'; dirty = '?' }
    if (-not (Test-Path (Join-Path $repo '.git'))) { return $out }
    try { $out.branch = (& git -C $repo rev-parse --abbrev-ref HEAD 2>$null) } catch { }
    try {
        $counts = (& git -C $repo rev-list --left-right --count 'origin/main...HEAD' 2>$null)
        if ($counts) {
            $p = $counts -split '\s+'
            if ($p.Count -ge 2) { $out.behind = [int]$p[0]; $out.ahead = [int]$p[1] }
        }
    } catch { }
    try {
        $st = @(& git -C $repo status --porcelain 2>$null)
        $out.dirty = if ($st.Count -eq 0) { 0 } else { $st.Count }
    } catch { }
    return $out
}

function Find-GameSrc([string]$repo) {
    $base = Join-Path $repo 'mod\src'
    if (-not (Test-Path (Join-Path $repo 'mod'))) { return $null }
    $dirs = @()
    if (Test-Path $base) { $dirs += @(Get-ChildItem $base -Directory -ErrorAction SilentlyContinue) }
    if ($dirs.Count -eq 0) {
        $dirs += @(Get-ChildItem (Join-Path $repo 'mod') -Directory -ErrorAction SilentlyContinue)
    }
    foreach ($d in $dirs) {
        if (Test-Path (Join-Path $d.FullName 'Plugin.cs')) { return $d.FullName }
        if (Test-Path (Join-Path $d.FullName 'A11yHost.cs')) { return $d.FullName }
    }
    return $null
}

function Get-Versions([string]$repo, [string]$src) {
    $r = [ordered]@{ csproj = ''; plugin = '' }
    $proj = @()
    if ($src) { $proj = @(Get-ChildItem $src -Filter '*.csproj' -ErrorAction SilentlyContinue) }
    if ($proj.Count -eq 0) {
        $proj = @(Get-ChildItem (Join-Path $repo 'mod') -Filter '*.csproj' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj|bep_dl|nvda_dl|dl)\\' } | Select-Object -First 1)
    }
    if ($proj.Count -gt 0) {
        $m = Select-String -LiteralPath $proj[0].FullName -Pattern '<Version>([^<]+)</Version>' -List
        if ($m) { $r.csproj = $m.Matches[0].Groups[1].Value }
    }
    if ($src) {
        $p = Join-Path $src 'Plugin.cs'
        if (Test-Path $p) {
            $m = Select-String -LiteralPath $p -Pattern 'BepInPlugin\([^)]*,\s*"([^"]+)"\s*\)' -List
            if ($m) { $r.plugin = $m.Matches[0].Groups[1].Value }
        }
    }
    return $r
}

function Get-MenuNavDefault([string]$src) {
    if (-not $src) { return '没有' }
    $p = Join-Path $src 'Plugin.cs'
    if (-not (Test-Path $p)) { return '没有' }
    $line = Select-String -LiteralPath $p -Pattern 'CfgMenuNav\s*=\s*Config\.Bind' -List
    if (-not $line) { return '没有这项' }
    $text = [System.IO.File]::ReadAllText($p)
    $m = [regex]::Match($text, 'CfgMenuNav\s*=\s*Config\.Bind\((?<a>[^;]+?)\);', 'Singleline')
    if ($m.Success) {
        $a = $m.Groups['a'].Value
        $parts = $a -split ','
        if ($parts.Count -ge 3) {
            $d = $parts[2].Trim()
            if ($d -eq 'true') { return 'true' }
            if ($d -eq 'false') { return 'false' }
            return $d
        }
    }
    return '?'
}

$results = @()
foreach ($repo in $Repos) {
    if (-not $repo) { continue }
    $full = [System.IO.Path]::GetFullPath($repo)
    Header ([System.IO.Path]::GetFileName($full))
    if (-not (Test-Path $full)) { Say "  不存在：$full"; continue }

    $g = Git-Info $full
    Say ("  git：分支 " + $g.branch + " / behind origin/main " + $g.behind + " / ahead " + $g.ahead + " / 未提交改动 " + $g.dirty)

    $src = Find-GameSrc $full
    if (-not $src) { Say "  找不到 mod\src\<Namespace>（没有 Plugin.cs / A11yHost.cs）" -ForegroundColor Red }
    else { Say ("  逐作源码：mod\src\" + (Split-Path $src -Leaf)) }

    $ns = if ($src) { Get-Namespace (Join-Path $src 'Plugin.cs') } else { '' }
    if (-not $ns -and $src) { $ns = Get-Namespace (Join-Path $src 'A11yHost.cs') }
    if ($ns) { Say ("  namespace：$ns") }

    # L1
    $same = @(); $diff = @(); $miss = @()
    foreach ($f in $l1) {
        $target = if ($src) { Join-Path $src $f } else { $null }
        if (-not $target -or -not (Test-Path -LiteralPath $target)) { $miss += $f; continue }
        $a = Normalize ([System.IO.File]::ReadAllText((Join-Path $fw ('platform\' + $f)))) $ns
        $b = Normalize ([System.IO.File]::ReadAllText($target)) $ns
        if ($a -ceq $b) { $same += $f } else { $diff += $f }
    }
    Say ("  L1：同 " + $same.Count + " / 漂 " + $diff.Count + " / 缺 " + $miss.Count)
    if ($diff.Count) { Say ("    漂移：" + ($diff -join ', ')) }
    if ($miss.Count) { Say ("    缺失：" + ($miss -join ', ')) }

    # 契约
    $cSame = '缺'; $cDiff = ''
    if ($src -and (Test-Path (Join-Path $src 'A11yHost.cs'))) {
        $a = Normalize ([System.IO.File]::ReadAllText($contractSrc)) $ns
        $b = Normalize ([System.IO.File]::ReadAllText((Join-Path $src 'A11yHost.cs'))) $ns
        $cSame = if ($a -ceq $b) { '同' } else { '漂' }
    }
    Say ("  契约 A11yHost.cs：" + $cSame)

    # 交互形态
    Say ("  CfgMenuNav 默认值：" + (Get-MenuNavDefault $src))
    $judge = Test-Path (Join-Path $full 'docs\交互形态判定.md')
    $judgeText = if ($judge) { '有' } else { '没有' }
    Say ("  docs\交互形态判定.md：" + $judgeText)

    # 工具与文档
    $tools = [ordered]@{
        'tools\sync_l1.py'                = $false
        'tools\l1-files.txt'              = $false
        'tools\compile_check.ps1'         = $false
        'tools\lint_repeat.ps1'           = $false
        'tools\check-staged.ps1'          = $false
        'tools\package_check.ps1'         = $false
        'tools\smoke.ps1'                 = $false
        'docs\无障碍补丁流水线.md'         = $false
        'docs\project_status.md'          = $false
    }
    $have = @(); $lack = @()
    foreach ($k in @($tools.Keys)) {
        if (Test-Path (Join-Path $full $k)) { $have += $k } else { $lack += $k }
    }
    Say ("  工具/文档：有 " + $have.Count + " / 缺 " + $lack.Count)
    if ($lack.Count) { Say ("    缺：" + ($lack -join '、')) }

    $v = Get-Versions $full $src
    Say ("  版本：csproj " + $v.csproj + " / Plugin " + $v.plugin)

    $results += [pscustomobject]@{
        repo = [System.IO.Path]::GetFileName($full)
        path = $full
        branch = $g.branch
        behind = $g.behind
        ahead = $g.ahead
        dirty = $g.dirty
        src = if ($src) { Split-Path $src -Leaf } else { '' }
        ns = $ns
        l1_same = $same.Count
        l1_diff = $diff.Count
        l1_missing = $miss.Count
        l1_missing_files = ($miss -join ',')
        contract = $cSame
        menunav = (Get-MenuNavDefault $src)
        judge_doc = $judgeText
        tools_missing = ($lack -join ',')
        version_csproj = $v.csproj
        version_plugin = $v.plugin
    }
}

Header '汇总'
$results | Format-Table repo, branch, behind, ahead, dirty, l1_same, l1_diff, l1_missing, contract, menunav, judge_doc, version_csproj -AutoSize | Out-String -Width 200 | Write-Host

if ($Json) {
    [System.IO.File]::WriteAllText($Json, ($results | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
    Say "已写 $Json"
}

Say "`n只读侦察完成：没有 fetch、没有改动任何仓库。"
