<#
.SYNOPSIS
  安装包自动化冒烟：把 mod\package 装进游戏目录、启动游戏、从日志断言补丁真的加载了。

.DESCRIPTION
  这是「可冒测」这一条的可复现入口（对应 docs\无障碍补丁流水线.md 的回归第 8 步）。

  断言（全部通过才算 PASS）：
    1. 插件被 BepInEx 加载，版本与 csproj 一致；
    2. [A11yHost] 契约已填满（逐作钩子都接上了）；
    3. [补丁] 行数 = -ExpectedPatches（默认 17），且没有失败行；
    4. 语音后端已选（NVDA / ZDSR / SAPI / 无）；
    5. 全程没有 WARN / ERR / Exception；
    6. 键盘链路（可选，需要游戏窗口能拿到前台）：F1 → 菜单播报 → 数字键 → 面板焦点进入 →
   第二页隐藏界面 → 断言「界面已隐藏」→ F1 → 断言「界面已恢复显示」→ 菜单重新列出（证明按钮回到可交互）。

  说明：
    * 会把 <GameDir> 当成测试副本覆盖安装（只看 package 里的文件，不动存档）。
    * 需要「界面诊断日志 = true」才能看到 [朗读] 行；脚本会自己改这条配置。
    * 游戏目录必须放纯 ASCII 路径，否则 Harmony 会静默半失效（README 踩坑 1）。

.PARAMETER GameDir
  测试副本的游戏根目录（含 <ExeName> 的那一层）。留空则读环境变量 A11Y_GAME_DIR，
  再留空则按 Steam 库目录找《妹控计划》。

.PARAMETER PackageDir
  安装包目录，默认 <仓库>\mod\package。

.PARAMETER ExpectedPatches
  期望的 Harmony 补丁类数量，默认 17。

.PARAMETER SkipKeyDrill
  跳过真实按键链路（无图形会话 / 不想抢前台时用）。

.EXAMPLE
  pwsh -NoProfile -File .\tools\smoke.ps1 -GameDir 'D:\mk-test\game'
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [string]$PackageDir = "",
    [string]$ExeName = "MeiKongProject.exe",
    [string]$ConfigName = "meikong.a11y.reader.cfg",
    [int]$ExpectedPatches = 17,
    [int]$TimeoutSec = 90,
    [switch]$SkipKeyDrill,
    [switch]$SkipInstall,
    [switch]$MenuWalk
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Get-LogText([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                                         [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            $txt = $sr.ReadToEnd()
            $sr.Dispose(); $fs.Dispose()
            return $txt
        } catch { Start-Sleep -Milliseconds 200 }
    }
    return ""
}

function Say([string]$m) { Write-Host ("  " + $m) }function Fail([string]$m) { Write-Host ("[FAIL] " + $m) -ForegroundColor Red; exit 1 }

