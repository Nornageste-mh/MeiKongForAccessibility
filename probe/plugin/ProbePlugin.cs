using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ShiMeng.DialogueV2;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MeiKongA11yProbe
{
    /// <summary>
    /// 《妹控计划》无障碍补丁 · P1 实机探针（只读观测 + 可选自动跳转）。
    ///
    /// 产出（都在游戏目录的 BepInEx 下）：
    ///   probe_meikong.log       快照 + 挂钩事件 + 补丁点存在性
    ///   probe_scene_tree.txt    运行时场景树（对象名/组件/控件标签）
    ///   LogOutput.log           BepInEx 日志（**含台词原文，绝不入库**）
    ///
    /// ★ IP 红线：本探针**不记录任何台词原文**。台词只记长度，选项只记条数/长度，
    ///   控件标签只记 ≤24 字的短串（按钮名）。场景树文件同样遵守。
    ///
    /// 自动跳转（替代不可靠的按键注入，见流水线 §5 的实机测试经验）：
    ///   MKPROBE_JUMP=story_3_教学时间   启动 N 秒后直接 StartScenario 指定段落
    ///   MKPROBE_JUMP_AT=30              何时跳（秒，默认 30）
    ///   MKPROBE_CHOOSE=2                选项一就绪就自动选第 N 项（默认 0=不选）
    /// </summary>
    [BepInPlugin(Guid, "妹控计划 A11y Probe", "0.0.0.1")]
    public class ProbePlugin : BaseUnityPlugin
    {
        public const string Guid = "meikong.a11y.probe";
        private static ManualLogSource L;
        private static string _logPath;
        private static string _treePath;
        private static float _startedAt;
        private static bool _sceneTreeDumped;

        internal static readonly List<Action> ChoiceActions = new List<Action>();

        private void Awake()
        {
            L = Logger;
            _logPath = Path.Combine(Paths.GameRootPath, "BepInEx", "probe_meikong.log");
            _treePath = Path.Combine(Paths.GameRootPath, "BepInEx", "probe_scene_tree.txt");
            _startedAt = Time.realtimeSinceStartup;
            Write(_logPath, "=== 《妹控计划》A11y Probe ===\n");
            Write(_treePath, "=== 运行时场景树 ===\n");

            Say("Awake: BepInEx 已挂载，游戏根目录 = " + Paths.GameRootPath);
            Say("Unity 版本 = " + Application.unityVersion + " | 平台 = " + Application.platform);

            try
            {
                var h = new Harmony(Guid);
                Type[] patchClasses = { typeof(ProbePatchesV2), typeof(ProbePatchesLegacy), typeof(ProbePatchesEventSystem) };
                foreach (var pc in patchClasses)
                {
                    try { h.PatchAll(pc); Say("Harmony PatchAll " + pc.Name + " : OK"); }
                    catch (Exception ex) { Say("Harmony PatchAll " + pc.Name + " : 失败 — " + ex.GetType().Name + ": " + ex.Message); }
                }
            }
            catch (Exception e) { Say("Harmony 异常: " + e); }

            ReportPatchTargets();
            ReportBuildScenes();
            StartCoroutine(Loop());
        }

        private static void Write(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); } catch { }
        }

        internal static void Say(string msg)
        {
            try { L?.LogInfo(msg); } catch { }
            try { File.AppendAllText(_logPath, msg + "\n", new UTF8Encoding(false)); } catch { }
        }

        internal static void Tree(string msg)
        {
            try { File.AppendAllText(_treePath, msg + "\n", new UTF8Encoding(false)); } catch { }
        }

        internal static string Shape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(空)";
            return "(len=" + s.Length + ")";
        }

        /// <summary>控件标签：短串（按钮名/标题）保留，长文本一律只记长度。</summary>
        internal static string Label(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Replace("\n", " ").Replace("\r", " ").Trim();
            if (t.Length == 0) return "";
            if (t.Length > 24) return "(长文本 len=" + t.Length + ")";
            return "\"" + t + "\"";
        }

        // ==================================================================
        // 构建里的场景清单（判定旧系统到底还有没有入口）
        // ==================================================================
        private static void ReportBuildScenes()
        {
            Say("");
            Say("################ 构建场景清单 ################");
            try
            {
                int n = SceneManager.sceneCountInBuildSettings;
                Say("sceneCountInBuildSettings = " + n);
                for (int i = 0; i < n; i++)
                {
                    string p = SceneUtility.GetScenePathByBuildIndex(i);
                    Say("  [" + i + "] " + p);
                }
            }
            catch (Exception e) { Say("读场景清单失败: " + e.Message); }
        }

        // ==================================================================
        // 补丁点存在性
        // ==================================================================
        private static void ReportPatchTargets()
        {
            Say("");
            Say("################ 补丁点存在性 ################");
            Type[] types =
            {
                typeof(DialoguePlaybackTracker), typeof(DialogueV2Runner), typeof(DialogueChoiceItemView),
                typeof(DialogueBoxView), typeof(DialogueUIManager), typeof(DialogueSceneManager),
                typeof(Ending2DialogueManager), typeof(PhoneDialogueManager), typeof(UIManager),
                typeof(PlayerState), typeof(PersistentDataManager),
            };
            foreach (var t in types) SayType(t, true);

            foreach (var name in new[]
            {
                "ShiMeng.DialogueV2.DialogueEntryManager", "ShiMeng.DialogueV2.MainStoryController",
                "ShiMeng.DialogueV2.DialogueEndIconManager", "StartScene", "SettingsTabManager",
                "VolumeManager", "SaveUIManager", "PetUiIntegrationHub", "PetPanelRegistry",
                "PetFeaturePanelPresentation", "DialogueDebugPanelController", "HistoryManager"
            })
            {
                var t = typeof(DialogueLine).Assembly.GetType(name);
                if (t == null) { Say("[type] " + name + " : 未找到"); continue; }
                SayType(t, false);
            }
        }

        private static void SayType(Type t, bool full)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var ms = t.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name).ToList();
            Say("[type] " + t.FullName + "  (方法 " + ms.Count + " 个)");
            if (!full) { Say("        " + string.Join(" / ", ms.Select(m => m.Name).Distinct().Take(40))); return; }
            foreach (var m in ms)
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                Say("        " + (m.IsPublic ? "public " : (m.IsPrivate ? "private " : "internal "))
                    + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
            }
        }

        // ==================================================================
        // 周期快照 + 自动跳转
        // ==================================================================
        private IEnumerator Loop()
        {
            int n = 0;
            string jump = Environment.GetEnvironmentVariable("MKPROBE_JUMP");
            float jumpAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_JUMP_AT"), 30f);
            bool jumped = false;
            while (true)
            {
                yield return new WaitForSecondsRealtime(n < 12 ? 5f : 20f);
                n++;
                try { Snapshot(n); } catch (Exception e) { Say("Snapshot 异常: " + e.Message); }
                if (!_sceneTreeDumped && n >= 4) { try { DumpScenes("首次"); _sceneTreeDumped = true; } catch (Exception e) { Say("DumpScenes 异常: " + e.Message); } }
                if (!jumped && !string.IsNullOrEmpty(jump) && (Time.realtimeSinceStartup - _startedAt) >= jumpAt)
                {
                    jumped = true;
                    try { DoJump(jump); } catch (Exception e) { Say("自动跳转失败: " + e.Message); }
                    DumpScenes("跳转后");
                }
            }
        }

        private static float ParseFloat(string s, float d)
        {
            float v;
            return float.TryParse(s, out v) ? v : d;
        }

        private static void DoJump(string scenarioId)
        {
            var runner = UnityEngine.Object.FindObjectOfType<DialogueV2Runner>(true);
            if (runner == null) { Say("[自动跳转] 找不到 DialogueV2Runner"); return; }
            bool ok = runner.StartScenario(scenarioId);
            Say("[自动跳转] StartScenario(\"" + scenarioId + "\") = " + ok);
        }

        private static void Snapshot(int n)
        {
            int t = (int)(Time.realtimeSinceStartup - _startedAt);
            var sb = new StringBuilder();
            sb.Append("[快照 #").Append(n).Append(" t+").Append(t).Append("s] ");
            sb.Append("scene=").Append(SceneManager.GetActiveScene().name);

            sb.Append(" | V2{playing=").Append(DialoguePlaybackTracker.IsPlaying);
            sb.Append(" box=").Append(DialoguePlaybackTracker.BoxMode);
            sb.Append(" story=").Append(DialoguePlaybackTracker.IsStoryModeActive);
            sb.Append(" scenario=").Append(DialoguePlaybackTracker.ScenarioId ?? "-");
            sb.Append(" node=").Append(DialoguePlaybackTracker.NodeType);
            sb.Append(" idx=").Append(DialoguePlaybackTracker.LineIndex);
            sb.Append(" text=").Append(Shape(DialoguePlaybackTracker.LineText));
            sb.Append(" voice=").Append(string.IsNullOrEmpty(DialoguePlaybackTracker.VoiceAddress) ? "无" : "有");
            sb.Append("}");

            var dsm = DialogueSceneManager.Instance;
            var e2 = Ending2DialogueManager.Instance;
            sb.Append(" | 旧{dsm=").Append(dsm != null);
            if (dsm != null)
            {
                sb.Append(" sel=").Append(dsm.isSelectionActive).Append(" rt=").Append(dsm.isRealTimeActive)
                  .Append(" phone=").Append(dsm.isPhoneActive).Append(" auto=").Append(dsm.isAutoPlayActive)
                  .Append(" ff=").Append(dsm.isFastForward);
            }
            sb.Append(" e2=").Append(e2 != null).Append("}");

            var es = EventSystem.current;
            sb.Append(" | EventSystem{");
            if (es == null) sb.Append("null");
            else
            {
                sb.Append("sendNav=").Append(es.sendNavigationEvents);
                sb.Append(" selected=").Append(es.currentSelectedGameObject == null ? "无" : es.currentSelectedGameObject.name);
            }
            sb.Append("}");

            var sels = UnityEngine.Object.FindObjectsOfType<Selectable>(true);
            var tmp = UnityEngine.Object.FindObjectsOfType<TMP_Text>(true);
            sb.Append(" | Selectable=").Append(sels.Count(s => s != null && s.IsActive() && s.IsInteractable()));
            sb.Append("/").Append(sels.Length);
            sb.Append(" TMP=").Append(tmp.Count(x => x != null && x.gameObject.activeInHierarchy && !string.IsNullOrEmpty(x.text)));
            sb.Append("/").Append(tmp.Length);

            Say(sb.ToString());
        }

        // ==================================================================
        // 场景树（写到独立文件，避免把事件日志冲掉）
        // ==================================================================
        private static void DumpScenes(string tag)
        {
            Tree("");
            Tree("################ 场景树转储（" + tag + " t+" + (int)(Time.realtimeSinceStartup - _startedAt) + "s）################");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                Tree("===== Scene[" + i + "] " + sc.name + " (rootObjects=" + sc.rootCount + ") =====");
                foreach (var go in sc.GetRootGameObjects()) Walk(go.transform, 0);
            }
            Tree("---- 活着的 MonoBehaviour 类型（Top50）----");
            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
            foreach (var g in all.Where(m => m != null).GroupBy(m => m.GetType().Name)
                                 .OrderByDescending(g => g.Count()).Take(50))
                Tree("  " + g.Key + " x" + g.Count());
        }

        private static void Walk(Transform t, int depth)
        {
            if (depth > 7) return;
            var go = t.gameObject;
            var sb = new StringBuilder();
            sb.Append(new string(' ', depth * 2));
            sb.Append(go.activeSelf ? "+ " : "- ");
            sb.Append(t.name);
            var comps = go.GetComponents<Component>().Where(c => c != null)
                          .Select(c => c.GetType().Name)
                          .Where(x => x != "Transform" && x != "RectTransform" && x != "CanvasRenderer")
                          .ToList();
            if (comps.Count > 0) sb.Append("  [").Append(string.Join(",", comps)).Append("]");
            var sel = go.GetComponent<Selectable>();
            if (sel != null) sb.Append(" selectable=").Append(sel.IsInteractable());
            var cg = go.GetComponent<CanvasGroup>();
            if (cg != null) sb.Append(" cg(alpha=").Append(cg.alpha.ToString("0.##"))
                              .Append(",ray=").Append(cg.blocksRaycasts)
                              .Append(",int=").Append(cg.interactable).Append(")");
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp != null) { string lb = Label(tmp.text); if (lb.Length > 0) sb.Append(" text=").Append(lb); }
            var ut = go.GetComponent<Text>();
            if (ut != null) { string lb = Label(ut.text); if (lb.Length > 0) sb.Append(" legacyText=").Append(lb); }
            Tree(sb.ToString());
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
        }
    }

    // ======================================================================
    // 观测用补丁（分三类，各自 PatchAll，一类失败不拖垮其余）
    // ======================================================================
    internal static class ProbePatchesV2
    {
        private const string T = "[hook] ";

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyLineChanged))]
        [HarmonyPostfix]
        private static void LineChanged_Post(DialogueLine line, int listPosition)
        {
            if (line == null) return;
            ProbePlugin.Say(T + "NotifyLineChanged type=" + line.Type + " idx=" + line.Index
                + " pos=" + listPosition + " scenario=" + (line.ScenarioId ?? "-")
                + " text=" + ProbePlugin.Shape(line.Text)
                + " voice=" + (string.IsNullOrEmpty(line.VoiceAddress) ? "无" : "有")
                + " spine=" + (string.IsNullOrEmpty(line.SpineAnimation) ? "-" : line.SpineAnimation)
                + " extra=" + ProbePlugin.Shape(line.ExtraParams)
                + " to=" + line.ToNumber);
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyChoiceWaiting))]
        [HarmonyPostfix]
        private static void ChoiceWaiting_Post(int firstChoiceIndex, int choiceCount)
        {
            ProbePlugin.ChoiceActions.Clear();
            ProbePlugin.Say(T + "NotifyChoiceWaiting first=" + firstChoiceIndex + " count=" + choiceCount);
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyScenarioStarted))]
        [HarmonyPostfix]
        private static void ScenarioStarted_Post(string scenarioId, DialogueBoxMode boxMode, string jsonSourceName, int segmentLineCount)
        {
            ProbePlugin.Say(T + "NotifyScenarioStarted scenario=" + scenarioId + " box=" + boxMode
                + " src=" + jsonSourceName + " lines=" + segmentLineCount);
        }

        [HarmonyPatch(typeof(DialoguePlaybackTracker), nameof(DialoguePlaybackTracker.NotifyScenarioEnded))]
        [HarmonyPostfix]
        private static void ScenarioEnded_Post(string scenarioId)
        {
            ProbePlugin.Say(T + "NotifyScenarioEnded scenario=" + scenarioId);
        }

        [HarmonyPatch(typeof(DialogueChoiceItemView), nameof(DialogueChoiceItemView.Bind))]
        [HarmonyPostfix]
        private static void Bind_Post(string text, Action onChosen)
        {
            ProbePlugin.ChoiceActions.Add(onChosen);
            ProbePlugin.Say(T + "ChoiceItem.Bind #" + ProbePlugin.ChoiceActions.Count
                + " label=" + ProbePlugin.Shape(text) + " callback=" + (onChosen == null ? "null" : "有"));
        }

        [HarmonyPatch(typeof(DialogueChoiceItemView), nameof(DialogueChoiceItemView.SetInteractable))]
        [HarmonyPostfix]
        private static void SetInteractable_Post(bool interactable)
        {
            ProbePlugin.Say(T + "ChoiceItem.SetInteractable " + interactable
                + " 已收集选项=" + ProbePlugin.ChoiceActions.Count);
            if (!interactable) return;
            int pick = 0;
            int.TryParse(Environment.GetEnvironmentVariable("MKPROBE_CHOOSE"), out pick);
            if (pick > 0 && pick <= ProbePlugin.ChoiceActions.Count)
            {
                ProbePlugin.Say(T + "自动选择第 " + pick + " 项");
                try { ProbePlugin.ChoiceActions[pick - 1]?.Invoke(); }
                catch (Exception e) { ProbePlugin.Say(T + "自动选择异常: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(DialogueV2Runner), "PlayVoiceForLine")]
        [HarmonyPostfix]
        private static void PlayVoice_Post(DialogueLine line, ref float __result)
        {
            ProbePlugin.Say(T + "PlayVoiceForLine voice=" + (line == null || string.IsNullOrEmpty(line.VoiceAddress) ? "无" : "有")
                + " 时长=" + __result.ToString("0.##") + "s");
        }

        [HarmonyPatch(typeof(DialogueBoxView), nameof(DialogueBoxView.StartTypewriter))]
        [HarmonyPostfix]
        private static void StartTypewriter_Post(string fullText, float charsPerSecond)
        {
            ProbePlugin.Say(T + "DialogueBoxView.StartTypewriter text=" + ProbePlugin.Shape(fullText)
                + " cps=" + charsPerSecond.ToString("0.#"));
        }
    }

    internal static class ProbePatchesLegacy
    {
        private const string T = "[hook] ";

        [HarmonyPatch(typeof(DialogueSceneManager), "TypeText")]
        [HarmonyPrefix]
        private static void DsmTypeText_Pre(string text)
        {
            ProbePlugin.Say(T + "DialogueSceneManager.TypeText text=" + ProbePlugin.Shape(text));
        }

        [HarmonyPatch(typeof(Ending2DialogueManager), "TypeText")]
        [HarmonyPrefix]
        private static void E2TypeText_Pre(string text)
        {
            ProbePlugin.Say(T + "Ending2DialogueManager.TypeText text=" + ProbePlugin.Shape(text));
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "GenerateReplyButton")]
        [HarmonyPostfix]
        private static void GenReply_Post(DialogueScene scene)
        {
            ProbePlugin.Say(T + "GenerateReplyButton scene=" + (scene == null ? "null" : scene.Number.ToString())
                + " choice=" + ProbePlugin.Shape(scene?.ChoiceText) + " to=" + scene?.ToNumber);
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "GenerateSelectionButton")]
        [HarmonyPostfix]
        private static void GenSelection_Post(DialogueScene scene, int index)
        {
            ProbePlugin.Say(T + "GenerateSelectionButton #" + index
                + " choice=" + ProbePlugin.Shape(scene?.ChoiceText) + " to=" + scene?.ToNumber);
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "ExecuteRealTimeLogic")]
        [HarmonyPrefix]
        private static void RealTime_Pre(DialogueScene currentScene)
        {
            ProbePlugin.Say(T + "ExecuteRealTimeLogic scene=" + (currentScene == null ? "null" : currentScene.Number.ToString()));
        }

        [HarmonyPatch(typeof(DialogueSceneManager), "AutoDestroyAfterTime")]
        [HarmonyPrefix]
        private static void AutoDestroy_Pre(ref float delay)
        {
            ProbePlugin.Say(T + "AutoDestroyAfterTime delay=" + delay.ToString("0.##") + "s");
        }

        [HarmonyPatch(typeof(PhoneDialogueManager), "AddMessage")]
        [HarmonyPrefix]
        private static void PhoneAdd_Pre(string text, bool isPlayer)
        {
            ProbePlugin.Say(T + "PhoneDialogueManager.AddMessage isPlayer=" + isPlayer + " text=" + ProbePlugin.Shape(text));
        }
    }

    internal static class ProbePatchesEventSystem
    {
        private const string T = "[hook] ";

        [HarmonyPatch(typeof(EventSystem), nameof(EventSystem.SetSelectedGameObject),
            new Type[] { typeof(GameObject), typeof(BaseEventData) })]
        [HarmonyPostfix]
        private static void SetSelected_Post(GameObject selected)
        {
            ProbePlugin.Say(T + "EventSystem.SetSelectedGameObject -> " + (selected == null ? "null" : selected.name)
                + " 调用栈=" + BriefStack());
        }

        private static string BriefStack()
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                var parts = new List<string>();
                for (int i = 0; i < st.FrameCount && parts.Count < 4; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    if (m == null || m.DeclaringType == null) continue;
                    string tn = m.DeclaringType.Name;
                    if (tn.StartsWith("EventSystem") || tn.StartsWith("StandaloneInputModule")) continue;
                    parts.Add(tn + "." + m.Name);
                }
                return string.Join(" <- ", parts);
            }
            catch { return "?"; }
        }
    }
}
