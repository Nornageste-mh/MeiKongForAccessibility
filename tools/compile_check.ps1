#Requires -Version 5.1
<#
    静态自检 —— 两种模式，按仓库根的目录结构自动判断。

    ============================================================
    模式 A：Framework 仓根（有 platform\ 与 contract\）
    ============================================================
    平台层自检 —— 证明 L1（platform\）**没有任何游戏类型依赖**。

    做法：把 platform\*.cs 与 contract\A11yHost.cs 拷进一个临时工程编译，
    引用 UnityEngine / TMPro / BepInEx，**故意不引用 Assembly-CSharp**。

        编得过  ⇒ 平台层确实只依赖引擎与 BepInEx，没有偷偷引用游戏类型。

    这就是《无障碍补丁流水线》§8 出口检查清单里
    「共享层无任何游戏类型引用」那一条的**机械验证**，而不是靠人眼看。

    同时它还会断言 platform\ 里没有任何 Plugin. 残留 ——
    有残留说明接口没有全部改指 A11yHost，逐作拷贝时会编译失败。

    ============================================================
    模式 B：逐作仓根（有 mod\src\，没有 platform\）
    ============================================================
    逐作层静态守卫。**不需要游戏目录**，拿到新骨架就能跑：

      1) L1 清单完整性：mod\src\<ns>\ 必须包含 tools\l1-files.txt 里的每个文件。
         少了 GlobalKeys.cs 的后果是新骨架第一次编译就 CS0103
         （KeyEdge.cs 调 GlobalKeys.Reading/Held/...）。
      2) NameAlias 大小写重复：**直接解析真实的 mod\src\<ns>\UiNav.Game.cs**
         （不是自检用的占位文件）。字典用 StringComparer.OrdinalIgnoreCase 时，
         "Start" 与 "start" 两个键会在**静态构造**时抛 ArgumentException →
         TypeInitializationException → 导航 100% 失效且没有一行日志。
         报错会打印两个键各自的行号。可用 -GameSrc 显式指定别的 UiNav.Game.cs。
      3) 重读缓冲区唯一出口：逐作层只有 Repeat.Say，不许 Speech.Speak
         （Repeat.cs 本身是 L1 拷来的缓冲区，默认放行）。
      4) 配置项无读取点（启发式，只警告）：Bind 出来的字段只在声明处出现。

    逐作仓要验证「能编译」请跑 mod\build.ps1（它需要游戏目录取引用），
    本脚本模式 B 只做静态守卫。

    用法：
        .\tools\compile_check.ps1                      # Framework 仓：完整自检
        .\tools\compile_check.ps1 -GameDir "..."       # Framework 仓：指定游戏目录
        .\tools\compile_check.ps1 -StaticOnly           # 只跑静态断言，不编译
        .\tools\compile_check.ps1 -Keep                # 保留临时工程，便于看编译错误

        逐作仓（骨架生成出来就是这样，不需要任何参数、不需要游戏目录）：
        .\tools\compile_check.ps1
        .\tools\compile_check.ps1 -GameSrc mod\src\MyGameA11y\UiNav.Game.cs
        （L1 清单默认读 tools\l1-files.txt，由 new_game.py 生成；没有该文件时
          退回内置保底清单并给出警告）

    注意：本脚本必须保存为 UTF-8 **带 BOM** —— Windows PowerShell 5.1
    读无 BOM 的脚本会按 GBK 解码，中文注释变乱码并直接语法错误（流水线 G5）。
