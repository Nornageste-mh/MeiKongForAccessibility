# 发行包逐项自检（流水线 P6 十二项，Mono / BepInEx 5 路线）
#
# 用法：  pwsh -NoProfile -File .\tools\package_check.ps1
#         pwsh -NoProfile -File .\tools\package_check.ps1 -PackageDir mod\package
#
# 任一项失败 → exit 1（不允许「警告后继续打包」）。
# 说明：本作是 Unity Mono 路线，第 3 项（IL2CPP interop SHA）标 N/A，
#       但会反向断言包里**没有** IL2CPP 专属内容。

param(
    [string]$PackageDir = "mod\package"
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($PackageDir)) { $PackageDir = Join-Path $repo $PackageDir }

$fail = New-Object System.Collections.Generic.List[string]
$na = New-Object System.Collections.Generic.List[string]

function Check([string]$name, [scriptblock]$test) {
    try {
        $r = & $test
        if ($r -eq $true) { Write-Host ("  ok   " + $name) -ForegroundColor Green }
        elseif ($r -eq 'na') { Write-Host ("  N/A  " + $name) -ForegroundColor DarkGray; $na.Add($name) }
        else { Write-Host ("  FAIL " + $name) -ForegroundColor Red; $fail.Add($name) }
    } catch {
        Write-Host ("  FAIL " + $name + " → " + $_.Exception.Message) -ForegroundColor Red
        $fail.Add($name)
    }
}

function Rel([string]$p) { $p.Substring($PackageDir.Length + 1) }

$project = Join-Path $repo 'mod\src\MeiKongA11y\MeiKongA11y.csproj'
$changelog = Join-Path $repo 'CHANGELOG.md'

Write-Host "发行包自检：$PackageDir" -ForegroundColor Cyan
if (-not (Test-Path $PackageDir)) { Write-Host "  FAIL 包目录不存在" -ForegroundColor Red; exit 1 }

$dll = Join-Path $PackageDir 'BepInEx\plugins\MeiKongA11y.dll'
$files = @(Get-ChildItem $PackageDir -Recurse -File)

# 1. 插件 dll 存在且版本 = csproj = CHANGELOG 顶部
Check "1. 插件 dll 存在，版本与 csproj / CHANGELOG 顶部一致" {
    if (-not (Test-Path $dll)) { return $false }
    $dllVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    $projVer = ([regex]::Match((Get-Content $project -Raw -Encoding UTF8), '<Version>([0-9.]+)</Version>')).Groups[1].Value
    $chgTop = ([regex]::Match((Get-Content $changelog -Raw -Encoding UTF8), '(?m)^## ([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')).Groups[1].Value
    if ($dllVer -ne $projVer -or $projVer -ne $chgTop) {
        Write-Host ("      dll=$dllVer csproj=$projVer CHANGELOG=$chgTop") -ForegroundColor DarkYellow
        return $false
    }
    $true
}

# 2. BepInEx 运行时（Mono 路线 5 件套）
Check "2. BepInEx 运行时齐全（Mono：BepInEx / Preloader / 0Harmony / MonoMod.RuntimeDetour / MonoMod.Utils）" {
    $core = Join-Path $PackageDir 'BepInEx\core'
    $need = @('BepInEx.dll', 'BepInEx.Preloader.dll', '0Harmony.dll', 'MonoMod.RuntimeDetour.dll', 'MonoMod.Utils.dll')
    $missing = $need | Where-Object { -not (Test-Path (Join-Path $core $_)) }
    if ($missing) { Write-Host ("      缺: " + ($missing -join ', ')) -ForegroundColor DarkYellow; return $false }
    $true
}

# 3. IL2CPP 专属项：本作是 Mono → N/A，但反向断言没有 interop
Check "3. IL2CPP interop 配对（Mono 路线不适用；断言包内无 interop）" {
    if (Test-Path (Join-Path $PackageDir 'BepInEx\interop')) { return $false }
    'na'
}

