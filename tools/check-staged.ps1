#Requires -Version 5.1
<#
    提交前安全闸门：
      1. 确认暂存区里没有任何游戏版权内容或第三方二进制；
      2. 确认没有把「临时提交信息文件」这类脚手架误提交进来；
      3. 确认暂存的 .ps1 都带 UTF-8 BOM。

    背景一：本仓库的分析产物（提取的剧本文本、反编译的游戏代码）属于游戏
    著作权人的资产，一旦提交并推送即构成再分发。这个检查曾抓到
    mod/verify/ 目录漏网（.gitignore 只写了根级 /verify/）。

    背景二：带中文和换行的提交信息塞进 PowerShell 命令行会被吃掉引号，
    所以本项目的习惯是写进临时文件再用 `git commit -F` 提交。
    但那个临时文件**不该写在仓库里**：本仓库的第一个提交里就混进了
    tmp_commit_msg.txt，第二个版本又混进了 tmp_msg.txt，各自还得再补一条
    「移除临时提交信息文件」的提交去擦屁股 —— 而发布标签恰好打在了那两条
    擦屁股的提交上，点开 tag 看到的是一句和版本毫无关系的说明。
    补救办法是把临时文件写到仓库外（或用 $env:TEMP），
    这里再加一道闸门兜住。

    背景三：Windows PowerShell 5.1 读取**无 BOM** 的 .ps1 时按系统 ANSI
    代码页（这里是 GBK）解码，中文注释会变乱码并直接导致语法错误，
    脚本整个跑不起来。而不少编辑器/工具保存 UTF-8 时不写 BOM ——
    mod/build.ps1 就这样坏过一次。所以在这里挡住。

    背景四（0.2.7.0 起，第 0 条纪律）：逐作仓不允许把「导航模式」当默认形态
    提交。这里机械检查三件事：判定文件存在且结论已填 A/B/C；Plugin 的
    CfgMenuNav 默认 false；README / 安装说明 / FAQ 不把 Tab 导航模式写成默认键。
    框架仓库本身没有 mod\src\，自动跳过这组检查。

    用法：
        .\tools\check-staged.ps1          # 检查
        git commit ...                    # 通过后再提交
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # core.quotepath=false：否则 git 会把中文文件名输出成 \345\215\270 这样的
    # 八进制转义，反斜杠在 Windows 路径里非法，GetExtension 会直接抛异常
    $staged = @(git -c core.quotepath=false diff --cached --name-only)
    if ($staged.Count -eq 0) {
        Write-Host "暂存区为空，没有要检查的内容。" -ForegroundColor Yellow
        exit 0
    }

    # 禁止出现的路径片段
    $forbidden = @(
        'extracted/',
        'decompiled/',
        'verify/',
        'textassets/',
        'bep_dl/',
        'nvda_dl/',
        'addressable_ids.txt'
    )
    # 禁止出现的文件扩展名
    $forbiddenExt = @('.dll', '.zip', '.exe', '.pdb')

    # 脚手架文件名（只匹配纯文件名，避免误伤正常文件）
    $scratchNames = @(
        'tmp_commit_msg.txt', 'tmp_msg.txt', 'commit_msg.txt',
        'msg.txt', '_review_diff.txt'
    )

    $violations = @()
    $scratch = @()
    foreach ($f in $staged) {
        foreach ($bad in $forbidden) {
            if ($f -like "*$bad*") { $violations += "$f   (命中禁用路径: $bad)"; break }
        }
        try { $ext = [System.IO.Path]::GetExtension($f).ToLower() } catch { $ext = "" }
        if ($forbiddenExt -contains $ext) { $violations += "$f   (禁用扩展名: $ext)" }

        try { $leaf = [System.IO.Path]::GetFileName($f).ToLower() } catch { $leaf = "" }
        if ($scratchNames -contains $leaf) { $scratch += $f }
    }

    # .ps1 必须带 UTF-8 BOM，否则 PowerShell 5.1 按 GBK 读，中文直接炸
    # 注意：这里必须用 Join-Path 拼成绝对路径。git 给的是相对仓库根的路径，
    # 而 [System.IO.File] 系列用的是 **.NET 进程当前目录**，Push-Location
    # 只改 PowerShell 的位置，两者不是一回事 —— 直接用相对路径会去找会话
    # 工作目录下的同名文件，报 Could not find a part of the path。
    $noBom = @()
    foreach ($f in $staged) {
        if ([System.IO.Path]::GetExtension($f).ToLower() -ne '.ps1') { continue }
        $abs = Join-Path $root $f
        if (-not (Test-Path -LiteralPath $abs)) { continue }   # 已删除的文件跳过
        $bytes = [System.IO.File]::ReadAllBytes($abs)
        $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        if (-not $hasBom) { $noBom += $f }
    }

    # ================================================================
    # 第 0 条纪律（0.2.7.0）：逐作仓的默认交互形态守卫。
    # 框架仓库没有 mod\src\，跳过；生成出来的逐作仓必须有判定文件且默认干净。
    # ================================================================
    $interaction = @()
    if (Test-Path (Join-Path $root 'mod\src')) {
        $judge = Join-Path $root 'docs\交互形态判定.md'
        if (-not (Test-Path -LiteralPath $judge)) {
            $interaction += '缺少 docs\交互形态判定.md（P0 必填；模板见 docs\templates\交互形态判定.md）'
        } else {
            $jt = Get-Content -LiteralPath $judge -Raw -Encoding UTF8
            if ($jt -notmatch '\*\*结论\*\*：\s*[ABC]') {
                $interaction += 'docs\交互形态判定.md 的「**结论**：」未填 A/B/C —— 未判定不许合并'
            }
        }

        # CfgMenuNav 必须是默认 false；A 类作品整组删掉也可以，但只要还留着这项，
        # 默认值就不能是 true。
        $plugins = @(Get-ChildItem -Path (Join-Path $root 'mod\src') -Recurse -Filter 'Plugin.cs' -File -ErrorAction SilentlyContinue)
        foreach ($p in $plugins) {
            $pt = Get-Content -LiteralPath $p.FullName -Raw -Encoding UTF8
            $hasNavEntry = ($pt -match 'CfgMenuNav|菜单键盘导航')
            $navOff = ($pt -match '"菜单键盘导航（遗留）"\s*,\s*false')
            if ($hasNavEntry -and -not $navOff) {
                $interaction += 'Plugin.cs 的 CfgMenuNav 不是默认 false（第 0 条纪律：新作默认不得启用导航模式；A 类可整组删掉）'
            }
        }

        # README / 安装说明 / FAQ 不得把 Tab 导航模式写成默认快捷键。
        # 允许「遗留 / 不提供 / 不得 / 历史」这类说明句 —— 卡的只是默认口径。
        foreach ($rel in @('README.md', 'mod\package\安装说明.md', 'mod\package\常见问题.md')) {
            $abs = Join-Path $root $rel
            if (-not (Test-Path -LiteralPath $abs)) { continue }
            $lines = @(Get-Content -LiteralPath $abs -Encoding UTF8)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                if ($line -match 'Tab' -and $line -match '导航模式' -and
                    $line -match '进入|退出' -and
                    $line -notmatch '遗留|不提供|不得|历史|老作') {
                    $interaction += "${rel}:$($i + 1) 仍把「Tab 进入/退出导航模式」当默认快捷键"
                }
            }
        }
    }

    Write-Host "暂存文件数: $($staged.Count)" -ForegroundColor Gray
    if ($violations.Count -gt 0) {
        Write-Host "`n禁止提交以下内容：" -ForegroundColor Red
        $violations | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "`n这些是游戏版权内容或第三方二进制，入库并推送等于再分发。" -ForegroundColor Red
        Write-Host "请修正 .gitignore 后重试。" -ForegroundColor Red
        exit 1
    }

    if ($scratch.Count -gt 0) {
        Write-Host "`n暂存区里有脚手架文件：" -ForegroundColor Red
        $scratch | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "`n提交信息用的临时文件不该写在仓库里 —— 它会被提交进去，" -ForegroundColor Red
        Write-Host "然后还得再补一条提交把它删掉，发布标签很容易打在那条上。" -ForegroundColor Red
        Write-Host "改写到仓库外或 `$env:TEMP：git commit -F `"`$env:TEMP\msg.txt`"" -ForegroundColor DarkGray
        exit 1
    }

    if ($noBom.Count -gt 0) {
        Write-Host "`n以下 .ps1 缺少 UTF-8 BOM：" -ForegroundColor Red
        $noBom | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "`nWindows PowerShell 5.1 会把无 BOM 的文件按 GBK 解码，" -ForegroundColor Red
        Write-Host "中文注释变乱码并直接语法错误。补 BOM：" -ForegroundColor Red
        Write-Host '  $b=[IO.File]::ReadAllBytes($f); [IO.File]::WriteAllBytes($f, [byte[]](0xEF,0xBB,0xBF)+$b)' -ForegroundColor DarkGray
        exit 1
    }

    if ($interaction.Count -gt 0) {
        Write-Host "`n第 0 条纪律（交互形态）检查未通过：" -ForegroundColor Red
        $interaction | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        Write-Host "`n补丁要像游戏本身就有无障碍，不是像外挂。" -ForegroundColor Red
        Write-Host "先填 docs\交互形态判定.md，再把默认形态改成游戏自己的键盘路径。" -ForegroundColor Red
        exit 1
    }

    Write-Host "通过：没有版权内容、没有二进制、没有脚手架文件，.ps1 的 BOM 也都在。" -ForegroundColor Green
    if (Test-Path (Join-Path $root 'mod\src')) {
        Write-Host "第 0 条纪律：判定文件已填、默认未启用导航模式、文档无补丁专属模式键。" -ForegroundColor Green
    }
    exit 0
} finally { Pop-Location }