#>
[CmdletBinding()]
param(
    # 借一个已安装的游戏来取 UnityEngine / TMPro / BepInEx 的引用。
    # 平台层不引用任何游戏类型，所以用哪个游戏都可以。
    # 默认读环境变量 A11Y_GAMEDIR；也可以用 -GameDir 显式指定。
    [string]$GameDir = "$env:A11Y_GAMEDIR",
    [string]$BepInExCore = "",
    [switch]$Keep,
    # 只跑静态断言（模式 A 跳过 2/4~4/4 的编译；模式 B 本来就不编译）
    [switch]$StaticOnly,
    # 模式 B 下要检查的 UiNav.Game.cs。留空 = mod\src\<ns>\UiNav.Game.cs。
    # 别名表重复是静态构造期就炸的错误，必须在真实文件上查，不能只在占位文件上查。
    [string]$GameSrc = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$platform = Join-Path $root "platform"
$contract = Join-Path $root "contract"
$work = Join-Path $root ".compilecheck"
$src = Join-Path $work "src"
$NL = [Environment]::NewLine

function Fail($msg) { Write-Host ""; Write-Host "FAIL  $msg" -ForegroundColor Red; exit 1 }
function Ok($msg)   { Write-Host "  ok  $msg" -ForegroundColor Green }

# ============================================================================
# 公用检查：NameAlias 字典（OrdinalIgnoreCase）不允许「只差大小写」的重复键
# ============================================================================
function Test-NameAlias([string]$File, [switch]$Required) {
    if (-not (Test-Path -LiteralPath $File)) {
        if ($Required) { Fail ("找不到要检查的 UiNav.Game.cs：{0}（用 -GameSrc 指定）" -f $File) }
        return
    }
    $lines = [System.IO.File]::ReadAllLines($File)

    # 1) 定位 NameAlias 声明，并确认它用的是 OrdinalIgnoreCase 字典。
    #    只对「大小写不敏感」的字典查重：普通 Dictionary 允许多种大小写并存。
    $decl = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (($lines[$i] -replace '//.*$', '') -match 'NameAlias\s*=') { $decl = $i; break }
    }
    if ($decl -lt 0) {
        if ($Required) { Fail ("{0} 里找不到 NameAlias 声明 —— 别名表被改名/挪走了？" -f $File) }
        return
    }
    $ordinal = $false
    $dictEnd = -1
    $limit = [Math]::Min($lines.Count - 1, $decl + 60)
    for ($i = $decl; $i -le $limit; $i++) {
        $code = $lines[$i] -replace '//.*$', ''
        if ($code -match 'new\s+Dictionary\s*<\s*string\s*,\s*string\s*>\s*\(\s*StringComparer\.OrdinalIgnoreCase') {
            $ordinal = $true
        }
        if ($ordinal -and $code -match '\}\s*;') { $dictEnd = $i; break }
    }
    if (-not $ordinal) { return }   # 不是大小写不敏感字典，不适用本检查
    if ($dictEnd -lt 0) { Fail ("{0} 的 NameAlias 初始化器找不到结尾 ""}};""，词法解析失败" -f $File) }

    # 2) 逐行收集「{ ""键"", ...」条目，记下行号（去行注释，避免把示例当配置）。
    $entries = @()
    for ($i = $decl; $i -le $dictEnd; $i++) {
        $code = $lines[$i] -replace '//.*$', ''
        foreach ($m in [regex]::Matches($code, '\{\s*"((?:[^"\\]|\\.)*)"\s*,')) {
            $entries += [pscustomobject]@{ Key = $m.Groups[1].Value; Line = ($i + 1) }
        }
    }
    if ($entries.Count -eq 0) {
        Write-Host ("  --  警告：{0} 的 NameAlias 没解析到任何键（空表或写法变了）" -f $File) -ForegroundColor Yellow
        return
    }

    # 3) 按 OrdinalIgnoreCase 查重；重复时打印两个键的行号。
    $seen = @{}
    foreach ($e in $entries) {
        $lk = $e.Key.ToLowerInvariant()
        if ($seen.ContainsKey($lk)) {
            $prev = $seen[$lk]
            Fail (("{0} 的 NameAlias 有大小写重复键：第 {1} 行 ""{2}"" 与第 {3} 行 ""{4}""。" +
                   "字典用 OrdinalIgnoreCase，静态构造会抛 ArgumentException → " +
                   "TypeInitializationException，表现为导航 100% 失效且零日志。删掉一个。") -f
                  $File, $prev.Line, $prev.Key, $e.Line, $e.Key)
        }
        $seen[$lk] = $e
    }
    Ok ("NameAlias 无大小写重复（{0} 个键，{1}）" -f $entries.Count, (Split-Path $File -Leaf))
}

