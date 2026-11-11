using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// L2 契约层 —— 平台层（L1: Speech / Nvda / Sapi / UiNav / KeyEdge）**唯一允许引用的宿主**。
    ///
    /// === 为什么要有这一层（《无障碍补丁流水线》§2.2）===
    ///
    /// 三个已发布仓库是同一套代码的三次「复制—改名—局部改写」。平台层逐字节相同，
    /// 但平台层当初直接读 Plugin.xxx，于是每一作都要把 63 / 117 / 64 处
    /// 引用重新改一遍名字 —— 这是三作里**唯一真实的复用摩擦**，
    /// 而且改错一处就要靠人工回归才发现。
    ///
    /// A11yHost 把这条接口固化下来：名字固定、语义固定，**每作一份，只填值，不写逻辑**。
    /// 填完之后，L1 逐字节复用，一作都不用再改。
    ///
    /// === 逐作怎么用 ===
    ///
    /// 在 Plugin.Awake 里按这个顺序填（顺序有讲究，见每一步的注释）：
    ///
    ///     A11yHost.Log      = Logger;                       // 1. 先给日志出口
    ///     A11yHost.CfgXxx   = Config.Bind(...);             // 2. 再绑共享层要用的配置
    ///     A11yHost.DiagOn   = false;                        // 3. 诊断总开关（可选）
    ///     A11yHost.BlockGameAdvance = () =&gt; UiNav.BlockGameAdvance;   // 4. 钩子
    ///     A11yHost.TickUiNav        = () =&gt; { ... };                   // 5. 每帧入口
    ///     A11yHost.Validate();                              // 6. 自检，漏填会写日志
    ///
    /// === 允许放什么 ===
    ///
    /// 只允许放**平台层真的会用到的**东西。判断标准：
    /// 「把这一项删掉，L1 里会不会有编译错误？」不会，就不该放这里 ——
    /// 那说明它是逐作层自己的配置，放在 Plugin.cs 里就行。
    ///
    /// 反过来说，L1 **绝不允许**出现的东西（一旦出现就丧失复用性）：
    ///   · 任何游戏类型（DialogueScene / Naninovel.* / PhoneDialogueManager …）
    ///   · 任何逐作配置项（CfgReadUnvoiced / CfgSilenceKey / CfgChoiceHotkeys …）
    ///   · Il2CppInterop / Il2CppSystem（IL2CPP 路线的特化处理留在逐作层）
    ///
    /// 这一条由 tools/compile_check.ps1 机械验证：它编译 platform\ 时
    /// **不引用 Assembly-CSharp** —— 编得过，就说明平台层确实没有游戏类型依赖。
    ///
    /// === 第 0 条纪律（docs/交互形态纪律.md）===
    ///
    /// 本契约里与 UiNav 有关的字段（CfgVisibleOnly / CfgSortByPosition /
    /// CfgExcludeNames / CfgQuitConfirm / CfgQuitNames / BlockGameAdvance /
    /// TickUiNav / ShouldMuteUnitySubmit / NavTogglePressed）**全部是 0.2.x 遗留
    /// 导航模式用的**：
    ///   · 新作默认**不启用**（Plugin 模板里 CfgMenuNav 默认 false）；
    ///   · 界面的键盘操作按 docs/交互形态判定.md 判定的 A / B / C 路线实现 ——
    ///     A 类只朗读；B / C 类补在**游戏自己的**输入 / 焦点 / 动作函数上；
    ///   · 任何情况下都**不许**新增「进入 / 退出导航模式」这类补丁专属状态键。
    /// 保留这些字段只为让已发布老作品逐字节不变 —— **保留不等于推荐**。
    /// </summary>
    internal static class A11yHost
    {
        // ====================================================================
        // 日志（共享层唯一日志出口）
        // ====================================================================

        /// <summary>共享层的日志出口。由 Plugin.Awake 里的 <c>A11yHost.Log = Logger;</c> 填入。</summary>
        internal static ManualLogSource Log;

        /// <summary>
        /// 「界面诊断日志」总开关。
        ///
        /// 与 <see cref="CfgDiagLog"/> 的关系是「或」：两者任一为真就写诊断日志。
        /// 之所以留这个独立字段，是为了在配置系统就绪**之前**（Awake 早期、
        /// 静态构造期）也能临时打开诊断 —— 排查「配置项根本没绑上」这类问题时，
        /// 那时 CfgDiagLog 还是 null。平时不用管它，保持 false 即可。
        /// </summary>
        internal static bool DiagOn;

        private static bool DiagEnabled()
        {
            try { return DiagOn || (CfgDiagLog != null && CfgDiagLog.Value); }
            catch { return false; }
        }

        /// <summary>
        /// 统一诊断出口。共享层里所有「只在排查时想看」的日志都走这里，
        /// 前缀由调用方自己带上（<c>[UiNav]</c> / <c>[朗读]</c> / <c>[Speech]</c> …），
        /// 这样在 LogOutput.log 里一眼能看出是谁写的。
        ///
        /// 三条纪律（照抄三作的经验）：
        ///   · 关掉时是一行空判断，没有开销；
        ///   · <b>绝不抛异常</b> —— 诊断日志本身不能变成故障源；
        ///   · 诊断日志的产出物（LogOutput.log）**不得入库**：
        ///     里面含台词原文，等于剧本泄漏（流水线 §6 P1 的 IP 红线）。
        /// </summary>
        internal static void Diag(string msg)
        {
            if (!DiagEnabled()) return;
            if (Log == null) return;
            try { Log.LogInfo(msg); } catch { }
        }

        // ====================================================================
        // 共享层需要的配置（名字固定、语义固定，不要改名）
        // ====================================================================

        /// <summary>
        /// 语音后端。可填 <c>自动</c> / <c>Tolk</c> / <c>NVDA</c> / <c>争渡</c>（或 <c>ZDSR</c>）/ <c>SAPI</c>。
        ///
        /// 钉死某一个主要用来排查「为什么不出声」；
        /// <b>钉死后不可用不会回退</b>，这是刻意的 —— 回退会掩盖「你选的这个后端坏了」
        /// 这个事实（见 Speech.cs 的 Verdict 日志）。
        /// </summary>
        internal static ConfigEntry<string> CfgSpeechBackend;

        /// <summary>
        /// 朗读诊断日志：开启后每一句被朗读的文本都写一行 <c>[朗读]</c>。
        /// 【遗留·UiNav 导航模式】下的控件扫描日志也走本开关；新作默认不启用导航模式，
        /// 本项只剩朗读行 —— 诊断日志不依赖导航模式。
        /// </summary>
        internal static ConfigEntry<bool> CfgDiagLog;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】导航是否只收录「画面上真的看得见、点得到」的控件。
        ///
        /// 默认 true。游戏里大量控件是 active 的却停在画面外，或被面板挡住
        /// （IL2CPP 那作尤其明显：Naninovel 面板靠 CanvasGroup.alpha 显隐，
        /// alpha=0 时 GameObject 仍然 active）。false 只在误排除正常按钮时才用。
        /// 新作默认不启用（Plugin 模板里 CfgMenuNav=false），A / B / C 判定见
        /// docs/交互形态纪律.md 与 docs/交互形态判定.md；保留只为老作逐字节不变，
        /// **不等于推荐**。
        /// </summary>
        internal static ConfigEntry<bool> CfgVisibleOnly;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】导航是否按屏幕位置排序（先上后下、同一行先左后右）。
        ///
        /// 默认 true，让「第几项」和画面对得上。false 则回退到按渲染层级
        /// （兄弟序号路径）排序。注意：**两种都必须是严格字典序** ——
        /// List.Sort 的比较函数一旦不满足传递性，结果任意而且不报错（流水线 G9）。
        /// 新作默认不启用；保留只为老作逐字节不变，不等于推荐。
        /// </summary>
        internal static ConfigEntry<bool> CfgSortByPosition;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】人工排除名单（逗号分隔，中英文逗号都认），按**祖先链**匹配。
        ///
        /// 按祖先链而不是只比自己：噪音往往是一个容器里面套着几个按钮，
        /// 只比自己的话容器被排掉了、子按钮还会留在列表里（流水线 §5.3 belfry 那条）。
        /// 名单里的名字必须来自探针的场景树转储实查，不许猜（流水线 §7 铁律 1）。
        /// 新作默认不启用；保留只为老作逐字节不变，不等于推荐。
        /// </summary>
        internal static ConfigEntry<string> CfgExcludeNames;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】危险操作（退出游戏 / 删除存档）是否需要二次确认。
        /// 新作默认不启用；保留只为老作逐字节不变，不等于推荐。
        /// </summary>
        internal static ConfigEntry<bool> CfgQuitConfirm;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】哪些控件需要二次确认，按对象名**全等**匹配
        /// （不是正则，见流水线 G12）。新作默认不启用；保留只为老作逐字节不变，不等于推荐。
        /// </summary>
        internal static ConfigEntry<string> CfgQuitNames;

        /// <summary>
        /// 允许把**原始标识**（对象名 / 类型名 / ID）当标签念出来。默认 <b>false</b>（0.2.2）。
        ///
        /// === 为什么默认关 ===
        ///
        /// 用户反馈的原话是「控件绑定的 Text 读不到时直接念 GameObject 名/类型名/ID」。
        /// 平台层原来在「子树文字 / 逐作钩子 / 别名 / 同行文字」全失败后
        /// <c>return AncestorNameOf(s)</c>，最后 <c>return s.gameObject.name</c> ——
        /// 这是一个**静默**退化：调用方拿到的是非空字符串，没有任何标志说明
        /// 它是对象名（可能还长得像人话，比如 <c>Exit</c>），玩家会当真。
        ///
        /// 现在默认改念结构化语义描述（见 UiNav.SemanticText）：
        /// 「未命名按钮，第 3 项，共 7 项，面板设置」。原始名只写诊断日志
        /// （<see cref="Diag"/>，需要「界面诊断日志」打开）。
        ///
        /// 只有排查「这个控件到底是什么」时才临时打开它 —— 打开后 UI 朗读会变难听，
        /// 不是给玩家用的开关。为 null 时按 false 处理（不依赖逐作是否绑了它）。
        /// </summary>
        internal static ConfigEntry<bool> CfgSpeakRawName;

        /// <summary>
        /// 「标签是推断出来的」时，激活前要不要二次确认。默认 <b>true</b>（0.2.2）。
        ///
        /// 标签质量分层见 UiNav.LabelQuality：Bound / Nearby 是可信的，
        /// Semantic（结构化语义描述）与 Raw（念了原始标识）是**推断**出来的。
        /// 推断标签下的控件照样可以操作，但不能一按就执行 —— 读屏玩家分不清
        /// 「未命名按钮，第 2 项」和旁边那个「确定」，按错可能直接存档 / 覆盖 / 返回标题。
        ///
        /// 语义与退出确认（CfgQuitConfirm）一致：第一次只重复朗读并提示，
        /// 第二次按同一控件才真正 Activate；8 秒不确认失效，换项 / 退出导航即清除。
        /// 为 null 时按 true 处理（安全默认值，不依赖逐作是否绑了它）。
        /// </summary>
        internal static ConfigEntry<bool> CfgUnknownConfirm;

        // ====================================================================
        // 共享层需要的钩子（由各作的 Plugin / Patches 实现并填入）
        // ====================================================================

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】导航模式下是否拦截「推进剧情」。
        ///
        /// 典型填法：<c>A11yHost.BlockGameAdvance = () =&gt; UiNav.BlockGameAdvance;</c>
        ///
        /// 为什么是委托而不是直接引用 UiNav：交互式的逐作差异不在这里，
        /// 而在**挂到哪个方法上**（三作的挂载点名字各不相同，见流水线 §2.3b）。
        /// 挂载点属于逐作层，所以由逐作层把「UiNav 的判断」接到自己的补丁上。
        /// 留空表示「不拦截」，L1 会当作 false 处理。
        ///
        /// ⚠ 新作默认**不启用**（Plugin 模板里 CfgMenuNav=false），A / B / C 判定见
        /// docs/交互形态纪律.md 与 docs/交互形态判定.md。保留只为老作逐字节不变，
        /// **不等于推荐**。
        /// </summary>
        internal static Func<bool> BlockGameAdvance;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】每帧入口。由逐作的 Plugin.Update 调用。
        ///
        /// ⚠ 新作默认**不启用**：界面键盘操作按 docs/交互形态判定.md 判定的
        /// A / B / C 实现 —— A 类只朗读、不要跑 UiNav；B / C 类补在**游戏自己的**
        /// 输入 / 焦点 / 动作函数上，也不需要这个钩子。
        /// 下面这段示例代码是**遗留接法**（0.2.x 导航模式），不要照抄进新作。
        ///
        /// 典型填法（《钟塔》Mono 路线，**遗留接法**）：
        /// <code>
        /// A11yHost.TickUiNav = () =&gt;
        /// {
        ///     if (CfgMenuNav != null &amp;&amp; CfgMenuNav.Value) UiNav.Update();
        /// };
        /// </code>
        ///
        /// IL2CPP 路线如果选择**常驻**关掉 uGUI 的 submit 通路
        /// （<c>EventSystem.sendNavigationEvents = false</c>），也放在这里，
        /// 而**不要**放进去 L1 —— 见流水线 §5.3：
        /// 《钟塔》不能常驻关（游戏自己就在用 EventSystem 选中态做横条导航、
        /// 面板提交、焦点修正、存档槽 ISubmitHandler），
        /// 《透明的她》必须常驻关（游戏全代码没有 SetSelectedGameObject，
        /// 不关的话鼠标点过的按钮会在按空格推进剧情时被重复点击）。
        /// **两作给出了两个相反的正确答案，所以这一条必须逐作判定。**
        ///
        /// 保留这个钩子只为老作逐字节不变，**不等于推荐**。
        /// </summary>
        internal static Action TickUiNav;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】导航模式是否要接管这一下「回车 / 空格」的 submit
        /// （对应 UiNav.ShouldMuteUnitySubmit）。新作默认不启用；保留只为老作逐字节不变，
        /// 不等于推荐。
        /// </summary>
        internal static Func<bool> ShouldMuteUnitySubmit;

        /// <summary>
        /// 【遗留·UiNav 导航模式专用】「这一帧要不要切换导航模式」。
        /// **只增不改**（0.2.2）：留空时平台层保持原来的行为 ——
        /// <c>KeyEdge.Pressed(KeyCode.Tab)</c>，前三作逐字节不变。
        ///
        /// ⚠ 这个钩子存在的前提就是「有一个补丁专属的模式要切换」，这正是第 0 条纪律
        /// 禁止的形态。新作默认**不启用**（Plugin 模板里 CfgMenuNav=false），
        /// A / B / C 判定见 docs/交互形态纪律.md 与 docs/交互形态判定.md。
        /// 保留只为老作逐字节不变，**不等于推荐**；下面的示例代码是遗留接法。
        ///
        /// === 为什么开关不能由平台层直接读配置项 ===
        ///
        /// 1) **组合键解析属于逐作热键系统**。平台层的 KeyEdge 只做单键边沿；
        ///    带修饰键的写法（<c>Ctrl+Alt+G</c>）由 Hotkey.cs 解析，语义是
        ///    「写了哪个修饰键就要求哪个按住、没写的必须没按住、且必须有一个主键」——
        ///    所以 <c>Ctrl+Alt+G</c> 不会被游戏自己的 <c>G</c> / <c>Alt+G</c> 触发，
        ///    也不会被 <c>Ctrl+Alt+X</c> 这种「组合键 + 第三个键」误判（主键对不上）。
        /// 2) **边沿闩锁必须和「有没有抢游戏键」的判定放在一起**。Hotkey.Down 只回答
        ///    「这一帧是否按下」，长按/OS 自动重复会连报，所以逐作层要自己加
        ///    `_navKeyLatched`（抬起复位）。这段状态放在逐作层才看得见它和
        ///    输入框聚焦、IME、游戏自身热键的关系。
        /// 3) **前三作不能改行为**。它们没有这个钩子，默认退回 Tab 即可；
        ///    若平台层改成读 A11yHost.CfgNavKey，没绑的老作会同时失去 Tab 和新键。
        ///
        /// === 0.2.3：窗口拿不到焦点，必须配 <see cref="UseGlobalKeys"/> ===
        ///
        /// 本作窗口 exstyle 含 WS_EX_NOACTIVATE，默认「单击穿透」下**永远没有键盘焦点**，
        /// UnityEngine.Input 读不到任何键（UiNav 的开关/方向键/回车/数字键全部失效）。
        /// 所以逐作除了填这个钩子，还必须 <c>A11yHost.UseGlobalKeys = true</c>，
        /// 让 KeyEdge / Hotkey 走 Win32 全局读取兜底（见 GlobalKeys.cs）。
        ///
        /// 用户把本作导航键定成 **Ctrl+Alt+主键盘加号**：主键是 KeyCode.Equals，
        /// 而「主键盘 + 」本身就是 Shift+Equals，所以配置写 <c>Ctrl+Alt+Plus</c>
        /// （Hotkey.Parse 会强制要求 Shift；小键盘 + = KeypadPlus 不算；
        /// 纯 Ctrl+Alt 没有主键也不算）——这样也不会误触游戏自带的 9 条 Ctrl+Alt+字母热键。
        ///
        /// 逐作填法（《不/存在的莉莉丝》，默认 <c>Ctrl+Alt+Plus</c>）：
        /// <code>
        /// // Plugin.cs
        /// CfgNavKey = Config.Bind("界面导航", "导航模式开关键", "Ctrl+Alt+Plus", "...");
        /// private static bool _navKeyLatched;
        /// private static bool NavKeyPressed()
        /// {
        ///     // 输入框聚焦时不要抢键（Enter 已经用于输入框）
        ///     bool down = Hotkey.Down(CfgNavKey.Value);   // 内部 try/catch
        ///     if (!down && CfgNavKeyAllowTab.Value) down = KeyEdge.Pressed(KeyCode.Tab);
        ///     if (!down) { _navKeyLatched = false; return false; }
        ///     if (_navKeyLatched) return false;
        ///     _navKeyLatched = true;
        ///     return true;
        /// }
        /// A11yHost.NavTogglePressed = NavKeyPressed;   // Awake
        /// A11yHost.UseGlobalKeys = true;               // 本作窗口拿不到焦点（0.2.3）
        /// </code>
        ///
        /// 平台层调用处对异常兜底：钩子抛异常时按「没按」处理，绝不把异常抛进主循环。
        /// </summary>
        internal static Func<bool> NavTogglePressed;

        /// <summary>
        /// 是否用 Win32 全局按键读取（GetAsyncKeyState）补足 UnityEngine.Input。
        /// **只增不改**，默认 false（前三作不受影响）。
        ///
        /// === 为什么需要它（0.2.3 实机取证） ===
        ///
        /// 《不/存在的莉莉丝》窗口 class = UnityWndClass、exstyle = 0x080800B0
        /// （WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST |
        /// WS_EX_TRANSPARENT）：默认「单击穿透」模式下**永远拿不到键盘焦点**
        /// （SetForegroundWindow / AttachThreadInput / SwitchToThisWindow 都试过，
        /// 前台一直是 Program Manager / 浏览器）。此时 UnityEngine.Input 恒为 false，
        /// UiNav 的导航开关、方向键、回车、数字键**全部静默失效** —— 是焦点依赖，
        /// 不是绑定问题。
        ///
        /// 打开后：KeyEdge / Hotkey 的「按住 / 按下」取「Input 或全局」并集，
        /// 边沿由 GlobalKeys 的静态表自己算（GetAsyncKeyState 只回答「是否按住」）。
        /// user32 不可用（非 Windows / 没有该导出）时自动退回纯 Input，
        /// 不抛异常、不刷日志。
        ///
        /// 安全边界：全局读取会看到**其它程序**的按键（这正是「窗口没焦点也能用」
        /// 的实现方式），所以必须逐作显式打开；平台层不装全局钩子、不做 DLL 注入。
        ///
        /// 接线方向：契约层只放这个 bool；平台层每帧懒同步它
        /// （GlobalKeys.Reading → SyncWithHostSwitch），保证 platform → contract
        /// 的分层不被倒过来。
        /// </summary>
        public static bool UseGlobalKeys;

        /// <summary>
        /// 全局读键的「焦点门」：返回 false 时，平台层**暂停** Win32 全局读取（0.2.5）。
        /// **只增不改**，留空 = 不设门，前三作行为逐字节不变。
        ///
        /// === 为什么需要它（《魔法少女ノ魔女裁判》实机取证） ===
        ///
        /// <c>GetAsyncKeyState</c> 是**整机范围**的：它不关心前台是哪个窗口。
        /// 打开 <see cref="UseGlobalKeys"/> 之后，玩家在浏览器里打字、在聊天框里按
        /// Ctrl+Alt+字母，本补丁的导航开关/方向键/回车/数字键全会跟着触发 ——
        /// 对「窗口永远拿不到焦点、只能靠全局读取」的作，这是必须付的代价，
        /// 但不能让它无限扩大：第五作把这一判断写在 L3（Plugin.FocusGate +
        /// Choices 的输入框判定），第六作也各写各的。
        ///
        /// 五次实机之后把它提为契约钩子：**平台层只问「现在能不能读全局键」，
        /// 具体条件（游戏窗口是否前台、是否在输入框/IME、是否在过场动画、
        /// 是否轮到玩家操作）留给逐作** —— 平台层不认识任何游戏类型，
        /// 也不该猜「什么时候算玩家在操作本游戏」。
        ///
        /// === 语义与兜底 ===
        ///
        ///   · 返回 true  → 全局读取照旧（默认；留空等价于恒 true）；
        ///   · 返回 false → GlobalKeys.Reading 变 false：KeyEdge / Hotkey / UiNav
        ///     的全局那一路同时停，且清掉「上一帧按住」表，避免门重新打开时把
        ///     门关闭期间的一次按键误判成按下沿；
        ///   · 钩子抛异常 → 按 **true** 处理（fail-open）。门是「减少误触」的优化，
        ///     不是安全边界；让它把热键整段关掉比误触更糟。
        ///
        /// 逐作填法（示意）：
        /// <code>
        /// A11yHost.KeyInputAllowed = () => IsGameForeground() &amp;&amp; !IsTextInputFocused();
        /// </code>
        ///
        /// [未验证] 本钩子的空实现路径（null / 恒 true）与 0.2.4.0 逐字节等价；
        /// 但「游戏级焦点判定」的正确条件仍要逐作实机测（尤其单击穿透 + 输入框聚焦）。
        /// </summary>
        public static Func<bool> KeyInputAllowed;

        // ====================================================================
        // 界面标签钩子（0.1.0.1 新增，来源：《妹控计划》实机取证）
        //
        // 起因：平台层原来的取值顺序是「子树文字 → 同行兄弟文字 → 别名表 → 对象名兜底」，
        // 但第四作实测 **173 个可交互控件里 131 个读不到文字**，只能退化成对象名
        // （CommonCloseButton / Button (6) / CCPlus / VolumeButton …），而且同名控件在不同
        // 面板里含义不同（CommonButton 在番茄钟里是「停止 / 暂停 / 下一个」）——
        // 「对象名 → 中文」的平表根本区分不开，必须能看**路径**。
        //
        // 四个钩子**只增不改**：平台层原有的取值顺序一条不动，只是在
        // 「别名表之后、对象名兜底之前」多问一句。钩子没填（前三作）时
        // 行为与从前逐字节一致 —— 由 tools/compile_check.ps1 与逐字节 diff 保证。
        // ====================================================================

        /// <summary>
        /// 「这段文字是预制体占位，别念」。平台层每读到一段文本都会问一次。
        ///
        /// 实例：`New Text` / `Button` / `音效名称` / `000` 这些是预制体上没被运行时
        /// 替换掉的默认字，念出来比念对象名还糟 —— 玩家会以为那真的叫「New Text」。
        /// </summary>
        internal static Func<string, bool> GameTextIsNoise;

        /// <summary>
        /// 逐作标签解析。平台层在「同行兄弟文字」之后、对象名兜底之前问它；
        /// 输入框则更早（在占位提示之前）。返回空串表示「我不认识这个控件」，照旧往下走。
        /// </summary>
        internal static Func<Selectable, string> GameLabelOf;

        /// <summary>
        /// 面板组的中文名。平台层原来直接念 Canvas 下的顶层祖先名（`Right` / `UpRight`）。
        ///
        /// 0.2.2 起：没填或返回空串时不再念英文对象名 —— 名字里没有中日韩字符就
        /// 退成中性兜底「面板 + 组号」（见 UiNav.GroupName / GroupLabel）。
        /// 对象名本身含中文时仍照念；<see cref="CfgSpeakRawName"/> 打开时也照念原始名。
        /// </summary>
        internal static Func<Transform, string> GameGroupName;

        /// <summary>
        /// 面板组的排序权重，**小的排前面**。默认（没填）时平台层保持原来的排序。
        /// 用途：把最常用的那一组排到第一组，否则玩家进导航落在别处、要按 PageDown 才够得着。
        /// </summary>
        internal static Func<Transform, int> GameGroupPriority;

        // ====================================================================
        // 自检
        // ====================================================================

        /// <summary>
        /// 开局自检：把没填的槽位一次性写进日志。
        ///
        /// 存在的理由：这类「忘记赋值」的错误在 C# 里是**静默**的 ——
        /// CfgSpeechBackend 为 null 不会崩，只会让语音后端一直走默认值；
        /// Log 为 null 更糟，诊断日志会全部消失，而你以为是自己没开开关。
        /// 与其在实机上猜，不如开局就报出来。
        ///
        /// 返回缺失项的条数（0 = 全填好了）。
        /// </summary>
        internal static int Validate()
        {
            string[] missing = Missing();
            if (Log == null) return missing.Length;   // 没有日志出口，只能靠返回值
            try
            {
                if (missing.Length == 0)
                {
                    Diag("[A11yHost] 契约已填满。");
                }
                else
                {
                    Log.LogWarning("[A11yHost] 契约有 " + missing.Length + " 项没填: "
                                   + string.Join(" / ", missing)
                                   + "。平台层会退回默认行为，功能可能静默失效。");
                }
            }
            catch { }
            return missing.Length;
        }

        /// <summary>返回没填的槽位名。供自检与单元测试用。</summary>
        internal static string[] Missing()
        {
            var list = new System.Collections.Generic.List<string>();
            if (Log == null) list.Add("Log");
            if (CfgSpeechBackend == null) list.Add("CfgSpeechBackend");
            if (CfgDiagLog == null) list.Add("CfgDiagLog");
            if (CfgVisibleOnly == null) list.Add("CfgVisibleOnly");
            if (CfgSortByPosition == null) list.Add("CfgSortByPosition");
            if (CfgExcludeNames == null) list.Add("CfgExcludeNames");
            if (CfgQuitConfirm == null) list.Add("CfgQuitConfirm");
            if (CfgQuitNames == null) list.Add("CfgQuitNames");
            if (BlockGameAdvance == null) list.Add("BlockGameAdvance");
            if (TickUiNav == null) list.Add("TickUiNav");
            // 以下都是 0.2.2 新增的**可选**槽位，留空表示「用平台层的安全默认值」，
            // 不计入缺失 —— 否则三个已发布的老作每次启动都会白报几条，
            // 把真正该修的漏填项淹掉：
            //   · ShouldMuteUnitySubmit ：留空 = 不接管 submit
            //   · NavTogglePressed      ：留空 = 开关仍旧是 Tab（前三作行为不变）
            //   · CfgSpeakRawName       ：留空 = false（不念对象名/类型名）
            //   · CfgUnknownConfirm     ：留空 = true（推断标签激活前二次确认）
            return list.ToArray();
        }

        /// <summary>卸载 / 重载时清空，避免静态字段把上一局的实例（Unity 伪空对象）留给下一局。</summary>
        internal static void Clear()
        {
            Log = null;
            DiagOn = false;
            CfgSpeechBackend = null;
            CfgDiagLog = null;
            CfgVisibleOnly = null;
            CfgSortByPosition = null;
            CfgExcludeNames = null;
            CfgQuitConfirm = null;
            CfgQuitNames = null;
            CfgSpeakRawName = null;
            CfgUnknownConfirm = null;
            BlockGameAdvance = null;
            TickUiNav = null;
            ShouldMuteUnitySubmit = null;
            NavTogglePressed = null;
            UseGlobalKeys = false;
            KeyInputAllowed = null;
        }
    }
}