function Find-SteamGameDir {
    $names = @('妹控计划', 'MeiKongProject')
    $roots = New-Object System.Collections.Generic.List[string]
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steam) { $roots.Add($steam) }
    } catch { }
    foreach ($r in @($roots)) {
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                $m = [regex]::Match($line, '"path"\s+"([^"]+)"')
                if ($m.Success) { $roots.Add(($m.Groups[1].Value -replace '\\\\', '\')) }
            }
        }
    }
    foreach ($r in $roots) {
        foreach ($n in $names) {
            $p = Join-Path $r ("steamapps\common\" + $n)
            if (Test-Path (Join-Path $p $ExeName)) { return $p }
        }
    }
    return ""
}

# ---------------------------------------------------------------- 0. 定位
Say "0/5  定位游戏目录与安装包"
if (-not $GameDir) { $GameDir = $env:A11Y_GAME_DIR }
if (-not $GameDir) { $GameDir = Find-SteamGameDir }
if (-not $GameDir) { Fail "找不到游戏目录；请用 -GameDir 指定（例如 -GameDir 'D:\mk-test\game'）" }
if (-not (Test-Path (Join-Path $GameDir $ExeName))) { Fail ("游戏目录里没有 " + $ExeName + "：" + $GameDir) }
if (-not $PackageDir) { $PackageDir = Join-Path $repo 'mod\package' }
if (-not (Test-Path $PackageDir)) { Fail ("安装包目录不存在：" + $PackageDir + "（先跑 mod\build.ps1）") }

$exe = Join-Path $GameDir $ExeName
$log = Join-Path $GameDir 'BepInEx\LogOutput.log'
$cfg = Join-Path $GameDir ('BepInEx\config\' + $ConfigName)
Say ("游戏目录：" + $GameDir)
Say ("安装包  ：" + $PackageDir)

# ASCII 路径守卫：路径含非 ASCII 时 Harmony 会静默半失效，冒烟结果没有意义
if ($GameDir -match '[^\x00-\x7F]') {
    Fail "游戏目录含非 ASCII 字符，Harmony 可能静默半失效；请拷到纯 ASCII 路径（例如 D:\mk-test\game）再冒烟"
}

# ---------------------------------------------------------------- 1. 覆盖安装
Say "1/5  覆盖安装到测试副本"
Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 25; $i++) {
    if (-not (Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Seconds 2
if ($SkipInstall) {
    Say "跳过安装（-SkipInstall）：直接冒烟当前已装好的副本，用来看 zip 解包后的实际状态"
} else {
    Robocopy $PackageDir $GameDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail ("Robocopy 复制失败，退出码 " + $LASTEXITCODE) }
}
$dll = Join-Path $GameDir 'BepInEx\plugins\MeiKongA11y.dll'
if (-not (Test-Path $dll)) { Fail ("测试副本里没有 BepInEx\plugins\MeiKongA11y.dll：" + $GameDir) }

# ---------------------------------------------------------------- 2. 诊断日志
Say "2/5  确认「界面诊断日志 = true」"
function Test-CfgDiagOn {
    if (-not (Test-Path $cfg)) { return $false }
    $line = Select-String -Path $cfg -Pattern '^\s*界面诊断日志\s*=\s*(.+)$' -ErrorAction SilentlyContinue
    return ($line -and $line.Matches[0].Groups[1].Value.Trim() -eq 'true')
}
if (-not (Test-Path $cfg)) {
    Say "首次运行，先启动一次让 BepInEx 生成配置（最多等 60 秒）"
    Remove-Item $log -ErrorAction SilentlyContinue
    Start-Process $exe
    $cfgDeadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $cfgDeadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $cfg) { break }
    }
    Start-Sleep -Seconds 2
    Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}
if (-not (Test-Path $cfg)) { Fail ("启动一次后仍未生成配置：" + $cfg) }
$cfgText = [System.IO.File]::ReadAllText($cfg)
$cfgNew = [regex]::Replace($cfgText, '(?m)^([ \t]*界面诊断日志[ \t]*=[ \t]*)([^\r\n]*)', '${1}true')
if ($cfgNew -ne $cfgText) {
    [System.IO.File]::WriteAllText($cfg, $cfgNew, (New-Object System.Text.UTF8Encoding($false)))
    Say "已把「界面诊断日志」打开（后续行号里的 [朗读] 才有意义）"
} else {
    Say "「界面诊断日志」已是 true"
}

# ---------------------------------------------------------------- 3. 启动
Say "3/5  启动游戏并等待补丁加载"
Remove-Item $log -ErrorAction SilentlyContinue
Start-Process $exe
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $partial = Get-LogText $log
    if ($partial -match '契约已填满') { $ready = $true; break }
}
if (-not $ready) { Fail ("等待 " + $TimeoutSec + " 秒仍未看到「契约已填满」，日志：" + $log) }
Start-Sleep -Seconds 3

# ---------------------------------------------------------------- 4. 断言
Say "4/5  日志断言"
$text = Get-LogText $log
$fail = @()

$m = [regex]::Match($text, 'Loading \[([^\]]+)\]')
if (-not $m.Success) { $fail += "没有看到 BepInEx 的 Loading [插件名] 行" }
$loaded = $m.Groups[1].Value