# ============================================================================
# 模式判断
# ============================================================================
$isFramework = Test-Path -LiteralPath $platform
$pergameSrc = Join-Path $root "mod\src"
$isPergame = (-not $isFramework) -and (Test-Path -LiteralPath $pergameSrc)
if (-not $isFramework -and -not $isPergame) {
    Fail "既找不到 platform\（Framework 仓），也找不到 mod\src\（逐作仓）—— 请在仓库根目录下运行"
}

# ============================================================================
# 模式 A：Framework 平台层自检
# ============================================================================
if ($isFramework) {

Write-Host ""
Write-Host "=== A11yFramework 平台层自检 ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1/4 静态断言
Write-Host ""
Write-Host "[1/4] 静态断言：Plugin. 引用 / namespace / partial / **重读缓冲区是唯一出口**" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $contract)) { Fail "找不到 contract\（请在 A11yFramework 仓库根目录下运行）" }

# 只看**注释之外**的代码。源文件里保留了「原来引用的是 Plugin.*」这类说明性注释，
# 人眼看是正常的，不该被机械检查判成违规。
$bad = @()
foreach ($f in Get-ChildItem $platform -Filter *.cs) {
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($f.FullName)) {
        $code = $line
        $i = $code.IndexOf('//')
        if ($i -ge 0) { $code = $code.Substring(0, $i) }
        if ($code -match 'Plugin\.') { $n++ }
    }
    if ($n -gt 0) { $bad += ("{0} 里 {1} 处" -f $f.Name, $n) }
}
if ($bad.Count -gt 0) {
    Fail ("platform\ 的代码里还有 Plugin. 引用: " + ($bad -join "; ") + " —— 平台层只允许引用 A11yHost.*")
}
Ok "platform\*.cs 无 Plugin. 引用"

foreach ($f in Get-ChildItem $platform -Filter *.cs) {
    if (-not (Select-String -LiteralPath $f.FullName -Pattern 'namespace A11yFramework' -Quiet)) {
        Fail ("{0} 的 namespace 不是 A11yFramework —— 平台层被改过了" -f $f.Name)
    }
}
Ok "platform\*.cs 的 namespace 都是 A11yFramework"

if (-not (Select-String -LiteralPath (Join-Path $platform "UiNav.cs") -Pattern 'internal static partial class UiNav' -Quiet)) {
    Fail "UiNav.cs 不是 partial class —— 逐作区无法挂上去"
}
Ok "UiNav 已拆成 partial（引擎 + 逐作区）"

# L1 文件清单：**唯一真源 = Framework 根 tools\l1-files.txt**（不是目录枚举、不是
# 本脚本里的常量）。为什么做成机械断言：assemble_from_sources.py 会**先删掉 platform\
# 再按自己的清单重建**，new_game.py 逐行照清单往新作拷 —— 任何一处各写一份清单，
# 漏一个文件，平台层的新能力就被静默删掉，git status 还看起来很干净
# （流水线 §1.3 的「平台层单向退化」）。所以这里**双向比对**：清单写了但目录没有 → Fail；
# 目录有但清单没写 → 也 Fail，逼人同步唯一真源。
$l1Manifest = Join-Path $root "tools\l1-files.txt"
if (-not (Test-Path -LiteralPath $l1Manifest)) {
    Fail "缺 tools\l1-files.txt —— L1 清单的唯一真源不见了（new_game.py / 本脚本都读它）"
}
$l1 = @(Get-Content -LiteralPath $l1Manifest -Encoding UTF8 |
        Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith("#") } |
        ForEach-Object { $_.Trim() })
