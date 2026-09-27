#Requires -Version 5.1
<#
    静态断言：**重读缓冲区只允许有一个出口。**

    《钟塔》0.1.0.1 与《妹控计划》0.1.0.0 踩的是同一个坑：
    逐作层绕过重读缓冲区直接播报，于是「有选项时按退格念不出选项」。

    0.1.1.0 起缓冲区提到 L1（platform\Repeat.cs），逐作层只能通过它说话。
    这个脚本机械保证这件事。

    用法：
        .\lint_repeat.ps1 -Path ..\mod\src\MyGameA11y
        .\lint_repeat.ps1 -Path ..\platform -AllowFile Repeat.cs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$AllowFile = ""      # 允许出现 Speech.Speak 的文件名（平台层只放行 Repeat.cs）
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $Path)) { Write-Host "路径不存在: $Path" -ForegroundColor Red; exit 1 }

$bad = @()
Get-ChildItem -LiteralPath $Path -Recurse -File -Filter *.cs | ForEach-Object {
    if ($AllowFile -and $_.Name -eq $AllowFile) { return }
    # 去掉行注释再找，免得把注释里提到的 Speech.Speak 当成违规
    $n = 0
    foreach ($line in (Get-Content -LiteralPath $_.FullName -Encoding UTF8)) {
        $n++
        $code = $line -replace '//.*$', ''
        if ($code -match 'Speech\.Speak\s*\(') { $bad += ("{0}:{1}: {2}" -f $_.Name, $n, $line.Trim()) }
    }
}

if ($bad.Count -gt 0) {
    Write-Host "FAIL  有 $($bad.Count) 处绕过了重读缓冲区（Speech.Speak 必须改成 Repeat.Say / Note / Push）：" -ForegroundColor Red
    $bad | Select-Object -First 20 | ForEach-Object { Write-Host "        $_" }
    Write-Host "      逐作层直接播音 = 玩家按重读键听不到它 —— 这个坑已经踩过两次。" -ForegroundColor Yellow
    exit 1
}
Write-Host "  ok  重读缓冲区是唯一出口（$Path）" -ForegroundColor Green
exit 0