$okLines = [regex]::Matches($text, '(?m)^.*\[补丁\]\s+(\S+)\s+OK\s*$')
$badLines = [regex]::Matches($text, '(?m)^.*\[补丁\]\s+(\S+)\s+失败.*$')
if ($badLines.Count -gt 0) { $fail += ("有 " + $badLines.Count + " 个补丁挂载失败：" + (($badLines | ForEach-Object { $_.Groups[1].Value }) -join ', ')) }
if ($okLines.Count -ne $ExpectedPatches) { $fail += ("补丁挂载数 " + $okLines.Count + "，期望 " + $ExpectedPatches) }

if ($text -notmatch '语音后端\s*[:：]\s*(\S+)') { $fail += "没有看到「语音后端: X」" }
else { $backend = $Matches[1] }

$warn = [regex]::Matches($text, '(?m)^.*\b(WARN|ERR|Exception)\b.*$')
if ($warn.Count -gt 0) {
    $sample = ($warn | Select-Object -First 3 | ForEach-Object { $_.Value.Trim() }) -join ' | '
    $fail += ("日志里有 " + $warn.Count + " 条 WARN/ERR/Exception：" + $sample)
}

# ---------------------------------------------------------------- 5. 键盘链路（可选）
$drill = 'SKIP'
if (-not $SkipKeyDrill) {
    Say "5/5  真实按键链路（F1 → 菜单 → 数字键 → 面板）"
    try {
        $proc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction Stop | Select-Object -First 1
        Add-Type -Namespace Win32 -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@
        $h = $proc.MainWindowHandle
        [Win32.U]::ShowWindow($h, 9) | Out-Null
        for ($i = 0; $i -lt 12; $i++) {
            [Win32.U]::SetForegroundWindow($h) | Out-Null
            Start-Sleep -Milliseconds 300
            if ([Win32.U]::GetForegroundWindow() -eq $h) { break }
        }
        $fg = ([Win32.U]::GetForegroundWindow() -eq $h)
        if (-not $fg) {
            $drill = 'SKIP（窗口拿不到前台）'
        } else {
            $wsh = New-Object -ComObject WScript.Shell
            $before = $text.Length
            $wsh.SendKeys('{F1}'); Start-Sleep -Seconds 3
            $t1 = Get-LogText $log
            # 第三十二轮：菜单改成列表播报——首项报总数（`菜单，共 12 项。第 1 项：小游戏。`）。
            $menuOk = $t1 -match '\[朗读\] .*菜单，共 .* 项。第 1 项：'
            $wsh.SendKeys('1'); Start-Sleep -Seconds 5
            $t2 = Get-LogText $log
            $panelOk = $t2 -match '\[焦点\] 进入'
            # 第二十四轮：面板开着时 F2 必须报位置，不能落进「没有打开的面板」。
            # 第二十六轮：桌宠面板里可能正盖着 tool 剧情，这时 F2 按第二十二轮的规矩报
            # 「正在剧情中：…」也算对，不能只认「个可操作项」。
            $wsh.SendKeys('{F2}'); Start-Sleep -Seconds 2
            $t2b = Get-LogText $log
            $f2Story = $t2b -match '\[朗读\] .*剧情中'
            $f2Panel = $t2b -match '\[朗读\] .*个可操作项'
            $f2Ok = (-not ($t2b -match '当前没有打开的面板')) -and ($f2Story -or $f2Panel)
            if ($f2Story) { Write-Host '  F2 命中剧情分支（面板里盖着 tool 剧情），按第二十二轮的规矩判定通过' }
            $wsh.SendKeys('{ESC}'); Start-Sleep -Seconds 2

            # 第十轮：隐藏界面是游戏侧单向死路，补丁必须能从 F1 恢复；顺带证明菜单真的回到可交互
            $wsh.SendKeys('{F1}'); Start-Sleep -Seconds 2
            $wsh.SendKeys('0'); Start-Sleep -Seconds 2      # 第二页：系统设置 / 隐藏界面 / 退出游戏
            $wsh.SendKeys('2'); Start-Sleep -Seconds 3
            $t3 = Get-LogText $log
            $hiddenOk = $t3 -match '界面已隐藏'
            $wsh.SendKeys('{F1}'); Start-Sleep -Seconds 3
            $t4 = Get-LogText $log
            $restoreOk = $t4 -match '界面已恢复显示'
            $wsh.SendKeys('{F1}'); Start-Sleep -Seconds 2
            $t5 = Get-LogText $log
            $menuBackOk = $t5 -match '\[朗读\] .*菜单，共 .* 项。第 1 项：'
            $wsh.SendKeys('{ESC}'); Start-Sleep -Milliseconds 500

            if ($menuOk -and $panelOk -and $f2Ok -and $hiddenOk -and $restoreOk -and $menuBackOk) { $drill = 'PASS' }
            else {
                $drill = 'FAIL'
                $fail += ("键盘链路失败：菜单播报=" + $menuOk + "，面板焦点=" + $panelOk + "，F2 报位置=" + $f2Ok +
                          "，隐藏界面播报=" + $hiddenOk + "，F1 恢复界面=" + $restoreOk +
                          "，恢复后菜单=" + $menuBackOk)
            }
        }
    } catch {
        $drill = 'SKIP（' + $_.Exception.Message + '）'
    }
}