if ($l1.Count -eq 0) { Fail "tools\l1-files.txt 里一个文件名都没有" }
$actual = @(Get-ChildItem $platform -Filter *.cs | Sort-Object Name | ForEach-Object { $_.Name })
$onlyInManifest = @($l1 | Where-Object { $actual -notcontains $_ })
$onlyInDir = @($actual | Where-Object { $l1 -notcontains $_ })
if ($onlyInManifest.Count -gt 0 -or $onlyInDir.Count -gt 0) {
    Fail (("platform\ 与 tools\l1-files.txt 不一致：清单里多出 platform\ 不存在的 [{0}]；" +
           "platform\ 里多出清单没写的 [{1}]。平台层增删文件后必须同步唯一真源。") -f
          (($onlyInManifest -join ", ") -replace '^$', "无"),
          (($onlyInDir -join ", ") -replace '^$', "无"))
}
if ($l1 -notcontains "GlobalKeys.cs") {
    Fail "tools\l1-files.txt 里没有 GlobalKeys.cs —— KeyEdge.cs 引用 GlobalKeys.*，新作会 CS0103"
}
# 交叉校验：KeyEdge.cs 引用 GlobalKeys.*，两者必须同时在（0.2.0.0 漏 GlobalKeys.cs 的事故）
$keyEdge = Join-Path $platform "KeyEdge.cs"
if ((Test-Path -LiteralPath $keyEdge) -and
    (Select-String -LiteralPath $keyEdge -Pattern 'GlobalKeys\.' -Quiet) -and
    (-not (Test-Path -LiteralPath (Join-Path $platform "GlobalKeys.cs")))) {
    Fail "KeyEdge.cs 引用了 GlobalKeys.*，但 platform\GlobalKeys.cs 不在 —— 新作骨架会 CS0103"
}
Ok ("L1 清单齐全（tools\l1-files.txt，{0} 个：{1}）" -f $l1.Count, ($l1 -join " "))

if (-not (Select-String -LiteralPath (Join-Path $platform "Speech.cs") -Pattern 'CycleAvailable' -Quiet)) {
    Fail "Speech.cs 里没有 CycleAvailable —— 运行中切换后端的能力被删掉了"
}
if (-not (Select-String -LiteralPath (Join-Path $platform "Zdsr.cs") -Pattern 'GetSpeakState' -Quiet)) {
    Fail "Zdsr.cs 里没有 GetSpeakState —— 争渡后端的可用性判据被改掉了（判据必须是 3/4）"
}
if (-not (Select-String -LiteralPath (Join-Path $platform "Hotkey.cs") -Pattern 'IsAllDigits' -Quiet)) {
    Fail "Hotkey.cs 里没有纯数字防护（流水线 G3：TryParse(""0"") 会成功但得到 KeyCode.None）"
}
Ok "争渡后端 / 切换后端 / 按键解析三个能力都在"

# 重读缓冲区只允许一个出口：platform 下只有 Repeat.cs 能调 Speech.Speak。
# 这个坑踩过两次（《钟塔》0.1.0.1「选项播报绕过了缓冲区」/《妹控计划》0.1.0.0 同款），
# 所以做成机械断言，而不是再写一遍复盘。
$lint = Join-Path $PSScriptRoot "lint_repeat.ps1"
if (Test-Path -LiteralPath $lint) {
    & $lint -Path $platform -AllowFile "Repeat.cs"
    if ($LASTEXITCODE -ne 0) { Fail "平台层里有绕过重读缓冲区的播报 —— 逐作层将听不到它" }
} else {
    Write-Host "  （找不到 lint_repeat.ps1，跳过重读出口检查）" -ForegroundColor Yellow
}

# 模板里的示例别名表也要过一遍：模板是逐作骨架的起点，示例里放重复键
# 等于每个新作都从一条坏数据开始。
Test-NameAlias (Join-Path $root "templates\game\UiNav.Game.cs.template")

# MSB4025 回归点：XML 注释里出现连续两个减号会让 csproj 直接解析失败，
# 而生成器裁剪 @@ROUTE 段后更容易无意拼出这种组合。这里机械拦住。
$csprojTpl = Join-Path $root "templates\game\GameA11y.csproj.template"
if (Test-Path -LiteralPath $csprojTpl) {
    $ln = 0
    foreach ($line in [System.IO.File]::ReadAllLines($csprojTpl)) {
        $ln++
        foreach ($m in [regex]::Matches($line, '<!--(.*?)-->')) {
            if ($m.Groups[1].Value -match '--') {
                Fail ("模板 {0}:{1} 的 XML 注释里有连续两个减号 → MSB4025，csproj 会解析失败" -f $csprojTpl, $ln)
            }
        }
    }
    Ok "csproj 模板的 XML 注释没有连续减号（MSB4025）"
}

