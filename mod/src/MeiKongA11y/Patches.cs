using System;
using HarmonyLib;
using ShiMeng.DialogueV2;
using UnityEngine;
using UnityEngine.EventSystems;

namespace MeiKongA11y
{
    /// <summary>
    /// Harmony 补丁点。**方法名与参数名全部来自反编译实查 + 探针运行时确认**（流水线铁律 1）。
    ///
    /// 三条本作特有的纪律：
    ///   · 参数名必须与**运行时**一致，不是与反编译结果一致（流水线 G2）。
    ///     本作已经踩过一次：`PhoneDialogueManager.AddMessage` 的第一个参数运行时叫
    ///     `messageText`，写成 `text` 会让 PatchAll 抛异常 —— 而且**其余补丁照常生效**，
    ///     症状是「随机某个功能不工作」，极难定位（P0 §4.3 E7）。
    ///   · 补丁体一律 try/catch 吞异常：无障碍层绝不能让游戏崩（流水线 §5.1）。
    ///   · 只读 + 只广播，不改游戏行为；`ref` 参数一个都不动。
    /// </summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyLineChanged))]
        internal static class LineChanged
        {
            [HarmonyPostfix]
            private static void Post(DialogueLine line, int listPosition)
            {
                try { Reader.OnLine(line); Focus.OnStoryLine(); } catch (Exception e) { A11yHost.Diag("[挂钩] LineChanged 异常: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyScenarioStarted))]
        internal static class ScenarioStarted
        {
            [HarmonyPostfix]
            private static void Post(string scenarioId)
            {
                try { Reader.OnScenarioStarted(scenarioId); Choices.Clear(); }
                catch (Exception e) { A11yHost.Diag("[挂钩] ScenarioStarted 异常: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyScenarioEnded))]
        internal static class ScenarioEnded
        {
            [HarmonyPostfix]
            private static void Post(string scenarioId)
            {
                try { Reader.OnScenarioEnded(scenarioId); Choices.Clear(); }
                catch { }
            }
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyChoiceWaiting))]
        internal static class ChoiceWaiting
        {
            [HarmonyPostfix]
            private static void Post(int firstChoiceIndex, int choiceCount)
            {
                try { Choices.Begin(choiceCount); } catch { }
            }
        }

        [HarmonyPatch(typeof(DialogueChoiceItemView), nameof(DialogueChoiceItemView.Bind))]
        internal static class ChoiceBind
        {
            [HarmonyPostfix]
            private static void Post(string text, Action onChosen)
            {
                try { Choices.Add(text, onChosen); } catch { }
            }
        }

        [HarmonyPatch(typeof(DialogueChoiceItemView), nameof(DialogueChoiceItemView.SetInteractable))]
        internal static class ChoiceInteractable
        {
            [HarmonyPostfix]
            private static void Post(bool interactable)
            {
                try { if (interactable) Choices.Ready(); } catch { }
            }
        }
        // ==============================================================
        // 以下两个补丁点**会改变游戏行为**，是本作仅有的两处「不是只读」的钩子。
        // 两处都只拦一个明确的入口，都不复制游戏逻辑 —— 理由各自写在下面。
        // ==============================================================

        /// <summary>
        /// **屏蔽游戏原生的 F1 开发者测试键。**
        ///
        /// 这不是补丁的锅，是游戏自己的残留 —— 反编译实查
        /// <c>DialogueV2Runner.Update()</c> 的**整个方法体**只有一句：
        ///
        /// <code>
        /// if (Input.GetKeyDown(KeyCode.F1))
        ///     StartScenario(defaultScenarioId, ResolveBoxMode(defaultScenarioId),
        ///                   DialogueTriggerContext.RunnerTest());
        /// </code>
        ///
        /// <c>defaultScenarioId = "Story_MorningGreeting"</c>（开局第一段），
        /// 而那个触发器工厂的签名是 <c>RunnerTest(string detail = "F1 测试键")</c> ——
        /// 名字已经把话说尽了。
        ///
        /// === 为什么拦在 StartScenario 而不是 Update ===
        ///
        /// 拦 <c>Update</c>（前缀返回 false）等于**整段跳过**它：现在它的方法体只有那一句，
        /// 但将来游戏往里面加任何一行（语音轮询、状态维护…），我们就会连同那一行一起吞掉 ——
        /// 症状是「游戏某个功能莫名不工作」，最难查的那一类。
        /// 拦 <c>StartScenario</c> 的 <c>RunnerTest</c> 触发器则是**语义精确**的：
        /// 只拒绝「开发者测试入口」这一个来源，游戏正常的剧情一行都不碰。
        /// 真要是哪天这个入口没了，这个前缀就永远不会命中 —— 自然失效，不留后患。
        ///
        /// <c>StartScenario</c> 有三个重载，只有三参数那个带 <c>DialogueTriggerContext</c>；
        /// 另两个都转调它，所以补这一个就够（必须写参数类型，否则 Harmony 报重载歧义）。
        /// </summary>
        [HarmonyPatch(typeof(DialogueV2Runner), nameof(DialogueV2Runner.StartScenario),
                      new[] { typeof(string), typeof(DialogueBoxMode), typeof(DialogueTriggerContext) })]
        internal static class RunnerTestKey
        {
            [HarmonyPrefix]
            private static bool Pre(DialogueTriggerContext trigger)
            {
                try
                {
                    if (Plugin.CfgBlockRunnerTestKey != null && !Plugin.CfgBlockRunnerTestKey.Value) return true;
                    if (trigger.Category != DialogueTriggerCategory.RunnerTest) return true;
                    A11yHost.Diag("[补丁] 拦下游戏原生的 F1 测试键（RunnerTest 触发器，不打断剧情）");
                    return false;
                }
                catch { return true; }   // 出任何岔子都放行 —— 无障碍层绝不替游戏做决定
            }
        }

        /// <summary>
        /// **返回键（ESC / 右键）的本帧裁决点。**
        ///
        /// <c>UIManager.Update()</c> 是游戏自己的「返回」入口：
        /// <code>if ((Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Escape))
        ///         &amp;&amp; !isInTransition &amp;&amp; !BlockBackInput) CloseOrGoBack();</code>
        ///
        /// 而小游戏窗口是**桌宠面板系统**（PetUiIntegrationHub / PetPanelRegistry）开的，
        /// 根本不在 <c>UIManager</c> 的返回栈上 —— 所以 ESC 不但关不掉它，
        /// 万一将来某个面板上了那个栈，ESC 还会去关**别的东西**。
        /// 前缀返回 false 就是「这一帧的返回输入我们已经处理了，游戏别看」：
        /// 只跳过这一个方法的一帧（它的方法体只有返回输入 + 队列出队），
        /// 而且**只在真的吃下输入的那一帧**才跳过。
        ///
        /// 注意：探针实查运行时的场景里**没有 UIManager 实例**，所以这个钩子平时根本不会执行；
        /// 真正干活的是 <see cref="Minigame.InterceptBackInput"/> 在每帧输入分发里的那次调用。
        /// 留着它是因为**帧序不可假设** —— 万一 UIManager 存在且先于我们跑，
        /// 同一个状态机（帧内去重）也能把这一帧的 ESC 拦下来。
        /// </summary>
        // 写字符串而不是 nameof：MonoBehaviour 的 Update 是**私有**方法，
        // 跨类取 nameof 会被可访问性挡住（CS0117），编译期就过不去。
        [HarmonyPatch(typeof(UIManager), "Update")]
        internal static class GameBackInput
        {
            [HarmonyPrefix]
            private static bool Pre()
            {
                try { return !Minigame.InterceptBackInput(); }
                catch (Exception e) { A11yHost.Diag("[挂钩] 返回键拦截异常: " + e.Message); return true; }
            }
        }

        // ==============================================================
        // 以下补丁点是 0.2.7.2「导航模式 → 游戏自己的输入/焦点层」迁移的核心。
        //
        // 全部只做两件事：①在游戏自己「面板打开/关闭」的那一刻交出/收回焦点；
        // ②在游戏自己的提交/推进通路上让开一帧，避免一次按键被处理两遍。
        // 没有一个补丁自己维护控件列表、顺序或索引（那正是旧 UiNav 的 X2/G62 违规点）。
        // ==============================================================

        /// <summary>
        /// 桌宠面板打开（<c>PetPanelRegistry.OpenPanel(string)</c>，15 个面板的唯一入口）。
        /// 用游戏自己的 <c>TryGetPanelRoot</c> 拿面板根 —— 不扫屏、不猜对象名。
        /// </summary>
        [HarmonyPatch(typeof(PetPanelRegistry), nameof(PetPanelRegistry.OpenPanel))]
        internal static class PetPanelOpened
        {
            [HarmonyPostfix]
            private static void Post(PetPanelRegistry __instance, string panelId)
            {
                try
                {
                    GameObject root;
                    if (!__instance.TryGetPanelRoot(panelId, out root) || root == null) return;
                    Focus.Enter(root.transform, "", Focus.Kind.PetPanel, panelId);  // 标题交给 PanelMap（游戏自己画在界面上的名字）
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] OpenPanel 异常: " + e.Message); }
            }
        }

        /// <summary>
        /// 绕过 <c>OpenPanel</c> 直接走 <c>PresentOpen</c> 的入口（例如「语音收藏」：
        /// <c>PetUiIntegrationHub.OpenVoiceCollect</c> 只调 <c>PanelRegistry.PresentOpen(root)</c>）。
        /// 面板 id 用根节点的对象名反查（PanelMap.KeyOf）。
        /// </summary>
        [HarmonyPatch(typeof(PetPanelRegistry), nameof(PetPanelRegistry.PresentOpen))]
        internal static class PetPanelPresented
        {
            [HarmonyPostfix]
            private static void Post(GameObject panelRoot)
            {
                try
                {
                    if (panelRoot == null) return;
                    string key = PanelMap.KeyOf(panelRoot.name);
                    if (string.IsNullOrEmpty(key)) return;
                    Focus.Enter(panelRoot.transform, "", Focus.Kind.PetPanel, key);
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] PresentOpen 异常: " + e.Message); }
            }
        }

        /// <summary>对应的「直接隐藏」入口（语音收藏走 <c>HideVoiceCollectInstant</c> → <c>PresentClose</c>）。</summary>
        [HarmonyPatch(typeof(PetPanelRegistry), nameof(PetPanelRegistry.PresentClose))]
        internal static class PetPanelPresentedClosed
        {
            [HarmonyPostfix]
            private static void Post(GameObject panelRoot)
            {
                try
                {
                    if (panelRoot == null || !Focus.IsPanelActive) return;
                    string key = PanelMap.KeyOf(panelRoot.name);
                    if (string.IsNullOrEmpty(key)) return;
                    if (string.Equals(Focus.DomainId, key, StringComparison.OrdinalIgnoreCase)) Focus.Leave();
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] PresentClose 异常: " + e.Message); }
            }
        }

        /// <summary>桌宠面板关闭：焦点交还，并清掉选中项（否则关面板后回车会点到一个看不见的按钮）。</summary>
        [HarmonyPatch(typeof(PetPanelRegistry), nameof(PetPanelRegistry.ClosePanel))]
        internal static class PetPanelClosed
        {
            [HarmonyPostfix]
            private static void Post(string panelId)
            {
                try
                {
                    if (Focus.Domain == Focus.Kind.PetPanel
                        && string.Equals(Focus.DomainId, panelId, StringComparison.OrdinalIgnoreCase))
                        Focus.Leave();
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] ClosePanel 异常: " + e.Message); }
            }
        }

        /// <summary>
        /// 对话内面板打开（<c>UIManager.OpenUI(GameObject, List&lt;GameObject&gt;)</c>，14 个面板的唯一入口）。
        /// OpenUI 只是把操作入队，真正的淡入在协程里 —— 所以这里立刻建域，
        /// 首焦点由 Focus 在面板淡入完成后自动补（CanvasGroup.alpha 未满时设焦点会被 uGUI 吞掉）。
        /// </summary>
        [HarmonyPatch(typeof(UIManager), nameof(UIManager.OpenUI))]
        internal static class UiPanelOpened
        {
            [HarmonyPostfix]
            private static void Post(GameObject uiPanel)
            {
                try
                {
                    if (uiPanel == null) return;
                    Focus.Enter(uiPanel.transform, "", Focus.Kind.UiManager, "");
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] OpenUI 异常: " + e.Message); }
            }
        }

        /// <summary>
        /// 返回（<c>UIManager.CloseOrGoBack()</c>，游戏自己的「关面板/后退」唯一入口）。
        /// 出栈在协程里做，所以只标记「等它关完，焦点回栈顶」—— 栈本身是游戏的 uiStack，我们只读。
        /// </summary>
        [HarmonyPatch(typeof(UIManager), nameof(UIManager.CloseOrGoBack))]
        internal static class UiPanelClosed
        {
            [HarmonyPostfix]
            private static void Post()
            {
                try
                {
                    if (Focus.Domain == Focus.Kind.UiManager) Focus.LeaveWithRestore(1.2f);
                }
                catch (Exception e) { A11yHost.Diag("[挂钩] CloseOrGoBack 异常: " + e.Message); }
            }
        }

        /// <summary>
        /// uGUI 自己的提交通路：焦点归我们管的时候让开，避免「我们提交一次 + 模块再提交一次」。
        /// 未装补丁时选中项恒为 null（游戏全代码零处 SetSelectedGameObject），这个调用本来就 no-op；
        /// 装了之后只在域内接管，域外一行都不改。
        /// </summary>
        [HarmonyPatch(typeof(StandaloneInputModule), "SendSubmitEventToSelectedObject")]
        internal static class MuteUnitySubmit
        {
            [HarmonyPrefix]
            private static bool Pre() { return !Focus.Active; }
        }

        [HarmonyPatch(typeof(StandaloneInputModule), "SendMoveEventToSelectedObject")]
        internal static class MuteUnityMove
        {
            [HarmonyPrefix]
            private static bool Pre() { return !Focus.Active; }
        }

        /// <summary>
        /// **推进剧情的唯一入口**：<c>DialogueSceneManager.DialogueButtonClicked()</c>
        /// （鼠标点对话框 → 同一个方法；键盘 Space/回车/滚轮 → `:319` 也调它）。
        ///
        /// 为什么要在面板开着时拦它：游戏的键盘推进门控是
        /// <c>!IsSpecialModeActive()</c>，而 `IsSpecialModeActive()` 经 `isUIActive()` 只认
        /// mainUI / historyUI / galleryUI / optionsUI / jumpBPPanel / collectVoiceUI 六个面板。
        /// 存档、读档、音乐、手机历史等面板**不在**其中 —— 不拦的话，读屏玩家在存档面板里
        /// 按空格选槽位，会同时把背后的剧情推进一句。
        ///
        /// 只跳过这一个方法，且**只在面板焦点域存在时**跳过；域外（含主界面浏览域）行为与不装补丁完全一致。
        /// </summary>
        [HarmonyPatch(typeof(DialogueSceneManager), nameof(DialogueSceneManager.DialogueButtonClicked))]
        internal static class DialogueAdvanceGate
        {
            [HarmonyPrefix]
            private static bool Pre()
            {
                try
                {
                    // 只拦「面板开着」的情形；主界面浏览（Kind.Scene）不拦 —— 那时回车是去点图标，
                    // 而剧情推进归 KeyEdge 之外的 game 键处理，玩家按空格应当照常推进。
                    if (!Focus.IsPanelActive) return true;
                    // 剧情正在播时不能拦：tool 剧情（日程完成、番茄钟启动…）会在面板不关的情况下播，
                    // 玩家按空格/回车是要continue剧情，拦下来就成了「按了没反应」。
                    if (Reader.InStory) return true;
                    A11yHost.Diag("[挂钩] 面板焦点期间拦下剧情推进");
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
