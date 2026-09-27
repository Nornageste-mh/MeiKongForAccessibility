using System;
using BepInEx.Configuration;
using BepInEx.Logging;

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
        /// 语音后端。可填 <c>自动</c> / <c>Tolk</c> / <c>NVDA</c> / <c>SAPI</c>。
        ///
        /// 钉死某一个主要用来排查「为什么不出声」；
        /// <b>钉死后不可用不会回退</b>，这是刻意的 —— 回退会掩盖「你选的这个后端坏了」
        /// 这个事实（见 Speech.cs 的 Verdict 日志）。
        /// </summary>
        internal static ConfigEntry<string> CfgSpeechBackend;

        /// <summary>朗读诊断日志：开启后每一句被朗读的文本都写一行 <c>[朗读]</c>。</summary>
        internal static ConfigEntry<bool> CfgDiagLog;

        /// <summary>
        /// 导航是否只收录「画面上真的看得见、点得到」的控件。
        ///
        /// 默认 true。游戏里大量控件是 active 的却停在画面外，或被面板挡住
        /// （IL2CPP 那作尤其明显：Naninovel 面板靠 CanvasGroup.alpha 显隐，
        /// alpha=0 时 GameObject 仍然 active）。false 只在误排除正常按钮时才用。
        /// </summary>
        internal static ConfigEntry<bool> CfgVisibleOnly;

        /// <summary>
        /// 导航是否按屏幕位置排序（先上后下、同一行先左后右）。
        ///
        /// 默认 true，让「第几项」和画面对得上。false 则回退到按渲染层级
        /// （兄弟序号路径）排序。注意：**两种都必须是严格字典序** ——
        /// List.Sort 的比较函数一旦不满足传递性，结果任意而且不报错（流水线 G9）。
        /// </summary>
        internal static ConfigEntry<bool> CfgSortByPosition;

        /// <summary>
        /// 人工排除名单（逗号分隔，中英文逗号都认），按**祖先链**匹配。
        ///
        /// 按祖先链而不是只比自己：噪音往往是一个容器里面套着几个按钮，
        /// 只比自己的话容器被排掉了、子按钮还会留在列表里（流水线 §5.3 belfry 那条）。
        /// 名单里的名字必须来自探针的场景树转储实查，不许猜（流水线 §7 铁律 1）。
        /// </summary>
        internal static ConfigEntry<string> CfgExcludeNames;

        /// <summary>危险操作（退出游戏 / 删除存档）是否需要二次确认。</summary>
        internal static ConfigEntry<bool> CfgQuitConfirm;

        /// <summary>哪些控件需要二次确认，按对象名**全等**匹配（不是正则，见流水线 G12）。</summary>
        internal static ConfigEntry<string> CfgQuitNames;

        // ====================================================================
        // 共享层需要的钩子（由各作的 Plugin / Patches 实现并填入）
        // ====================================================================

        /// <summary>
        /// 导航模式下是否拦截「推进剧情」。
        ///
        /// 典型填法：<c>A11yHost.BlockGameAdvance = () =&gt; UiNav.BlockGameAdvance;</c>
        ///
        /// 为什么是委托而不是直接引用 UiNav：交互式的逐作差异不在这里，
        /// 而在**挂到哪个方法上**（三作的挂载点名字各不相同，见流水线 §2.3b）。
        /// 挂载点属于逐作层，所以由逐作层把「UiNav 的判断」接到自己的补丁上。
        /// 留空表示「不拦截」，L1 会当作 false 处理。
        /// </summary>
        internal static Func<bool> BlockGameAdvance;

        /// <summary>
        /// 每帧入口。由逐作的 Plugin.Update 调用，逐作在这里决定要不要跑 UiNav。
        ///
        /// 典型填法（《钟塔》Mono 路线）：
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
        /// </summary>
        internal static Action TickUiNav;

        /// <summary>导航模式是否要接管这一下「回车 / 空格」的 submit（对应 UiNav.ShouldMuteUnitySubmit）。</summary>
        internal static Func<bool> ShouldMuteUnitySubmit;

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
            // ShouldMuteUnitySubmit 是可选钩子，留空表示「不接管」，不算缺失。
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
            BlockGameAdvance = null;
            TickUiNav = null;
            ShouldMuteUnitySubmit = null;
        }
    }
}