if ($StaticOnly) {
    Write-Host ""
    Write-Host "PASS  静态断言全部通过（-StaticOnly：未编译）" -ForegroundColor Green
    exit 0
}

# ---------------------------------------------------------------- 2/4 找引用
Write-Host ""
Write-Host "[2/4] 解析引用（GameDir = $GameDir）" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $GameDir)) { Fail "找不到游戏目录: $GameDir（用 -GameDir 指定，或加 -StaticOnly 只跑静态断言）" }

$managed = Get-ChildItem -LiteralPath $GameDir -Directory -Filter "*_Data" |
           ForEach-Object { Join-Path $_.FullName "Managed" } |
           Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $managed) { Fail "在 $GameDir 下找不到 *_Data\Managed" }
Ok "Managed: $managed"

if ([string]::IsNullOrEmpty($BepInExCore)) { $BepInExCore = Join-Path $GameDir "BepInEx\core" }
if (-not (Test-Path -LiteralPath (Join-Path $BepInExCore "BepInEx.dll"))) {
    Fail "找不到 $BepInExCore\BepInEx.dll（用 -BepInExCore 指定）"
}
Ok "BepInEx core: $BepInExCore"

$refs = @()
foreach ($n in @("BepInEx.dll", "0Harmony.dll")) {
    $p = Join-Path $BepInExCore $n
    if (Test-Path -LiteralPath $p) { $refs += $p } else { Fail "缺少 $p" }
}
foreach ($n in @("UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.UIModule.dll",
                 "UnityEngine.UI.dll", "UnityEngine.InputLegacyModule.dll",
                 "UnityEngine.TextRenderingModule.dll", "Unity.TextMeshPro.dll")) {
    $p = Join-Path $managed $n
    if (Test-Path -LiteralPath $p) { $refs += $p } else { Fail "缺少 $p" }
}
Ok ("引用 {0} 个程序集，**不含 Assembly-CSharp.dll**" -f $refs.Count)

# ---------------------------------------------------------------- 3/4 造临时工程
Write-Host ""
Write-Host "[3/4] 生成临时工程 .compilecheck\" -ForegroundColor Cyan
if (Test-Path -LiteralPath $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $src | Out-Null

foreach ($f in Get-ChildItem $platform -Filter *.cs) { Copy-Item $f.FullName $src }
Copy-Item (Join-Path $contract "A11yHost.cs") $src

# UiNav 的逐作区：这里给一份**最小占位**实现，只是为了让 partial class 编得过。
# 逐作真正的内容在 templates\game\UiNav.Game.cs.template 里。
$stub = @'
// 自检用的最小占位（不是逐作实现）。真东西见 templates\game\UiNav.Game.cs.template
using System;
using System.Collections.Generic;
using TMPro;

namespace A11yFramework
{
    internal static partial class UiNav
    {
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Placeholder", "占位" },
        };

        private static string InputFieldRoleName(TMP_InputField inf) { return ""; }
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path $src "UiNav.Game.cs"), $stub, (New-Object System.Text.UTF8Encoding $false))

$items = ""
foreach ($r in $refs) {
    $items += '    <Reference Include="' + [System.IO.Path]::GetFileNameWithoutExtension($r) + '">' + $NL
    $items += '      <HintPath>' + $r + '</HintPath>' + $NL
    $items += '      <Private>false</Private>' + $NL
    $items += '    </Reference>' + $NL
}
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>A11yFrameworkCompileCheck</AssemblyName>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
    <NoWarn>CS0169;CS0414;CS0649;MSB3277</NoWarn>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <ItemGroup>
