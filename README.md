# MeiKongA11y

> 《妹控计划》的读屏无障碍补丁。
> 平台层与契约层来自 [A11yFramework](https://github.com/Nornageste-mh/Framework)。

## 说明

让读屏用户能把《妹控计划》玩下去。

本作有个和前三作都不一样的特点：**97.2% 的台词有配音**。所以这个补丁的重心**不在"念台词"**，
而在**操控** —— 让盲人知道自己在哪个界面、有哪些东西能操作、怎么和诗萌互动。

### 做

| 功能 | 默认键 | 说明 |
| --- | --- | --- |
| 朗读无配音台词 | — | 全作 1392 行台词里只有 39 行没有配音，这 39 行自动念 |
| 重读当前句 | `Backspace` | 有配音的行**唯一**的朗读通路（见下） |
| 选项播报 + 数字键选择 | `1`-`9` | 一次念完全部选项并编号；等价于用鼠标点那一个按钮 |
| 界面导航 | `Tab` | 上下选择、左右调滑条、回车激活、Home/End 首尾、PageUp/PageDown 换面板组 |
| 功能菜单 | `F1` | 念出「小游戏 / 番茄钟 / 日程表 / 设置 / 故事与收藏 / 换装」并编号，数字键直达 |
| 状态播报 | `F2` | 当前显示模式、诗萌在屏幕哪个方位、当前面板、可操作项数量、番茄钟剩余时间 |
| 戳一下诗萌 | `F3` | **等价于鼠标左键点她本人**（走游戏自己的 `FireClick()`，音效与番茄钟分支都一样） |
| 和诗萌聊聊 | `F4` | 按下当前可见的「和诗萌聊聊」按钮；不可见时退化为「戳一下诗萌」 |
| **切回全屏（保命键）** | `F5` | **任何时候按都能从桌宠模式切回全屏** —— 桌宠模式下游戏会把整个全屏界面 SetActive(false)，游戏自己的界面里没有退路 |
| 记忆翻牌网格导航 | 方向键 + 回车 | 4×4 牌阵上移动光标、翻开；行列按牌的真实屏幕位置算；翻开结果立刻播报 |

### 明确不做

- **不自动朗读有配音的行**。配音本身就是内容，读屏叠上去两边都听不清。
  需要确认文字时按重读键。想吃「全语音 + 全文本」的玩家可以在配置里打开「有配音的行也自动朗读」。
- **不播报「谁在说」**。本作的行数据里**没有说话人字段**（`DialogueLine` 只有 10 个字段），
  这是功能范围上的真实缩水，不粉饰。
- **不取消任何限时、不预选、不代替玩家做选择。**

### ★ 一条比"剧透闸门"更宽的设计纪律：**不许作弊**

无障碍层的语义是「把本来能看到、能操作的东西，用朗读与键盘**等价地**呈现」，
**不是**「顺手降低难度」。这条在前三作里叫「不许扩大可达范围」；
本作因为有一个**记忆翻牌小游戏**，需要把它说得更明确：

> **补丁给出的信息，不得超过正常人在同一时刻抬眼看屏幕所能获得的信息；
> 也不得替玩家承担游戏本身要考的能力（记忆、注意力、判断）。**

具体到记忆翻牌（16 张牌 / 8 对 / 对手是诗萌）：

| 层次 | 内容 | 理由 |
| --- | --- | --- |
| **给** | 牌阵坐标、当前光标所在牌的位置与状态、**这一回合已经翻开的牌是什么图案**、比分、轮到谁 | 正常人抬眼就能看到，给了不算作弊 |
| **不给** | 已经盖回去的牌是什么 | **这就是游戏本身**。正常人靠脑子记，盲人也得靠自己记 |
| **可选、默认关** | 一个「笔记」查询键 | 打开等于给自己发一本笔记，会降低难度。README 如实标注，不假装它不影响难度 |

顺带一个可量化的锚点：**游戏自己的 AI 也是有限记忆的**（`MemoryMatchAiSettings`：
最多记 12 张 / 每步 8% 遗忘 / 15% 看漏 / 10% 乱猜，只有最高难度档才是完美记忆）。
所以真要给"笔记"，对齐到这个默认档比给完美记录更站得住脚 —— 那样最多也只是跟诗萌一个水平。

## 安装（仅限正版玩家）

见 `mod/package/安装说明.md`。补丁不修改任何游戏文件，装法就是把 `package/` 里的东西
拷进有 `MeiKongProject.exe` 的那一层。

> ⚠ **不要把游戏装在含中文（或任何非 ASCII 字符）的路径下再用这个补丁。**
> BepInEx 5 + HarmonyX 在这种路径下打补丁会抛
> `IL Compile Error … String illegal byte sequence`，而且**部分补丁已经生效**，
> 症状是「有些功能莫名其妙不工作」。详见下面「几条踩过的坑」。

## 主要快捷键

见上面「做」那张表。**游戏原生占用的键**（补丁不去抢，也不要拿来当重读键）：
空格 / 回车 / 小键盘回车 / 滚轮 = 推进剧情，按住 Ctrl = 快进，R = 历史记录，
Esc / 右键 = 呼出或收起界面。

## 已知问题 / 注意事项

- **没有说话人播报**：行数据里没有这个字段（见上）。
- **部分界面本来就没有可读文本**：背景与氛围面板整个命名空间 0 个 TMP、CG 面板无 TMP、
  记忆翻牌的牌面是纯图片、节点图文字画在图上 —— 这些地方只能报「第几项 / 什么操作」，报不出内容。
- **存档槽与读档列表把章节标题截断到 20 字**，全文只在鼠标悬停时才写进 TMP。
- **全屏 247 个控件里通常只有 18 个此刻可用**，所以「只导航看得见的控件」不要关。

## 构建

```powershell
cd mod
.\build.ps1 -GameDir "D:\Steam\steamapps\common\<游戏目录>"
```

## 目录结构

```
mod/src/MeiKongA11y/
  A11yHost.cs      L2 契约层（只填值，不写逻辑）
  Speech/Nvda/Sapi/UiNav/KeyEdge.cs   L1 平台层（整份拷贝，不改）
  UiNav.Game.cs    平台层的**逐作区**（别名表 + 输入框角色）
  TextProc.cs      文本清洗（本作近乎空实现，理由见文件注释）
  Reader.cs        朗读中枢（哪一行、念不念、去重）
  Choices.cs       选项（抓取 / 播报 / 数字键）
  Surfaces.cs      交互层（面板感知 / 功能菜单 / 状态播报）
  Pet.cs           桌宠交互层（戳她 / 她在哪 / 互动按钮）
  Patches.cs       Harmony 补丁点
  Plugin.cs        BepInEx 入口 + 配置 + 每帧输入分发
probe/             实机探针（P1 证据来源）
```

## 模组做了什么

| 功能 | 挂载点 | 依据 |
| --- | --- | --- |
| 抓住当前台词 | `DialoguePlaybackTracker.NotifyLineChanged(DialogueLine, int)` | 四个分支都调它，是唯一的「当前行」真值来源 |
| 选项开始 | `DialoguePlaybackTracker.NotifyChoiceWaiting(int, int)` | 此时只有数量，没有文本 |
| 选项文本 + 回调 | `DialogueChoiceItemView.Bind(string, Action)` | 每个选项调一次；回调直接调用等价于点按钮 |
| 选项就绪 | `DialogueChoiceItemView.SetInteractable(bool)` | **每个选项各调一次**，补丁必须自己去重 |
| 段落起止 | `DialoguePlaybackTracker.NotifyScenarioStarted/Ended` | 清空上一句与选项缓存 |
| 戳诗萌 | 反射调用 `DialogueEntryClickTarget.FireClick()` | 与鼠标左键点她完全等价 |
| uGUI submit 通路 | `EventSystem.sendNavigationEvents` 每帧复位为 `false` | 游戏从不读选中态；视频面板会把它置回 true |

## 几条踩过的坑（改之前请先读）

1. **游戏目录含非 ASCII 字符 → Harmony 静默半失效**。
   `PatchAll` 抛 `IL Compile Error … Illegal byte sequence`（栈底是
   `MonoMod.Utils.MMReflectionImporter` → `Assembly.GetCodeBase()`），
   而**已经应用的补丁照常工作**，看起来像「随机某个功能不生效」。测试副本放在纯 ASCII 路径。
2. **Harmony 前缀/后缀的参数名必须与运行时一致，不是与反编译结果一致**（流水线 G2）。
   本作实例：`PhoneDialogueManager.AddMessage(string messageText, bool isLeft, PhoneDialogue dialogue)`
   的第一个参数叫 `messageText`，写成 `text` 会让整个补丁类 PatchAll 失败。
3. **同一行台词会被通知两次**，必须按 (ScenarioId, Index) 去重，否则每句都念两遍。
4. **`NotifyChoiceWaiting` 拿不到选项文本**，文本要等 `Bind`；而 `SetInteractable(true)`
   是**每个选项各调一次**，不去重会把选项播报 N 遍。
5. **A11yFramework 的 `tools/new_game.py` 漏拷 `platform/KeyEdge.cs`**：平台层直接编译不过；
   另外它的 csproj 模板注释里带连续两个减号，MSBuild 会以 MSB4025 拒载整个项目。
6. **Python 必须显式用 `D:\Python3.13\python.exe`**：PATH 上的 `python` 是坏的 uv trampoline。
7. **不要用 PowerShell 文本 cmdlet 改源码**（流水线 G4）；`.ps1` 必须 UTF-8 **带 BOM + CRLF**，
   否则 PowerShell 5.1 按 GBK 解码会直接把 here-string 解析成普通语句，脚本整份不执行。

## 如何复现分析

```powershell
# 1) 实机探针（只读观测；日志含控件标签但不含台词，不入库）
cd probe
.\run_probe.ps1 -WaitSeconds 60
.\run_probe.ps1 -Jump "story_3_教学时间" -Choose 1     # 自动跳转 + 自动选项
$env:MKPROBE_POKE_AT = '25'; .\run_probe.ps1 -WaitSeconds 55   # 自动戳一下诗萌

# 2) 反编译
ilspycmd -p -o decomp "<游戏目录>\MeiKongProject_Data\Managed\Assembly-CSharp.dll"
```

探针产物在游戏目录的 `BepInEx/probe_meikong.log` 与 `probe_scene_tree.txt`。
**`LogOutput.log` 含台词原文，绝不入库。**

## 合规说明

- 本仓库**不含任何游戏资源、不含剧本原文、不含反编译产物**。
- 补丁只在运行时挂载（BepInEx），**不修改游戏文件**，仅限正版玩家使用。
- 详细说明见 A11yFramework 的 README「IP 与合规」。

## 许可

见 `mod/licenses/`。
