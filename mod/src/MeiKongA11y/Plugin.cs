using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MeiKongA11y
{
    /// <summary>
    /// 《妹控计划》读屏无障碍补丁。
    ///
    /// 这是**逐作层**的入口。平台层（Speech / Nvda / Sapi / UiNav）从 A11yFramework
    /// 整份拷来，**一行都不要改**；这个文件负责三件事：
    ///   1) 注册配置项（分「朗读」「界面导航」「其它」三组，组名不要改）
    ///   2) 把配置与钩子填进 A11yHost（契约层）
    ///   3) 声明 Harmony 补丁点
    ///
    /// === 补丁点一览（方法名**必须**来自 Assembly-CSharp.dll 的反编译实查）===
    ///
    ///   功能                        挂载点
    ///   ------------------------    ------------------------------------------
    ///   TODO 抓住当前这一行          <反编译实查的方法名>
    ///   TODO 朗读无配音台词          <反编译实查的方法名>
    ///   TODO 朗读选项 + 数字键选择    <反编译实查的方法名>
    ///   TODO 限时选择延长倒计时       <反编译实查的方法名>（只改 ref delay）
    ///   TODO 拦住游戏自己的「推进剧情」<反编译实查的方法名>
    ///   菜单 / 存档 / 设置键盘导航    无挂载点，UiNav 每帧扫描 Selectable
    ///
    /// 铁律 1：不许猜方法名和对象名。只能来自 ilspycmd 反编译实查，或探针场景树转储。
    /// </summary>
    [BepInPlugin(Guid, "妹控计划 A11y Reader", "0.1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "meikong.a11y.reader";

        // ==================== 配置项 ====================
        //
        // 组名与项名照抄三作（流水线 §6 P2）：玩家在三个补丁之间切换时不用重新学。
        // 中文说明里**要写实测数字** —— 那是本补丁的可信度来源。
        // 例：「全作 3812 行里 1491 行有配音（39.1%），其余 2321 行靠这个开关才听得到。」

        // ---- 朗读 ----
        internal static ConfigEntry<bool> CfgReadUnvoiced;
        internal static ConfigEntry<bool> CfgSpeakWhenUnknown;
        internal static ConfigEntry<bool> CfgSpeakName;
        internal static ConfigEntry<bool> CfgReadChoices;
        internal static ConfigEntry<bool> CfgChoiceHotkeys;
        internal static ConfigEntry<float> CfgRealTimeSeconds;
        internal static ConfigEntry<string> CfgSilenceKey;
        internal static ConfigEntry<string> CfgRepeatKey;
        internal static ConfigEntry<string> CfgSpeechBackend;
        internal static ConfigEntry<bool> CfgReadPhone;
        internal static ConfigEntry<bool> CfgReadPhoneSticker;

        // ---- 界面导航 ----
        internal static ConfigEntry<bool> CfgMenuNav;
        internal static ConfigEntry<bool> CfgSortByPosition;
        internal static ConfigEntry<bool> CfgVisibleOnly;
        internal static ConfigEntry<bool> CfgQuitConfirm;
        internal static ConfigEntry<string> CfgQuitNames;
        internal static ConfigEntry<string> CfgExcludeNames;

        // ---- 其它 ----
        internal static ConfigEntry<bool> CfgStartupHint;
        internal static ConfigEntry<bool> CfgAnnounceOnSkipStop;
        internal static ConfigEntry<bool> CfgDiagLog;

        private Harmony _harmony;

        private void Awake()
        {
            // ================================================================
            // 1) 先填日志出口 —— 后面每一步失败都要能记下来
            // ================================================================
            A11yHost.Log = Logger;

            // ================================================================
            // 2) 注册配置项
            //    三组的组名固定：「朗读」「界面导航」「其它」
            // ================================================================
            CfgReadUnvoiced = Config.Bind("朗读", "朗读无配音剧情", true,
                "TODO 逐作实测数字：全作 <N> 行里 <M> 行有配音（<P>%），其余靠这个开关才听得到。");

            CfgSpeakWhenUnknown = Config.Bind("朗读", "无法判定时也朗读", true,
                "当抓不到当前行的剧本数据时（例如刚读档跳进剧情），选择朗读而不是跳过。");

            CfgSpeakName = Config.Bind("朗读", "朗读时带上说话人", true,
                "在台词前加上说话人名字。旁白没有名字，不会加前缀。");

            CfgReadChoices = Config.Bind("朗读", "朗读选项", true,
                "出现选项时，把全部选项一次念完并编号。TODO 本作选项数量：<N> 处。");

            CfgChoiceHotkeys = Config.Bind("朗读", "数字键选择选项", true,
                "用数字键 1-9 选择对应编号的选项。");

            CfgRealTimeSeconds = Config.Bind("朗读", "限时选择时长", 20f,
                "限时选择的倒计时秒数。\n" +
                "读屏念完选项需要更多时间，所以默认放宽；填原版秒数即恢复原版节奏。\n" +
                "**倒计时只能拉长，不能取消**：到点会走这一批最后一条的沉默分支，\n" +
                "取消掉等于删掉一个剧情分支。");

            CfgSilenceKey = Config.Bind("朗读", "沉默按键", "0",
                "在限时选择里立刻选择「沉默」（什么都不做）。留空则关闭。");

            CfgRepeatKey = Config.Bind("朗读", "重读按键", "Backspace",
                "重新朗读当前这一句的按键。填 KeyCode 名称。\n" +
                "警告：不要填游戏原生占用的键（见 README 的「游戏原生占用的键」）。");

            CfgSpeechBackend = Config.Bind("朗读", "语音后端", "自动",
                "用哪个后端朗读。默认「自动」按 Tolk → NVDA → SAPI 挑第一个可用的。\n" +
                "可以填：自动 / Tolk / NVDA / SAPI。\n" +
                "钉死的后端不可用时**不会**回退 —— 这是刻意的，用于排查。");

            CfgReadPhone = Config.Bind("朗读", "朗读手机消息", true,
                "朗读游戏里的手机聊天消息。TODO 本作有几段电话/聊天剧情。");

            CfgReadPhoneSticker = Config.Bind("朗读", "朗读手机表情", true,
                "手机聊天里的表情图片提示为「表情图片」。");

            CfgMenuNav = Config.Bind("界面导航", "菜单键盘导航", true,
                "让标题菜单 / 存读档 / 设置 / 画廊可以用键盘操作并被朗读。\n" +
                "  Tab          进入 / 退出导航模式\n" +
                "  上 / 下      上一项 / 下一项\n" +
                "  左 / 右      调整滑条\n" +
                "  回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）\n" +
                "  Home / End   第一项 / 最后一项\n" +
                "  PageUp/PageDown  切换面板组");

            CfgSortByPosition = Config.Bind("界面导航", "按屏幕位置排序控件", true,
                "按控件在屏幕上的位置排序（先上后下、同一行先左后右），让「第几项」和画面对得上。\n" +
                "关掉则改回按渲染层级（兄弟节点序号）排序。");

            CfgVisibleOnly = Config.Bind("界面导航", "只导航看得见的控件", true,
                "只把画面上真正能看到、能点到的控件纳入导航。\n" +
                "只有在发现正常按钮被误排除时才需要关掉。");

            CfgQuitConfirm = Config.Bind("界面导航", "退出前二次确认", true,
                "在导航模式下激活下面列出的控件会先朗读一次确认，再按一次才真的执行；\n" +
                "按方向键即取消。");

            CfgQuitNames = Config.Bind("界面导航", "退出确认对象名", "TODO",
                "哪些控件需要二次确认，按 Unity 里的对象名**全等**匹配（不是正则）。\n" +
                "名字从 probe 的运行时场景树转储里实查。多个名字用逗号分隔；留空则关闭。");

            CfgExcludeNames = Config.Bind("界面导航", "排除的对象名", "",
                "这些对象**及其整棵子树**里的控件不纳入导航（按祖先链匹配）。\n" +
                "用途：把画面上的噪音（社交链接条、装饰按钮）从导航里摘掉。\n" +
                "名字从 probe 的运行时场景树转储里实查。多个名字用逗号分隔；留空则关闭。");

            CfgStartupHint = Config.Bind("其它", "启动时播报", true,
                "游戏启动后朗读一句「无障碍补丁已加载」，用来确认读屏通路是通的。");

            CfgAnnounceOnSkipStop = Config.Bind("朗读", "快进停止时补念当前句", true,
                "快进停下来的时候，把停在的那一句补念出来。\n" +
                "快进期间补丁不朗读（否则会刷屏），所以停下来那一刻屏幕上是什么\n" +
                "读屏用户完全不知道 —— 这一项就是补这个缺口。");

            CfgDiagLog = Config.Bind("其它", "界面诊断日志", false,
                "把进入导航模式时扫描到的控件全部写进 LogOutput.log。\n" +
                "排查完请关掉，否则日志会变得很大。\n" +
                "注意：日志含台词原文，**不要公开**（IP 红线，见 README）。");

            // ================================================================
            // 3) 填契约层 —— 平台层唯一看得见的东西
            //    漏填不会崩，只会静默失效，所以最后一定要 Validate()
            // ================================================================
            A11yHost.CfgSpeechBackend  = CfgSpeechBackend;
            A11yHost.CfgDiagLog        = CfgDiagLog;
            A11yHost.CfgVisibleOnly    = CfgVisibleOnly;
            A11yHost.CfgSortByPosition = CfgSortByPosition;
            A11yHost.CfgExcludeNames   = CfgExcludeNames;
            A11yHost.CfgQuitConfirm    = CfgQuitConfirm;
            A11yHost.CfgQuitNames      = CfgQuitNames;

            A11yHost.BlockGameAdvance  = () => UiNav.BlockGameAdvance;
            A11yHost.ShouldMuteUnitySubmit = () => UiNav.ShouldMuteUnitySubmit;

            // 每帧入口。逐作在这里决定要不要跑 UiNav。
            //
            // ★ 这一作要不要**常驻**关掉 uGUI 的 submit 通路
            //   （EventSystem.sendNavigationEvents = false）？**必须单独判定**：
            //   判据是「游戏自己有没有用 SetSelectedGameObject / 选中态」。
            //     · 有（横条导航 / 面板提交 / 存档槽 ISubmitHandler）→ 不能常驻关，
            //       只在导航模式确实要接管那一下按键时才掐（UiNav 已经这么做）
            //     · 全代码没有 → 必须常驻关，否则鼠标点过的按钮会在按空格
            //       推进剧情时被重复点击
            //   见 README「一条必须逐作判定的『正确答案』」。
            A11yHost.TickUiNav = () =>
            {
                if (CfgMenuNav != null && CfgMenuNav.Value) UiNav.Update();
                Choices.Update();
            };

            A11yHost.Validate();

            // ================================================================
            // 4) 初始化语音后端
            // ================================================================
            try
            {
                Speech.Init(A11yHost.Log);
                if (Speech.Current == Speech.Backend.None)
                    Logger.LogWarning("语音不可用: " + Speech.LastError);
                else
                    Logger.LogInfo("语音后端就绪: " + Speech.BackendName);
            }
            catch (Exception e)
            {
                Logger.LogWarning("语音初始化异常: " + e.Message);
            }

            // ================================================================
            // 5) 挂补丁
            // ================================================================
            _harmony = new Harmony(Guid);
            try
            {
                _harmony.PatchAll(typeof(Patches));
                Logger.LogInfo("Harmony 补丁已应用。");
            }
            catch (Exception e)
            {
                // ★ 注意：PatchAll 抛异常时**其他补丁仍然生效**。
                //   症状极隐蔽：只有一个功能不工作。所以这里必须 LogError 而不是吞掉。
                Logger.LogError("Harmony 补丁失败: " + e);
            }
        }

        private bool _updateErrorLogged;

        private void Update()
        {
            try
            {
                if (A11yHost.TickUiNav != null) A11yHost.TickUiNav();
            }
            catch (Exception e)
            {
                // 每帧的异常不能刷屏：只记一次
                if (!_updateErrorLogged)
                {
                    _updateErrorLogged = true;
                    Logger.LogError("[Plugin] Update 异常: " + e);
                }
            }
        }

        private void OnDestroy()
        {
            try { Speech.Shutdown(); } catch { }
            try { _harmony?.UnpatchSelf(); } catch { }
            try { A11yHost.Clear(); } catch { }
        }
    }

    // ========================================================================
    // 补丁点
    //
    // ★ 两条最容易踩的坑：
    //   G1  迭代器方法**绝不能**用「Prefix 返回 false 跳过」—— 它会返回 null，
    //       而调用方是 StartCoroutine(...)。只能改 ref 参数。
    //   G2  Harmony 前缀的**参数名必须和运行时一致**，不是和反编译结果一致。
    //       写错名字会让 PatchAll 抛异常，但其他补丁仍然生效 —— 症状极隐蔽。
    //       拿不准就用 [HarmonyArgument(n)] 按**位置**取，位置下标没有这个问题。
    // ========================================================================
    internal static class Patches
    {
        // TODO 逐个补丁点，**每一个都要先在 probe_full_dump.log 里确认存在**。
        //
        // 例（照抄形状，方法名换成本作反编译实查的结果）：
        //
        // [HarmonyPatch(typeof(<本作类型>), nameof(<本作方法>))]
        // [HarmonyPrefix]
        // private static void <本作方法>_Pre(<本作类型> scene, [HarmonyArgument(2)] bool isInstant)
        // {
        //     Reader.NoteScene(scene, isInstant);
        // }
    }
}