# 4. nvdaControllerClient.dll（x64）在 plugins 与包根
Check "4. nvdaControllerClient.dll（x64）在 plugins 与包根，且是 64 位 PE" {
    $a = Join-Path $PackageDir 'BepInEx\plugins\nvdaControllerClient.dll'
    $b = Join-Path $PackageDir 'nvdaControllerClient.dll'
    if (-not (Test-Path $a) -or -not (Test-Path $b)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($a)
    $peOff = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $peOff + 4)
    if ($machine -ne 0x8664) { Write-Host ("      machine=0x{0:X}" -f $machine) -ForegroundColor DarkYellow; return $false }
    $true
}

# 5. 两份自产文档
Check "5. 安装说明.md / 常见问题.md 都在包根" {
    (Test-Path (Join-Path $PackageDir '安装说明.md')) -and (Test-Path (Join-Path $PackageDir '常见问题.md'))
}

# 6. 许可证只带实际随包组件；不得出现别作模板
Check "6. licenses 只含随包组件，且无 THIRD-PARTY-NOTICES.example-*" {
    $lic = Join-Path $PackageDir 'licenses'
    if (-not (Test-Path $lic)) { return $false }
    if (-not (Test-Path (Join-Path $lic 'THIRD-PARTY-NOTICES.txt'))) { Write-Host "      缺 THIRD-PARTY-NOTICES.txt" -ForegroundColor DarkYellow; return $false }
    $bad = @(Get-ChildItem $lic -File | Where-Object { $_.Name -like 'THIRD-PARTY-NOTICES.example-*' })
    if ($bad.Count -gt 0) { Write-Host ("      别作模板: " + (($bad | ForEach-Object Name) -join ', ')) -ForegroundColor DarkYellow; return $false }
    # IL2CPP 路线专属许可证不该出现在 Mono 包里
    $il2cpp = @(Get-ChildItem $lic -File | Where-Object { $_.Name -match 'Il2Cpp|il2cpp|Cpp2IL|AsmResolver|AssetRipper|Capstone|Dobby|Disarm|Iced|uTinyRipper' })
    if ($il2cpp.Count -gt 0) { Write-Host ("      IL2CPP 专属许可证: " + (($il2cpp | ForEach-Object Name) -join ', ')) -ForegroundColor DarkYellow; return $false }
    $true
}

# 7. 不含 interop / unity-libs / 玩家配置
Check "7. 不含 BepInEx\interop、unity-libs、config\<guid>.cfg" {
    $bad = $files | Where-Object {
        $r = Rel $_.FullName
        $r -like 'BepInEx\interop*' -or $r -like '*unity-libs*' -or $r -match 'BepInEx\\config\\.*\.cfg$'
    }
    if ($bad) { Write-Host ("      " + (($bad | ForEach-Object { Rel $_.FullName }) -join ', ')) -ForegroundColor DarkYellow; return $false }
    $true
}

# 8. 不含下载缓存 / 构建中间产物 / 测试报告
Check "8. 不含 dl\ / bep_dl\ / nvda_dl\ / dist\ / bin\ / obj\ / 测试报告" {
    $bad = $files | Where-Object {
        $r = Rel $_.FullName
        $r -match '^(dl|bep_dl|nvda_dl|dist|bin|obj)\\' -or $r -match '\\bin\\' -or $r -match '\\obj\\' -or
        $_.Name -match '测试报告|TestReport|\.trx$'
    }
    if ($bad) { Write-Host ("      " + (($bad | ForEach-Object { Rel $_.FullName }) -join ', ')) -ForegroundColor DarkYellow; return $false }
    $true
}

