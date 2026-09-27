#Requires -Version 5.1
<#
    平台层自检 —— 证明 L1（platform\）**没有任何游戏类型依赖**。

    做法：把 platform\*.cs 与 contract\A11yHost.cs 拷进一个临时工程编译，
    引用 UnityEngine / TMPro / BepInEx，**故意不引用 Assembly-CSharp**。

        编得过  ⇒ 平台层确实只依赖引擎与 BepInEx，没有偷偷引用游戏类型。

    这就是《无障碍补丁流水线》§8 出口检查清单里
    「共享层无任何游戏类型引用」那一条的**机械验证**，而不是靠人眼看。

    同时它还会断言 platform\ 里没有任何 Plugin. 残留 ——
    有残留说明接口没有全部改指 A11yHost，逐作拷贝时会编译失败。

    用法：
        .\tools\compile_check.ps1
        .\tools\compile_check.ps1 -GameDir "F:\Steam\steamapps\common\妹控计划"
        .\tools\compile_check.ps1 -Keep          # 保留临时工程，便于看编译错误

    注意：本脚本必须保存为 UTF-8 **带 BOM** —— Windows PowerShell 5.1
    读无 BOM 的脚本会按 GBK 解码，中文注释变乱码并直接语法错误（流水线 G5）。
#>
[CmdletBinding()]
param(
    # 借一个已安装的游戏来取 UnityEngine / TMPro / BepInEx 的引用。
    # 平台层不引用任何游戏类型，所以用哪个游戏都可以。
    [string]$GameDir = "F:\Steam\steamapps\common\妹控计划",
    [string]$BepInExCore = "",
    [switch]$Keep
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

Write-Host ""
Write-Host "=== A11yFramework 平台层自检 ===" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1/4 静态断言
Write-Host ""
Write-Host "[1/4] 静态断言：platform\ 的代码里不许有 Plugin. 引用（注释不算）" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $platform)) { Fail "找不到 platform\（请在 A11yFramework 仓库根目录下运行）" }

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

# ---------------------------------------------------------------- 2/4 找引用
Write-Host ""
Write-Host "[2/4] 解析引用（GameDir = $GameDir）" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $GameDir)) { Fail "找不到游戏目录: $GameDir（用 -GameDir 指定）" }

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
