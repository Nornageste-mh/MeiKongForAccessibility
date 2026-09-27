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
    }
}