# 9. 不含剧本文本 / 截图 / 运行日志
Check "9. 不含截图、LogOutput.log、朗读日志（IP 红线）" {
    $bad = $files | Where-Object { $_.Extension -match '^\.(png|jpg|jpeg|gif|bmp|log)$' }
    if ($bad) { Write-Host ("      " + (($bad | ForEach-Object { Rel $_.FullName }) -join ', ')) -ForegroundColor DarkYellow; return $false }
    # 文本文件里不得出现朗读日志/剧本行标记
    $txt = $files | Where-Object { $_.Extension -match '^\.(md|txt|ini|json)$' }
    foreach ($f in $txt) {
        $c = Get-Content $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($null -ne $c -and ($c -match '\[朗读\]|\[台词\]|配音=有')) {
            Write-Host ("      含朗读/剧本标记: " + (Rel $f.FullName)) -ForegroundColor DarkYellow
            return $false
        }
    }
    $true
}

# 10. 不含别作残留（belfry / transparenther / noexistence 等路径或名字）
Check "10. 不含别作路径/名字残留（belfry / transparenther / noexistence / manosaba / justlilith）" {
    $bad = $files | Where-Object { (Rel $_.FullName) -match '(?i)belfry|transparenther|noexistence|manosaba|justlilith' }
    if ($bad) { Write-Host ("      " + (($bad | ForEach-Object { Rel $_.FullName }) -join ', ')) -ForegroundColor DarkYellow; return $false }
    $txt = $files | Where-Object { $_.Extension -match '^\.(md|txt|ini)$' }
    foreach ($f in $txt) {
        $c = Get-Content $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($null -ne $c -and $c -match '(?i)belfry|transparenther|noexistence') {
            Write-Host ("      文档提到别作: " + (Rel $f.FullName)) -ForegroundColor DarkYellow
            return $false
        }
    }
    $true
}

# 11. zip 条目分隔符全为 /（有 zip 才查）
Check "11. zip 条目分隔符全为 /" {
    $zips = @(Get-ChildItem $repo -Recurse -Filter *.zip -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\mod\\dist\\' -or $_.FullName -match '\\release' })
    if ($zips.Count -eq 0) { return 'na' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    foreach ($z in $zips) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($z.FullName)
        try {
            $bad = @($zip.Entries | Where-Object { $_.FullName -like '*\*' })
            if ($bad.Count -gt 0) { Write-Host ("      " + $z.Name + " 有反斜杠条目") -ForegroundColor DarkYellow; return $false }
        } finally { $zip.Dispose() }
    }
    $true
}

# 12. 解包后目录层级与安装说明一致
Check "12. 包根层级与安装说明一致（winhttp / doorstop / BepInEx / dll 都在根或 BepInEx 下）" {
    $need = @('winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.dll', 'BepInEx\plugins\MeiKongA11y.dll')
    $missing = $need | Where-Object { -not (Test-Path (Join-Path $PackageDir $_)) }
    if ($missing) { Write-Host ("      缺: " + ($missing -join ', ')) -ForegroundColor DarkYellow; return $false }
    # 不允许多余顶层目录（除 BepInEx / licenses 与已知文件外）
    $allowedFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt', 'nvdaControllerClient.dll', '安装说明.md', '常见问题.md')
    $top = @(Get-ChildItem $PackageDir)
    $extra = @($top | Where-Object { $_.PSIsContainer -and $_.Name -notin @('BepInEx', 'licenses') })
    $extraFiles = @($top | Where-Object { -not $_.PSIsContainer -and $_.Name -notin $allowedFiles })
    if ($extra.Count -gt 0 -or $extraFiles.Count -gt 0) {
        Write-Host ("      多余顶层项: " + ((($extra + $extraFiles) | ForEach-Object Name) -join ', ')) -ForegroundColor DarkYellow
        return $false
    }
    $true
}

Write-Host ""
if ($fail.Count -gt 0) {
    Write-Host ("包自检失败：" + $fail.Count + " 项") -ForegroundColor Red
    $fail | ForEach-Object { Write-Host ("  · " + $_) -ForegroundColor Red }
    exit 1
}
Write-Host ("包自检通过：" + (12 - $na.Count) + " 项通过" + $(if ($na.Count -gt 0) { "，" + $na.Count + " 项 N/A" } else { "" })) -ForegroundColor Green
exit 0