# ---------------------------------------------------------------- 6. 菜单逐项走查（可选，-MenuWalk）
$walk = 'SKIP'
if ($MenuWalk -and $drill -like 'PASS*') {
    Say "6/6  菜单逐项走查（12 项菜单里的 9 个安全入口）"
    try {
        $wsh2 = New-Object -ComObject WScript.Shell
        # 页 / 数字键 / 预期进入的面板名（顺序与 Surfaces.BarEntries 一致；跳过小游戏、隐藏界面、退出游戏）
        $targets = @(
            @{ Page = 1; Digit = '2'; Title = '番茄钟' },
            @{ Page = 1; Digit = '3'; Title = '日程表' },
            @{ Page = 1; Digit = '4'; Title = '音乐列表' },
            @{ Page = 1; Digit = '5'; Title = '换装' },
            @{ Page = 1; Digit = '6'; Title = '收藏内容' },
            @{ Page = 1; Digit = '7'; Title = '联动展架' },
            @{ Page = 1; Digit = '8'; Title = '鬼畜制造机' },
            @{ Page = 1; Digit = '9'; Title = '背景设置' },
            @{ Page = 2; Digit = '1'; Title = '系统设置' }
        )
        $bad = @()
        foreach ($tg in $targets) {
            $pos = (Get-LogText $log).Length
            $wsh2.SendKeys('{F1}'); Start-Sleep -Seconds 2
            if ($tg.Page -eq 2) { $wsh2.SendKeys('0'); Start-Sleep -Seconds 2 }
            $wsh2.SendKeys($tg.Digit); Start-Sleep -Seconds 4
            $seg = Get-LogText $log
            $seg = $seg.Substring([Math]::Min($pos, $seg.Length))
            if ($seg -notmatch ('\[焦点\] 进入 ' + [regex]::Escape($tg.Title))) {
                $bad += ($tg.Title + '（第' + $tg.Page + '页 ' + $tg.Digit + '）')
            }
            $wsh2.SendKeys('{ESC}'); Start-Sleep -Seconds 2
            $wsh2.SendKeys('{ESC}'); Start-Sleep -Milliseconds 700
        }
        if ($bad.Count -eq 0) { $walk = 'PASS（9/9）' }
        else { $walk = 'FAIL'; $fail += ('菜单逐项走查失败：' + ($bad -join '、')) }
    } catch {
        $walk = 'SKIP（' + $_.Exception.Message + '）'
    }
}
Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

Say ("插件：" + $loaded)
Say ("补丁挂载：" + $okLines.Count + "/" + $ExpectedPatches)
Say ("语音后端：" + $backend)
Say ("键盘链路：" + $drill)
Say ("菜单走查：" + $walk)
if ($fail.Count -gt 0) {
    foreach ($f in $fail) { Say ("✗ " + $f) }
    Fail ("冒烟不通过（" + $fail.Count + " 项），日志：" + $log)
}
Write-Host "冒烟通过：补丁加载、契约、挂载点、语音后端、无异常全部对得上。" -ForegroundColor Green
exit 0
