# 一键验证：构建 → 三道静态闸门 → 发行包自检 → 发布包冒烟
# 用法：
#   .\tools\verify_all.ps1 -GameDir 'D:\mk-test\game'
#   .\tools\verify_all.ps1 -GameDir 'D:\mk-test\game' -MenuWalk
#   .\tools\verify_all.ps1 -SkipSmoke            # 只跑静态守卫（不启动游戏）
#   .\tools\verify_all.ps1 -RepoRoot 'D:\DSHWorkSpace\belfry_a11y' -GameDir 'D:\mk-test\belfry' -ExpectedPatches 14
#
# 逐作仓通用：不写死 namespace 目录、exe 名、cfg 名、挂载点数，换仓只传参数。
# 退出码：0 = 全部通过；1 = 有步骤失败（摘要里会标 FAIL）。

[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$GameDir = "",
    [switch]$MenuWalk,
    [switch]$SkipSmoke,
    [switch]$NoStage,
    [int]$ExpectedPatches = 17,
    [string]$ExeName = "MeiKongProject.exe",
    [string]$ConfigName = "meikong.a11y.reader.cfg"
)

$ErrorActionPreference = "Stop"
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$mod  = Join-Path $repo "mod"

function Find-PerGameDir {
    # 逐作源码目录 = mod\src\ 下含 Plugin.cs 的那一层（有多个时优先带 A11yHost.cs 的）
    $srcRoot = Join-Path $mod "src"
    if (-not (Test-Path -LiteralPath $srcRoot)) { return $null }
    $cands = @(Get-ChildItem -LiteralPath $srcRoot -Directory | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName "Plugin.cs")) -or
        (Test-Path -LiteralPath (Join-Path $_.FullName "UiNav.Game.cs"))
    })
    if ($cands.Count -eq 0) { return $null }
    $withContract = @($cands | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "A11yHost.cs") })
    if ($withContract.Count -ge 1) { return $withContract[0].FullName }
    return $cands[0].FullName
}

$results = New-Object System.Collections.ArrayList
function Add-Result([string]$name, [bool]$ok, [string]$note) {
    [void]$results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Note = $note })
    $mark = if ($ok) { "ok  " } else { "FAIL" }
    Write-Host ("  [{0}] {1}{2}" -f $mark, $name, $(if ($note) { "  $note" } else { "" }))
}
function Step([string]$title) {
    Write-Host ""
    Write-Host ("=== " + $title + " ===")
}

Write-Host ("仓库：{0}" -f $repo)
$perGame = Find-PerGameDir
if ($perGame) {
    Write-Host ("逐作源码目录：{0}" -f $perGame)
} else {
    Write-Host "逐作源码目录：未找到（mod\src\ 下没有含 Plugin.cs 的目录）"
}

Step "1/5  构建并组包（mod\build.ps1）"
if (-not (Test-Path -LiteralPath (Join-Path $mod "build.ps1"))) {
    Add-Result "build.ps1（编译 + 组包）" $false "mod\build.ps1 不存在"
    Write-Host "`n构建脚本缺失，后续步骤无意义，终止。"; exit 1
}
$buildArgs = @{}
if ($GameDir) { $buildArgs.GameDir = $GameDir }
& (Join-Path $mod "build.ps1") @buildArgs | ForEach-Object { Write-Host $_ }
$buildOk = ($LASTEXITCODE -eq 0)
Add-Result "build.ps1（编译 + 组包）" $buildOk ""
if (-not $buildOk) { Write-Host "`n构建失败，后续步骤无意义，终止。"; exit 1 }

Step "2/5  静态守卫"
& (Join-Path $PSScriptRoot "compile_check.ps1") | ForEach-Object { Write-Host $_ }
Add-Result "compile_check.ps1（L1 清单 / NameAlias / 重读出口）" ($LASTEXITCODE -eq 0) ""

if ($perGame) {
    & (Join-Path $PSScriptRoot "lint_repeat.ps1") -Path $perGame -AllowFile "Repeat.cs" | ForEach-Object { Write-Host $_ }
    Add-Result "lint_repeat.ps1（逐作层不许 Speech.Speak）" ($LASTEXITCODE -eq 0) ""
} else {
    Add-Result "lint_repeat.ps1（逐作层不许 Speech.Speak）" $false "未定位逐作源码目录"
}

& (Join-Path $PSScriptRoot "package_check.ps1") | ForEach-Object { Write-Host $_ }
Add-Result "package_check.ps1（发行包十二项）" ($LASTEXITCODE -eq 0) ""

if (-not $NoStage) {
    & git -C $repo add -A 2>&1 | ForEach-Object { Write-Host $_ }
}
& (Join-Path $PSScriptRoot "check-staged.ps1") | ForEach-Object { Write-Host $_ }
Add-Result "check-staged.ps1（版权 / 二进制 / BOM / 第 0 条纪律）" ($LASTEXITCODE -eq 0) ""

Step "3/5  发布包冒烟（可跳过：-SkipSmoke）"
if ($SkipSmoke) {
    Add-Result "smoke.ps1" $true "跳过（-SkipSmoke）"
} else {
    $smokeArgs = @{ ExpectedPatches = $ExpectedPatches; ExeName = $ExeName; ConfigName = $ConfigName }
    if ($GameDir) { $smokeArgs.GameDir = $GameDir }
    if ($MenuWalk) { $smokeArgs.MenuWalk = $true }
    if (-not $GameDir) {
        Write-Host "  未指定 -GameDir：交给 smoke.ps1 自己按 Steam 注册表定位（可能是非 ASCII 路径，会被它拒绝）"
    }
    & (Join-Path $PSScriptRoot "smoke.ps1") @smokeArgs | ForEach-Object { Write-Host $_ }
    Add-Result ("smoke.ps1（加载 / {0} 挂载点 / 语音后端 / 无异常）" -f $ExpectedPatches) ($LASTEXITCODE -eq 0) ""
}

Step "4/5  发行包"
$zip = Get-ChildItem (Join-Path $mod "dist\*.zip") -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($zip) {
    Add-Result "zip 已生成" $true ("{0}（{1:N0} 字节）" -f $zip.Name, $zip.Length)
} else {
    Add-Result "zip 已生成" $false "mod\dist\ 下没有 zip"
}

Step "5/5  摘要"
$fail = @($results | Where-Object { -not $_.Ok })
foreach ($r in $results) {
    $mark = if ($r.Ok) { "ok  " } else { "FAIL" }
    Write-Host ("  [{0}] {1}" -f $mark, $r.Name)
}
if ($fail.Count -gt 0) {
    Write-Host ""
    Write-Host ("验证未通过：{0} 项失败。" -f $fail.Count)
    exit 1
}
Write-Host ""
Write-Host "全部通过。注意：这只是「可构建 + 可冒测 + 可交付」的机器证据；"
Write-Host "听感验收仍需按 docs\templates\测试问卷.md 第 2 / 4 / 8 节由人实机确认。"
exit 0