$items  </ItemGroup>
</Project>
"@
[System.IO.File]::WriteAllText((Join-Path $work "CompileCheck.csproj"), $csproj, (New-Object System.Text.UTF8Encoding $false))

# ---------------------------------------------------------------- 4/4 编译
Write-Host ""
Write-Host "[4/4] dotnet build（不引用 Assembly-CSharp）" -ForegroundColor Cyan
Push-Location $work
try {
    & dotnet build -c Release -v minimal --nologo 2>&1 | ForEach-Object { Write-Host "      $_" }
    $code = $LASTEXITCODE
} finally { Pop-Location }

if ($code -ne 0) {
    Write-Host ""
    Fail "编译失败 —— 平台层引用了不该引用的东西（常见原因：引用了 Assembly-CSharp 里的游戏类型）。临时工程保留在 .compilecheck\ 供排查。"
}

Write-Host ""
Write-Host "PASS  平台层只依赖 UnityEngine / TMPro / BepInEx，**没有游戏类型依赖**。" -ForegroundColor Green
Write-Host "      注意：这证明的是「编得过」，不是「跑得对」——" -ForegroundColor Yellow
Write-Host "      行为正确性要靠实机跑 docs\templates\测试问卷.md。" -ForegroundColor Yellow
Write-Host ""

if (-not $Keep) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
exit 0
}

# ============================================================================
# 模式 B：逐作仓静态守卫（不需要游戏目录）
# ============================================================================
Write-Host ""
Write-Host "=== 逐作仓静态守卫（模式 B：无 platform\，只查逐作层）===" -ForegroundColor Cyan

$nsDir = Get-ChildItem -LiteralPath $pergameSrc -Directory |
         Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName ($_.Name + ".csproj")) } |
         Select-Object -First 1
if (-not $nsDir) {
    $nsDir = Get-ChildItem -LiteralPath $pergameSrc -Directory | Select-Object -First 1
}
if (-not $nsDir) { Fail "mod\src\ 下没有目录" }
$ns = $nsDir.Name
Write-Host "    namespace / 程序集：$ns  ($($nsDir.FullName))" -ForegroundColor Gray

# ---------------------------------------------------------------- B1: L1 清单
Write-Host ""
Write-Host "[1/3] L1 清单完整性（mod\src\$ns\ ⊇ tools\l1-files.txt）" -ForegroundColor Cyan
$manifest = Join-Path $root "tools\l1-files.txt"
if (Test-Path -LiteralPath $manifest) {
    $expected = @(Get-Content -LiteralPath $manifest -Encoding UTF8 |
                  Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith("#") } |
                  ForEach-Object { $_.Trim() })
} else {
    Write-Host "  警告：tools\l1-files.txt 不存在，退回内置保底清单（可能落后于 Framework）" -ForegroundColor Yellow
    Write-Host "        重新跑一次 tools\new_game.py ... --merge 可生成清单。" -ForegroundColor Yellow
    $expected = @("GlobalKeys.cs", "Hotkey.cs", "KeyEdge.cs", "Nvda.cs", "Repeat.cs",
                  "Sapi.cs", "Speech.cs", "UiNav.cs", "Zdsr.cs")
}
if ($expected -notcontains "GlobalKeys.cs") { $expected += "GlobalKeys.cs" }

$missing = @()
foreach ($fn in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $nsDir.FullName $fn))) { $missing += $fn }
}
if ($missing.Count -gt 0) {
    $hint = ""
    if ($missing -contains "GlobalKeys.cs") {
        $hint = " 尤其 GlobalKeys.cs：KeyEdge.cs 调 GlobalKeys.Reading/Held/SnapshotWords/" +
                "FillOtherKeySnapshot/AnyNewOtherKeyHeld/ResetPressed，删掉必然 CS0103。"
    }
    # 注意：-f 只作用于紧挨着的字符串，整串必须先拼接再套括号，否则 {0} 原样输出
    Fail (("L1 文件缺失：{0}。重新跑 tools\new_game.py ... --merge 从 Framework 补拷。" +
           $hint) -f ($missing -join ", "))
}
Ok ("L1 清单齐全（{0} 个文件）" -f $expected.Count)

