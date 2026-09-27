# MeiKongForAccessibility

本仓库是**专门针对 Steam 游戏《妹控计划》（MeiKongProject，AppID 4169160）** 的屏幕阅读器
辅助补丁 —— 一个游戏内 BepInEx 5 插件，通过 Tolk / NVDA Controller Client /
Windows SAPI 朗读屏幕上的文字。

> ## 🎮 关于本作
>
> | | |
> |---|---|
> | 中文名 | **妹控计划** |
> | 英文名 / 工程名 | MeiKongProject |
> | 开发 / 发行 | **可味玩KawayiPlay** |
> | Steam | [AppID 4169160](https://store.steampowered.com/app/4169160/) |
> | 引擎 | Unity 2022.3.43f1c1，**Mono** 后端 |
> | 类型 | **桌面陪伴型**游戏（桌宠 + 主线剧情 + 日程 / 番茄钟 / 音乐 / 换装等工具） |
> | 主要角色 | 妹妹「诗萌」 |
>
> 本作和同开发商的前两作（《钟塔》《透明的她与真实的我》）**共用一套引擎与工程底子** ——
> 这一点是从游戏文件本身看出来的（`app.info` 里的开发商字段、残留的前作剧本表）。
>
> 台词 **1392 行里有 1353 行（97.2%）有配音**，与《钟塔》的 39.1% 正好相反。
> 所以这个补丁的重点**不是「念台词」，而是「能操作」** ——
> 让读屏用户知道自己在哪个界面、有哪些东西能按、怎么和诗萌互动。
>
> **这个补丁是玩家自制的第三方工具，与可味玩KawayiPlay 没有任何关系，
> 也没有得到他们的授权或背书。** 游戏的著作权归他们所有，
> 本仓库只发布自己的补丁代码，不含任何游戏资源。
>
> 脚注（哪些是本仓库亲自核实的）：
>
> - **开发商 / 引擎 / 后端**：本机核实 —— `UnityPlayer.dll` 的版本信息、
>   `MeiKongProject_Data/app.info`、`Managed/` 下的托管程序集
> - **剧本规模与配音覆盖**：本机从游戏资源统计（12 张表 1969 行）
> - **发售日期、售价、商店特性**：**本仓库未核实**（Steam 商店页在本项目的开发环境里访问不到），
>   请以商店页为准

---

> ## 致 可味玩KawayiPlay
>
> 我们欠你们一句道歉。
>
> 做这个补丁的过程中，我们反编译了你们的游戏（`Assembly-CSharp.dll`）、
> 从资源里把剧本表提取了出来 —— **这些都没有事先问过你们。**
>
> 技术上这是绕不开的一步：这个游戏的界面里大量控件是**纯图片按钮**
> （实测 173 个可交互控件里 131 个读不出任何文字），不反编译就找不到能挂钩子的地方。
> 但**「必要」不等于「可以不打招呼」**。
> 我们做这件事的出发点不是不尊重这部作品，恰恰相反 ——
> 是因为觉得它值得被更多人玩到，才想让读屏玩家也能走完一遍。
>
> 已经做的补救：
>
> - 提取的剧本与反编译的代码**从未进入版本库**（全历史核查过），
>   只在本机用于分析，已被 `.gitignore` 排除，也**不随发布包分发**
> - 证据目录 `probe/evidence/` 只收录**不含剧本文本**的材料；
>   含台词的会话日志与游戏截图故意不入库
> - 仓库与发布包**不含任何游戏资源**，只有我们自己的补丁代码和文档
> - 发给玩家的文档里没有任何剧情内容，也没有泄露结局相关的信息
> - **如果你们认为任何部分不妥，请联系我们 —— 我们会立刻调整，或者整体下架**
>
> 也请读到这里、并且用得上这个补丁的玩家：
> **它让你能玩上这款游戏，不是让你不必买这款游戏。请去买一份正版。**
> 这部作品是可味玩KawayiPlay 的。

> ## ⚠️ 郑重警告（请务必阅读）
>
> - 本项目是 **Vibe coding 产物**（AI 辅助生成），**非官方**，
>   与开发兼发行 **可味玩KawayiPlay** **没有任何关系**。
> - **请务必支持正版：本辅助仅面向已在 Steam 购买《妹控计划》的玩家。**
>   **强烈要求每一位使用者通过 Steam 购买正版游戏。** 我们坚决反对任何形式的盗版、
>   破解、未授权传播；请勿将本工具用于协助获取或游玩盗版副本。
>   **这个补丁是让你能玩上这款游戏，不是让你不必买它** ——
>   可味玩KawayiPlay 的劳动成果值得被正当地支持。
> - 本项目**不保证可用、不保证稳定**，代码可能存在各种问题（兼容性、稳定性、安全性等），
>   **无任何维护承诺**。使用风险自负，仅供个人学习/研究参考，请勿用于商业或分发牟利。
> - **建议在完全理解代码的前提下再使用**，自行承担一切后果。
> - 本仓库**不包含任何游戏资源**，只发布运行时补丁代码与文档。
> - **如您是 可味玩KawayiPlay 的成员**，或本作的版权方，认为本仓库有任何不妥，
>   请联系我们，我们会立即配合调整或移除。

---

> ## ⚠️ 一个和补丁有关、但**不是补丁造成**的坑：桌宠模式
>
> 桌宠模式下，游戏会把**整个全屏界面（功能条、所有面板、设置）整个 `SetActive(false)`** ——
> 只剩一只贴在桌面上的诗萌。我们实机测到可交互控件从 **18 个掉到 0 个**。
> 那时游戏自己的界面里**没有退路**（右键菜单在当前版本是空的，悬停条需要鼠标）。
>
> 补丁为此加了一个保命键：**`F5`，任何时候按都能切回全屏**，
> 并且会在你刚进入桌宠模式时就提醒一次。
>
> 但这只是**我们的补救**，不是游戏的问题，也不是我们能替游戏决定的事。
> 如果你因此受到困扰，那是我们考虑得不够周全的一部分 —— 请反馈。

---

## 说明

本辅助的作用：让使用读屏软件的玩家，也能基本正常地游玩《妹控计划》。

- 朗读**没有配音**的剧情文本（全作 1392 行台词里只有 39 行没有配音）；
  **有配音的台词只放语音、不朗读**，避免两路声音打架 —— 想确认文字按 `Backspace` 重读
- 朗读**选项**，并用数字键 `1`-`9` 选择
- 界面控件的键盘导航与朗读（游戏原本这些界面几乎只能鼠标点）
- 功能菜单（`F1`）与状态播报（`F2`）：念出六个功能入口并编号、报当前面板与可操作项
- 桌宠交互：`F3` 戳一下诗萌、`F4` 和诗萌聊聊 —— 都走游戏自己的入口，等价于鼠标操作
- **记忆翻牌**小游戏的网格导航（方向键在 4×4 牌阵上走，回车翻牌）

游戏版本：Unity 2022.3.43f1c1，**Mono** 后端。面向**通过 Steam 购买的正式版**
（AppID 4169160）。

> **游戏所在路径不能含中文/非 ASCII 字符。** 这不是本补丁的怪癖：
> BepInEx + HarmonyX 在那种路径下打补丁会抛 `IL Compile Error … Illegal byte sequence`，
> 而且**部分补丁已经生效**，症状是「有些功能莫名其妙不工作」。
> 详见下面「几条踩过的坑」。

---

## 安装（仅限正版玩家）

**最快的装法**：到 [Releases](../../releases) 下载 `MeiKongA11y-<版本>.zip`，
解压后把里面的东西**整体**拷进游戏根目录（有 `MeiKongProject.exe` 的那一层），
提示「是否合并/替换」时选**是**。

**没有安装程序** —— 补丁由 BepInEx 在运行时挂载，不修改任何游戏文件，
所以安装就是拷文件。zip 内容就是下文的 `mod\package\`，
其中 `licenses\` 是第三方组件的许可证与声明，**别删**。

三条容易踩的坑：

- `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version` 必须在**游戏根目录**
- `BepInEx\core\` 里的文件**一个都不能少**
- `nvdaControllerClient.dll` 放游戏根目录（Mono 查找 DLL 时先看应用目录）

如果已经装过别的 BepInEx 模组，只需要两个文件：
`MeiKongA11y.dll` → `BepInEx\plugins\`，`nvdaControllerClient.dll` → 游戏根目录。
**不要**覆盖对方已有的 `winhttp.dll` 和 `BepInEx\core\`。

卸载：删掉游戏目录下的 `winhttp.dll` 即完全失效；连同 `BepInEx\` 一并删掉就彻底干净。
存档在 `%USERPROFILE%\AppData\LocalLow\Kawayi Play\MeiKongProject\`，不受影响。

> 详细步骤、按键表、每个配置项的含义见
> [`mod/package/安装说明.md`](mod/package/安装说明.md)；
> 遇到疑问先翻 [`mod/package/常见问题.md`](mod/package/常见问题.md)。
>
> 再次提醒：请通过 Steam 购买正版《妹控计划》后再使用本辅助。

---

## 主要快捷键

| 快捷键 | 功能 |
| --- | --- |
| `F1` | 功能菜单 —— 念出六个功能入口并编号，数字键直达 |
| `F2` | 状态播报 —— 显示模式、诗萌在屏幕哪个方位、当前面板、可操作项数量 |
| `F3` | 戳一下诗萌（等价于鼠标左键点她本人） |
| `F4` | 和诗萌聊聊（按下当前可见的「和诗萌聊聊」按钮） |
| `F5` | **切回全屏（保命键）** —— 见上面「桌宠模式」那一段 |
| `1` - `9` | 选择对应编号的选项 |
| `Backspace` | 重读最近那一句；**有选项时重读选项** |
| `Tab` | 进入 / 退出界面导航模式 |
| `↑` `↓` | 上一项 / 下一项 |
| `←` `→` | 调整滑条；记忆翻牌里是在 4×4 牌阵上移动 |
| `回车` / `空格` | 导航模式下激活控件；记忆翻牌里是翻开当前这张 |
| `Home` / `End` | 第一项 / 最后一项 |
| `PageUp` / `PageDown` | 切换面板组 |

**游戏原生占用的键**（模组不会去抢，也不要拿来当重读键）：
空格 / 回车 / 小键盘回车 / 鼠标滚轮 = 推进剧情，按住 `Ctrl` = 快进，
`R` = 历史记录，`Esc` / 鼠标右键 = 呼出或收起界面。

后五个模组自用的键都可以在配置里改或关掉。

---

## 已知问题 / 注意事项

- 通过读取游戏运行时信息工作，**游戏更新后可能失效**（补丁点是按游戏内方法名与类型挂的）
- **不播报「谁在说」** —— 游戏的行数据里**没有说话人字段**（`DialogueLine` 只有 10 个字段）。
  这是功能范围上的真实缩水，不是遗漏
- 游戏把**大量界面控件画成了图片**（功能条图标、换装卡片、通用关闭按钮），
  TMP 里一个字都没有。模组按控件路径与游戏自己的字段做了中文映射，
  见 `UiNav.Game.cs`，每条的依据都写在注释里
- 部分界面**本来就没有可读文本**：背景与氛围面板 0 个 TMP、CG 面板无 TMP、
  记忆翻牌的牌面是纯图片、节点图文字画在图上 —— 这些位置只能报「第几项 / 什么操作」，
  报不出内容
- 存档槽与读档列表把章节标题**截断到 20 字**，全文只在鼠标悬停时才写进 TMP
- **未覆盖所有界面与交互**，下列内容还没逐项打磨：系统设置、
  日程表、番茄钟的计时播报、故事与收藏 / 音乐 / 饭点 / 表情包工坊的面板级细节
- 语音朗读依赖所选后端（Tolk / NVDA / SAPI），中文需要中文语音（系统语音里要有中文嗓音）
- **主线 50 章只跑过开头几章**，联动内容与按日期/进度触发的台词都没有实机确认过

---

## 构建

```powershell
cd mod
.\build.ps1
```

会自动下载 BepInEx 5.4.23.5 (win x64) 与 NVDA Controller Client (x64)，
编译插件并组装 `mod\package\`。第三方二进制不入库，全靠这个脚本复现。
需要 .NET SDK（本项目用 10.0.301 验证过）。

> `build.ps1` 与 `tools\check-staged.ps1` 是 `.ps1`，必须保存为
> **UTF-8 带 BOM** —— Windows PowerShell 5.1 读无 BOM 的脚本会按 GBK 解码，
> 中文注释变乱码并直接语法错误。提交闸门会拦住这种情况。

### 目录结构

```
.
├─ mod/
│  ├─ build.ps1                 下依赖 → 编译 → 组包
│  ├─ src/MeiKongA11y/
│  │  ├─ Plugin.cs              BepInEx 入口 + 配置 + 每帧输入分发
│  │  ├─ Speech.cs              Tolk / NVDA / SAPI 后端调度
│  │  ├─ Nvda.cs                NVDA Controller Client 封装
│  │  ├─ Sapi.cs                Windows 内置语音（纯 P/Invoke 直调 ISpVoice）
│  │  ├─ UiNav.cs               界面导航引擎（来自 A11yFramework 的平台层）
│  │  ├─ UiNav.Game.cs          平台层的**逐作区**：标签解析 / 组名 / 组序
│  │  ├─ A11yHost.cs            契约层：平台层唯一看得见的宿主
│  │  ├─ Reader.cs              朗读中枢（念不念、去重）
│  │  ├─ Choices.cs             选项抓取与数字键
│  │  ├─ Surfaces.cs            面板感知 / 功能菜单 / 状态播报
│  │  ├─ Pet.cs                 桌宠交互（戳她 / 她在哪 / 保命键）
│  │  ├─ Minigame.cs            记忆翻牌网格导航
│  │  ├─ Announcer.cs           播报调度（别和配音抢话）
│  │  ├─ TextProc.cs            文本清洗（本作近乎空实现）
│  │  └─ Patches.cs             Harmony 补丁点
│  └─ package/                  组装出来的安装包（不入库）
├─ probe/                       实机探针（P1 证据来源，不入库产物）
├─ docs/                        设计依据
└─ tools/                       提交前守卫
```

平台层（`Speech` / `Nvda` / `Sapi` / `UiNav` / `KeyEdge` / `A11yHost`）来自
[A11yFramework](https://github.com/Nornageste-mh/Framework)，**整份拷贝、不逐作改写**。
逐作只动 `UiNav.Game.cs` 与其余几个文件。

### 模组做了什么

| 功能 | 挂载点 | 依据 |
| --- | --- | --- |
| 抓住当前台词 | `DialoguePlaybackTracker.NotifyLineChanged(DialogueLine, int)` | 四个分支都调它，是唯一的「当前行」真值来源 |
| 选项开始 | `DialoguePlaybackTracker.NotifyChoiceWaiting(int, int)` | 此时只有数量，没有文本 |
| 选项文本 + 回调 | `DialogueChoiceItemView.Bind(string, Action)` | 每个选项调一次 |
| 选项就绪 | `DialogueChoiceItemView.SetInteractable(bool)` | **每个选项各调一次**，补丁必须自己去重 |
| 段落起止 | `DialoguePlaybackTracker.NotifyScenarioStarted/Ended` | 清空上一句与选项缓存 |
| 戳诗萌 | 反射调用 `DialogueEntryClickTarget.FireClick()` | 与鼠标左键点她完全等价 |
| 切回全屏 | `PetDisplayModeController.SetFullScreenMode()` | 就是设置面板里那个「全屏」勾选框背后调的东西 |
| 翻牌 | `MemoryMatchGameController.OnCardClicked(int)` | 等价于用鼠标点那张牌 |
| uGUI submit 通路 | `EventSystem.sendNavigationEvents` 每帧复位为 `false` | 游戏从不读选中态；视频面板会把它置回 true |

### 几条踩过的坑（改之前请先读）

1. **游戏目录含非 ASCII 字符 → Harmony 静默半失效**。
   `PatchAll` 抛 `IL Compile Error … Illegal byte sequence`，
   而**已经应用的补丁照常工作**，看起来像「随机某个功能不生效」。测试副本请放在纯 ASCII 路径。
2. **Harmony 前缀/后缀的参数名必须与运行时一致**，不是与反编译结果一致。
   本作实例：`PhoneDialogueManager.AddMessage` 的第一个参数运行时叫 `messageText`，
   写成 `text` 会让整个补丁类 PatchAll 失败。
3. **同一行台词会被通知两次**，必须按 `(ScenarioId, Index)` 去重。
4. **`NotifyChoiceWaiting` 拿不到选项文本**，文本要等 `Bind`；
   而 `SetInteractable(true)` 是**每个选项各调一次**，不去重会把选项播报 N 遍。
5. **GitHub 的 `.gitignore` 规则后面不能跟行内注释**（只在行首认 `#`），
   否则整条规则**静默失效** —— 本项目发布时因此把第三方二进制提进了库，靠提交闸门拦下。
6. **不要用 PowerShell 文本 cmdlet 改源码**；`.ps1` 必须 UTF-8 **带 BOM + CRLF**。

---

## 如何复现分析

```powershell
# 1) 实机探针（只读观测；日志含控件标签但不含台词，不入库）
cd probe
.\run_probe.ps1 -WaitSeconds 60
.\run_probe.ps1 -Jump "story_3_教学时间" -Choose 1          # 自动跳转 + 自动选项
$env:MKPROBE_POKE_AT = '25'; .\run_probe.ps1 -WaitSeconds 55   # 自动戳一下诗萌

# 2) 反编译
ilspycmd -p -o decomp "<游戏目录>\MeiKongProject_Data\Managed\Assembly-CSharp.dll"
```

探针产物在游戏目录的 `BepInEx/probe_meikong.log` 与 `probe_scene_tree.txt`。
**`LogOutput.log` 含台词原文，绝不入库。**

分析结论与出处见
[`无障碍可行性验证.md`](无障碍可行性验证.md)（P0 阶段全部事实与依据）。

---

## 合规说明

- 本仓库**不含任何游戏资源、不含剧本原文、不含反编译产物**。
- 补丁只在运行时挂载（BepInEx），**不修改游戏文件**，仅限正版玩家使用。
- 详细说明见 A11yFramework 的 README「IP 与合规」。

## 许可

见 `mod/licenses/`。

## 致谢

- 平台层来自 [A11yFramework](https://github.com/Nornageste-mh/Framework)，
  它是从《钟塔》《透明的她与真实的我》《不/存在的你，和我》三个已发布补丁里
  逆向固化出来的。
- 语音后端：[Tolk](https://github.com/ndarilek/tolk)（BSD）、
  [NVDA Controller Client](https://www.nvaccess.org/)（LGPL-2.1，NV Access）、
  Windows SAPI。
- 挂载框架：[BepInEx](https://github.com/BepInEx/BepInEx)（LGPL-2.1）、
  [HarmonyX](https://github.com/BepInEx/HarmonyX)（MIT）、
  [UnityDoorstop](https://github.com/Neighhiola/UnityDoorstop)（LGPL-2.1）。
