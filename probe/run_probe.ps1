#Requires -Version 5.1
<#
    P1 实机探针：部署 -> 启动 -> 收日志 -> 收场景树
    用法：
        .\run_probe.ps1
        .\run_probe.ps1 -WaitSeconds 90 -Jump story_3_教学时间 -Choose 1
    产出（都在游戏目录的 BepInEx 下）：
        probe_meikong.log       快照 / 挂钩事件 / 补丁点存在性（含控件标签，不含台词原文）
        probe_scene_tree.txt    运行时场景树（同上，不入库）
        LogOutput.log           BepInEx 日志（**含台词原文，绝不入库**）

    为什么不做按键注入：本会话抓不到游戏窗口前台，keybd_event 不可靠（前作实测结论）。
    要推进剧情请用 -Jump（探针内部直接调 DialogueV2Runner.StartScenario）。
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\mk-test\game',
    [string]$ProbeDir = 'D:\Harness工作区\meikong-a11y\probe',
    [int]$WaitSeconds = 75,
    [string]$Jump = '',
    [int]$JumpAt = 30,
    [int]$Choose = 0,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $GameDir 'MeiKongProject.exe'
$probe = Join-Path $GameDir 'BepInEx\probe_meikong.log'
$tree = Join-Path $GameDir 'BepInEx\probe_scene_tree.txt'
$beplog = Join-Path $GameDir 'BepInEx\LogOutput.log'

if (-not (Test-Path -LiteralPath $exe)) { Write-Host "找不到游戏：$exe" -ForegroundColor Red; exit 1 }

Write-Host "=== 1/5 停掉正在跑的实例 ===" -ForegroundColor Cyan
Get-Process -Name 'MeiKongProject' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "=== 2/5 部署探针 ===" -ForegroundColor Cyan
$dll = Join-Path $ProbeDir 'plugin\bin\Release\MeiKongA11yProbe.dll'
if (-not (Test-Path -LiteralPath $dll)) { Write-Host "探针没编译：$dll" -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\plugins') | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $GameDir 'BepInEx\plugins') -Force
Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $tree -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $beplog -ErrorAction SilentlyContinue
Write-Host ("    " + (Split-Path $dll -Leaf))

Write-Host "=== 3/5 启动游戏（SteamAppId=4169160） ===" -ForegroundColor Cyan
$env:SteamAppId = '4169160'
$env:SteamGameId = '4169160'
$env:MKPROBE_JUMP = $Jump
$env:MKPROBE_JUMP_AT = "$JumpAt"
$env:MKPROBE_CHOOSE = "$Choose"
if ($Jump) { Write-Host ("    自动跳转 = " + $Jump + " @" + $JumpAt + "s  自动选项 = " + $Choose) }
$p = Start-Process -FilePath $exe -WorkingDirectory $GameDir -PassThru
Write-Host ("    PID = " + $p.Id)

Write-Host "=== 4/5 等待 $WaitSeconds 秒 ===" -ForegroundColor Cyan
$elapsed = 0
while ($elapsed -lt $WaitSeconds) {
    Start-Sleep -Seconds 5
    $elapsed += 5
    if ($p.HasExited) { Write-Host "    进程已退出（exit=$($p.ExitCode)）" -ForegroundColor Yellow; break }
    Write-Host ("    t+" + $elapsed + "s alive")
}

if (-not $KeepRunning) {
    Get-Process -Name 'MeiKongProject' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host ""
Write-Host "---- LogOutput.log（前 22 行）----"
Get-Content -LiteralPath $beplog -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -First 22
Write-Host ""
$n = 0; if (Test-Path -LiteralPath $probe) { $n = (Get-Content -LiteralPath $probe).Count }
$tn = 0; if (Test-Path -LiteralPath $tree) { $tn = (Get-Content -LiteralPath $tree).Count }
Write-Host "---- probe_meikong.log（$n 行）定位：$probe ----"
Write-Host "---- probe_scene_tree.txt（$tn 行）定位：$tree ----"
Write-Host ""
Write-Host "提示：探针日志含控件标签但不含台词；LogOutput.log 含台词原文，**不要提交**。" -ForegroundColor Yellow
