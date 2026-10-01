using System;
using HarmonyLib;
using ShiMeng.DialogueV2;

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
                try { Reader.OnLine(line); } catch (Exception e) { A11yHost.Diag("[挂钩] LineChanged 异常: " + e.Message); }
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
                try { Choices.Clear(); } catch { }
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
    }
}
