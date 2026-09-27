using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MeiKongA11y
{
    /// <summary>
    /// 《妹控计划》读屏无障碍补丁。
    ///
    /// 这是**逐作层**的入口。平台层（Speech / Nvda / Sapi / UiNav）从 A11yFramework 整份拷来，
    /// **一行都不要改**；这个文件负责四件事：
    ///   1) 注册配置项（分「朗读」「界面导航」「交互」「其它」四组）
    ///   2) 把配置与钩子填进 A11yHost（契约层）
    ///   3) 声明 Harmony 补丁点
    ///   4) 每帧的输入分发（导航 / 选项数字键 / 功能菜单 / 状态播报 / 重读）
    ///
    /// === 本作的策略与前三作不同，理由都在 P0/P1 文档里 ===
    ///   · 97.2% 的台词有配音 → 默认**不**自动朗读有配音的行（配音就是内容），
    ///     但保留「重读键」作为手动通路；无配音的行才自动念。
    ///   · 没有说话人数据 → 不播报「谁在说」（见 TextProc.Speaker 的说明）。
    ///   · 只有一个场景、旧对话系统是不可达死代码 → 只挂 ShiMeng.DialogueV2 一套。
    ///   · 交互是主战场 → 面板感知 + 功能菜单 + 状态播报（Surfaces.cs）。
    /// </summary>
    [BepInPlugin(Guid, "妹控计划 A11y Reader", "0.1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "meikong.a11y.reader";

        // ==================== 配置项 ====================

        // ---- 朗读 ----
        internal static ConfigEntry<bool>   CfgReadUnvoiced;     // 无配音行：自动念
        internal static ConfigEntry<bool>   CfgReadVoiced;       // 有配音行：也自动念（默认关）
        internal static ConfigEntry<bool>   CfgReadChoices;
        internal static ConfigEntry<bool>   CfgChoiceHotkeys;
        internal static ConfigEntry<string> CfgRepeatKey;
        internal static ConfigEntry<string> CfgSpeechBackend;

        // ---- 界面导航 ----
        internal static ConfigEntry<bool>   CfgMenuNav;
        internal static ConfigEntry<bool>   CfgSortByPosition;
        internal static ConfigEntry<bool>   CfgVisibleOnly;
        internal static ConfigEntry<bool>   CfgQuitConfirm;
        internal static ConfigEntry<string> CfgQuitNames;
        internal static ConfigEntry<string> CfgExcludeNames;

        // ---- 交互 ----
        internal static ConfigEntry<string> CfgMenuKey;          // 功能菜单
        internal static ConfigEntry<string> CfgStateKey;         // 状态播报
        internal static ConfigEntry<bool>   CfgAnnouncePanel;    // 面板开关播报

        // ---- 其它 ----
        internal static ConfigEntry<bool>   CfgStartupHint;
        internal static ConfigEntry<bool>   CfgDiagLog;

        private Harmony _harmony;
        private float _nextSendNavCheck;

        private void Awake()
        {
            A11yHost.Log = Logger;

            // ---------------- 配置 ----------------
            CfgReadUnvoiced = Config.Bind("朗读", "朗读无配音剧情", true,
                "全作 1392 行台词里 1353 行有配音（97.2%），剩下 39 行没有配音 —— 这一项决定那 39 行念不念。\n" +
                "注意：本作绝大多数台词都有配音，所以这一项开着也不会吵。");

            CfgReadVoiced = Config.Bind("朗读", "有配音的行也自动朗读", false,
                "把有配音的行也交给读屏念一遍。\n" +
                "默认关闭的理由：配音本身就是内容，读屏叠上去两边都听不清。\n" +
                "想吃「全语音+全文本」的玩家可以打开；只想听配音的保持关闭，\n" +
                "需要确认文字时按重读键即可。");

            CfgReadChoices = Config.Bind("朗读", "朗读选项", true,
                "出现选项时把全部选项一次念完并编号。本作共 123 处多选（111 处二选一）。");

            CfgChoiceHotkeys = Config.Bind("朗读", "数字键选择选项", true,
                "用数字键 1-9 选择对应编号的选项。等价于用鼠标点那一个按钮 —— 不预选、不跳过。\n" +
                "选项还没全部出现完时按数字键无效（那是游戏自己的节奏）。");

            CfgRepeatKey = Config.Bind("朗读", "重读按键", "Backspace",
                "重新朗读最近一次收到的台词。对**有配音的行**来说，这是唯一的朗读通路。\n" +
                "填 KeyCode 名称。不要填游戏原生占用的键（见 README「游戏原生占用的键」）。");

            CfgSpeechBackend = Config.Bind("朗读", "语音后端", "自动",
                "自动 / Tolk / NVDA / SAPI。默认按 Tolk → NVDA → SAPI 挑第一个可用的；\n" +
                "钉死某个后端且它不可用时**不会回退**（这是刻意的，用于排查）。");

            CfgMenuNav = Config.Bind("界面导航", "菜单键盘导航", true,
                "Tab 进入 / 退出导航模式；上下选择，左右调滑条，回车激活。\n" +
                "本作全屏 247 个控件里通常只有 18 个此刻可用，所以「只导航看得见的控件」建议保持开启。");

            CfgSortByPosition = Config.Bind("界面导航", "按屏幕位置排序控件", true,
                "按控件在屏幕上的位置排序（先上后下、同一行先左后右），让「第几项」和画面对得上。");

            CfgVisibleOnly = Config.Bind("界面导航", "只导航看得见的控件", true,
                "只收录画面上真的看得见、点得到的控件。本作有大量「active 但 alpha=0」的幽灵面板，\n" +
                "关掉这一项会把没打开的面板里的控件也念出来。");

            CfgQuitConfirm = Config.Bind("界面导航", "退出前二次确认", true,
                "在导航模式下激活下面列出的控件会先念一次确认，再按一次才真的执行；按方向键取消。");

            CfgQuitNames = Config.Bind("界面导航", "退出确认对象名", "退出游戏,退出,Quit",
                "哪些控件需要二次确认，按 Unity 对象名**全等**匹配（不是正则）。多个用逗号分隔。");

            CfgExcludeNames = Config.Bind("界面导航", "排除的对象名", "Scrollbar Horizontal,Scrollbar Vertical,Common_VScrollbar Variant",
                "这些对象**及其整棵子树**里的控件不纳入导航（按祖先链匹配）。\n" +
                "默认排除滚动条：本作用上下键选择、左右键调滑条，滚动条本身是噪音。\n" +
                "名字来自 probe 的运行时场景树转储实查。");

            CfgMenuKey = Config.Bind("交互", "功能菜单键", "F1",
                "念出功能菜单（小游戏 / 番茄钟 / 日程表 / 设置 / 故事与收藏 / 换装）并编号，\n" +
                "然后按数字键直接打开 —— 不用在一堆控件里翻。");

            CfgStateKey = Config.Bind("交互", "状态播报键", "F2",
                "念出当前打开的面板、里面有几个可操作项、番茄钟剩余时间等状态。");

            CfgAnnouncePanel = Config.Bind("交互", "面板开关时自动播报", true,
                "面板被打开 / 关闭时主动说一句（例：「已打开：日程表，6 个可操作项」）。");

            CfgStartupHint = Config.Bind("其它", "启动时播报", true,
                "游戏启动后朗读一句「无障碍补丁已加载」，用来确认读屏通路是通的。");

            CfgDiagLog = Config.Bind("其它", "界面诊断日志", false,
                "把朗读与导航的诊断写进 LogOutput.log（排查用）。\n" +
                "日志含台词原文，**不要公开**（IP 红线）。");

            // ---------------- 契约层 ----------------
            A11yHost.CfgSpeechBackend  = CfgSpeechBackend;
            A11yHost.CfgDiagLog        = CfgDiagLog;
            A11yHost.CfgVisibleOnly    = CfgVisibleOnly;
            A11yHost.CfgSortByPosition = CfgSortByPosition;
            A11yHost.CfgExcludeNames   = CfgExcludeNames;
            A11yHost.CfgQuitConfirm    = CfgQuitConfirm;
            A11yHost.CfgQuitNames      = CfgQuitNames;

            A11yHost.BlockGameAdvance      = () => UiNav.BlockGameAdvance;
            A11yHost.ShouldMuteUnitySubmit = () => UiNav.ShouldMuteUnitySubmit;
            A11yHost.TickUiNav = () =>
            {
                if (CfgMenuNav != null && CfgMenuNav.Value) UiNav.Update();
                Choices.Update();
                Surfaces.Tick();
            };
            A11yHost.Validate();

            // ---------------- 语音 ----------------
            try { Speech.Init(Logger); }
            catch (Exception e) { Logger.LogError("[语音] Init 异常: " + e); }

            // ---------------- Harmony ----------------
            _harmony = new Harmony(Guid);
            foreach (var t in new[] { typeof(Patches.LineChanged), typeof(Patches.ScenarioStarted),
                                      typeof(Patches.ScenarioEnded), typeof(Patches.ChoiceWaiting),
                                      typeof(Patches.ChoiceBind), typeof(Patches.ChoiceInteractable) })
            {
                try { _harmony.PatchAll(t); A11yHost.Diag("[补丁] " + t.Name + " OK"); }
                catch (Exception e) { Logger.LogError("[补丁] " + t.Name + " 失败: " + e.Message); }
            }

            StartCoroutine(StartupHint());
        }

        /// <summary>
        /// 启动提示：等语音后端就绪再说（NVDA 可能比游戏晚启动，平台层会每 10 秒重试）。
        /// </summary>
        private IEnumerator StartupHint()
        {
            float t = 0f;
            while (t < 60f)
            {
                yield return new WaitForSecondsRealtime(2f);
                t += 2f;
                bool ready = false;
                try { ready = Speech.Ready(); } catch { }
                if (!ready) continue;
                if (CfgStartupHint != null && CfgStartupHint.Value)
                    Speech.Speak("妹控计划无障碍补丁已加载。按 F1 打开功能菜单，按 F2 播报状态，按 Tab 进入界面导航。", true);
                yield break;
            }
        }

        private void Update()
        {
            try
            {
                // uGUI 的 submit 通路：本作全代码零处 SetSelectedGameObject、运行时选中项恒为 null，
                // 所以常驻关闭是安全的（P0 §4.3 E5）。但 VideoPanelManager 播完视频会把它置回 true，
                // 所以这里周期性复位 —— 比 Patch 一个私有方法更耐游戏改版。
                if (Time.realtimeSinceStartup >= _nextSendNavCheck)
                {
                    _nextSendNavCheck = Time.realtimeSinceStartup + 1f;
                    var es = EventSystem.current;
                    if (es != null && es.sendNavigationEvents) es.sendNavigationEvents = false;
                }

                // 每帧入口：导航 / 选项 / 面板感知 / 功能菜单
                try { A11yHost.TickUiNav?.Invoke(); }
                catch (Exception e) { A11yHost.Diag("[每帧] TickUiNav 异常: " + e.Message); }

                // 热键
                if (KeyDown(CfgMenuKey)) Surfaces.OpenMenu();
                if (KeyDown(CfgStateKey)) Surfaces.ReportState();
                if (KeyDown(CfgRepeatKey)) Reader.Repeat();
            }
            catch (Exception e) { A11yHost.Diag("[每帧] Update 异常: " + e.Message); }
        }

        /// <summary>
        /// 配置里的键名 → 是否本帧按下。
        /// 手工处理单数字（流水线 G3：`Enum.TryParse("0")` 会成功但得到 KeyCode.None，
        /// 而 `Enum.TryParse("1")` 在部分运行时会解析成数值 1 = KeyCode.Backspace，全是坑）。
        /// </summary>
        internal static bool KeyDown(ConfigEntry<string> cfg)
        {
            if (cfg == null) return false;
            var k = ParseKey(cfg.Value);
            if (k == KeyCode.None) return false;
            return Input.GetKeyDown(k);
        }

        internal static KeyCode ParseKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return KeyCode.None;
            s = s.Trim();
            if (s.Length == 1 && s[0] >= '1' && s[0] <= '9')
                return KeyCode.Alpha0 + (s[0] - '0');
            if (s.Length == 1 && s[0] == '0')
                return KeyCode.Alpha0;
            if (s.Length >= 2 && (s[0] == 'F' || s[0] == 'f'))
            {
                int n;
                if (int.TryParse(s.Substring(1), out n) && n >= 1 && n <= 12)
                    return KeyCode.F1 + (n - 1);
            }
            try
            {
                KeyCode k;
                if (Enum.TryParse<KeyCode>(s, true, out k) && k != KeyCode.None) return k;
            }
            catch { }
            return KeyCode.None;
        }
    }
}