# ---------------------------------------------------------------- B2: NameAlias
Write-Host ""
Write-Host "[2/3] NameAlias 大小写唯一（OrdinalIgnoreCase 下重复键 = 导航静默死亡）" -ForegroundColor Cyan
if ([string]::IsNullOrEmpty($GameSrc)) { $GameSrc = Join-Path $nsDir.FullName "UiNav.Game.cs" }
elseif (-not [System.IO.Path]::IsPathRooted($GameSrc)) { $GameSrc = Join-Path $root $GameSrc }
Write-Host "    检查：$GameSrc" -ForegroundColor Gray
Test-NameAlias $GameSrc -Required

# ---------------------------------------------------------------- B3: 重读出口 + 配置项读取点
Write-Host ""
Write-Host "[3/3] 重读缓冲区唯一出口 / 配置项读取点（启发式）" -ForegroundColor Cyan
$lint = Join-Path $root "tools\lint_repeat.ps1"
if (Test-Path -LiteralPath $lint) {
    # Repeat.cs 是 L1 拷进来的、平台层唯一允许直接调 Speech.Speak 的文件；
    # 逐作层自己的 Plugin/UiNav.Game/Choices/TextProc 都不许绕。
    & $lint -Path $nsDir.FullName -AllowFile "Repeat.cs"
    if ($LASTEXITCODE -ne 0) { Fail "逐作层里有绕过重读缓冲区的播报 —— 玩家按重读键听不到它" }
} else {
    Write-Host "  警告：找不到 tools\lint_repeat.ps1，跳过重读出口检查" -ForegroundColor Yellow
}

# 启发式：Config.Bind 赋给的字段如果在整个逐作层只出现一次（声明处），
# 多半是「绑了但从没读过」——玩家改了配置不生效。只警告，不 fail
# （有些配置项确实只给平台层通过契约读取，字段名会出现在 Plugin.cs 的多处）。
$pluginCs = Join-Path $nsDir.FullName "Plugin.cs"
if (Test-Path -LiteralPath $pluginCs) {
    $pText = [System.IO.File]::ReadAllText($pluginCs)
    $bindFields = @()
    foreach ($m in [regex]::Matches($pText, '(\w+)\s*=\s*Config\.Bind\s*<')) {
        $bindFields += $m.Groups[1].Value
    }
    if ($bindFields.Count -eq 0) {
        # 旧写法：Config.Bind("组", "项", ...) 直接赋给字段
        foreach ($m in [regex]::Matches($pText, '(\w+)\s*=\s*Config\.Bind\s*\(')) {
            $bindFields += $m.Groups[1].Value
        }
    }
    $noRead = @()
    if ($bindFields.Count -gt 0) {
        $allText = ""
        foreach ($f in Get-ChildItem $nsDir.FullName -Recurse -File -Filter *.cs) {
            $allText += [System.IO.File]::ReadAllText($f.FullName) + "`n"
        }
        foreach ($fld in ($bindFields | Select-Object -Unique)) {
            $cnt = ([regex]::Matches($allText, "\b" + [regex]::Escape($fld) + "\b")).Count
            if ($cnt -le 1) { $noRead += $fld }
        }
    }
    if ($noRead.Count -gt 0) {
        Write-Host ("  --  警告：这些配置项只有 Bind、没有读取点（改了不生效？）：{0}" -f ($noRead -join ", ")) -ForegroundColor Yellow
        Write-Host "        启发式检查，确认是给平台层用的就忽略本条。" -ForegroundColor Yellow
    } else {
        Ok "配置项都有读取点（启发式）"
    }
}

Write-Host ""
Write-Host "PASS  逐作仓静态守卫通过。" -ForegroundColor Green
Write-Host "      编译验证请跑 mod\build.ps1（需要游戏目录取 UnityEngine / BepInEx 引用）；" -ForegroundColor Yellow
Write-Host "      行为正确性要靠实机跑 docs\templates\测试问卷.md。" -ForegroundColor Yellow
Write-Host ""
exit 0
