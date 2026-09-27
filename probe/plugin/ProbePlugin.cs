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
            bool poked = false;
            float PokeAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_POKE_AT"), 0f);
            bool opened = false, navToggled = false, deskPetOn = false, exitTried = false, facesExported = false;
            float FacesAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_FACES_AT"), 0f);
            float MiniAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_MINI_AT"), 0f);
            bool repeated = false;
            float RepeatAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_REPEAT_AT"), 0f);
            int miniStep = 0;
            float DeskPetAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_DESKPET_AT"), 0f);
            float ExitAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_EXIT_AT"), 0f);
            string OpenPanel = Environment.GetEnvironmentVariable("MKPROBE_OPEN_PANEL") ?? "";
            float OpenAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_OPEN_AT"), 20f);
            float NavAt = ParseFloat(Environment.GetEnvironmentVariable("MKPROBE_NAV_AT"), 0f);
            while (true)
            {
                yield return new WaitForSecondsRealtime(n < 12 ? 5f : 20f);
                n++;
                try { Snapshot(n); } catch (Exception e) { Say("Snapshot 异常: " + e.Message); }
                if (!_sceneTreeDumped && n >= 4) { try { DumpScenes("首次"); _sceneTreeDumped = true; } catch (Exception e) { Say("DumpScenes 异常: " + e.Message); } }
                if (n == 6) { try { AuditPanels(); } catch (Exception e) { Say("AuditPanels 异常: " + e.Message); } }
                if (n == 7) { try { AuditInteraction(); } catch (Exception e) { Say("AuditInteraction 异常: " + e.Message); } }
                if (!poked && PokeAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= PokeAt)
                {
                    poked = true;
                    try { DoPoke(); } catch (Exception e) { Say("戳诗萌失败: " + e.Message); }
                }
                if (!opened && !string.IsNullOrEmpty(OpenPanel) && (Time.realtimeSinceStartup - _startedAt) >= OpenAt)
                {
                    opened = true;
                    try { OpenPanelByName(OpenPanel); } catch (Exception e) { Say("开面板失败: " + e.Message); }
                }
                if (MiniAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= MiniAt + miniStep * 4f && miniStep < 6)
                {
                    try { DriveMinigame(miniStep); } catch (Exception e) { Say("驱动小游戏失败: " + e.Message); }
                    miniStep++;
                }
                if (!repeated && RepeatAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= RepeatAt)
                {
                    repeated = true;
                    try { CallA11yRepeat(); } catch (Exception e) { Say("重读失败: " + e.Message); }
                }
                if (!facesExported && FacesAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= FacesAt)
                {
                    facesExported = true;
                    try { ExportCardFaces(); } catch (Exception e) { Say("导牌面失败: " + e.Message); }
                }
                if (!deskPetOn && DeskPetAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= DeskPetAt)
                {
                    deskPetOn = true;
                    try { SwitchMode("SetDeskPetMode", "桌宠模式"); } catch (Exception e) { Say("切桌宠失败: " + e.Message); }
                }
                if (!exitTried && ExitAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= ExitAt)
                {
                    exitTried = true;
                    try { CallA11yExitDeskPet(); } catch (Exception e) { Say("保命键失败: " + e.Message); }
                }
                if (!navToggled && NavAt > 0f && (Time.realtimeSinceStartup - _startedAt) >= NavAt)
                {
                    navToggled = true;
                    try { DoNavToggle(); } catch (Exception e) { Say("进导航失败: " + e.Message); }
                    try { DumpNavAll(); } catch (Exception e) { Say("导航空表转储失败: " + e.Message); }
                }
                if (!jumped && !string.IsNullOrEmpty(jump) && (Time.realtimeSinceStartup - _startedAt) >= jumpAt)
                {
                    jumped = true;
                    try { DoJump(jump); } catch (Exception e) { Say("自动跳转失败: " + e.Message); }
                    DumpScenes("跳转后");
                }
            }
        }

        // ==================================================================
        // 界面勘查：面板清单 / 功能按钮条 / 桌宠交互面
        // 这一节是为「盲人怎么跟控件交互、怎么玩小游戏」服务的，
        // 所以重点不是台词，而是「有哪些可操作的东西、它们有没有可读标签」。
        // ==================================================================
        private static void AuditPanels()
        {
            Say("");
            Say("################ 界面勘查 ################");
            var asm = typeof(DialogueLine).Assembly;

            // ---- 1) PetPanelRegistry 里的全部面板 ----
            var regType = asm.GetType("PetPanelRegistry");
            Say("[面板注册表] PetPanelRegistry = " + (regType == null ? "未找到" : "OK"));
            if (regType != null)
            {
                foreach (var inst in UnityEngine.Object.FindObjectsOfType(regType, true))
                {
                    var flds = regType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    foreach (var f in flds)
                    {
                        var dict = f.GetValue(inst) as System.Collections.IDictionary;
                        if (dict == null) continue;
                        Say("  字段 " + f.Name + " : " + dict.Count + " 个面板");
                        foreach (System.Collections.DictionaryEntry e in dict)
                        {
                            var go = e.Value as GameObject;
                            Say("    - " + e.Key + " -> " + Describe(go, 1));
                        }
                    }
                }
            }

            // ---- 2) 功能按钮条（button_*）----
            Say("[功能按钮] 名称以 button_ 开头的对象：");
            var all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            foreach (var t in all)
            {
                if (t == null || !t.name.StartsWith("button_", StringComparison.OrdinalIgnoreCase)) continue;
                Say("    - " + Chain(t) + " -> " + Describe(t.gameObject, 0));
            }

            // ---- 3) 桌宠交互面 ----
            Say("[桌宠交互面]");
            foreach (var tn in new[] { "PetDeskPetContextMenuController", "PetDeskPetHoverUiController", "PetDeskPetHoverReveal",
                                       "PetDeskPetDragController", "PetDisplayModeController", "PetUiSurfaceSwitcher" })
            {
                var ty = asm.GetType(tn);
                if (ty == null) { Say("    - " + tn + " : 未找到"); continue; }
                var insts = UnityEngine.Object.FindObjectsOfType(ty, true);
                Say("    - " + tn + " x" + insts.Length);
            }

            // ---- 4) 小游戏 ----
            foreach (var tn in new[] { "MemoryMatchUIView", "MemoryMatchPanelController", "MemeMakerPanelController" })
            {
                var ty = asm.GetType("ShiMeng.Minigames.MemoryMatch." + tn) ?? asm.GetType(tn);
                Say("    - " + tn + " : " + (ty == null ? "未找到" : "OK"));
            }

            // ---- 5) 全场景「有标签的按钮」总表（去重前 60 条）----
            Say("[全部 Button：对象名 -> 子树里第一个 TMP 文本]");
            int n = 0;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Button>(true))
            {
                if (b == null || n++ > 60) continue;
                Say("    - " + Chain(b.transform) + "  label=" + ProbePlugin.Label(SubtreeText(b.transform)));
            }

            // ---- 6) TMP_InputField 清单（数字/文本输入，盲人要能用）----
            Say("[TMP_InputField]");
            foreach (var inf in UnityEngine.Object.FindObjectsOfType<TMP_InputField>(true))
            {
                if (inf == null) continue;
                Say("    - " + Chain(inf.transform) + "  text=" + ProbePlugin.Label(inf.text));
            }
        }

        // ==================================================================
        // 交互面勘查：怎么戳诗萌、互动按钮有哪些、右键菜单里是什么、桌宠在哪儿
        // ==================================================================
        private static void AuditInteraction()
        {
            Say("");
            Say("################ 交互面勘查 ################");
            var asm = typeof(DialogueLine).Assembly;

            // ---- 1) 桌宠模式状态 + 诗萌在屏幕上的位置 ----
            var inputUtil = asm.GetType("PetDeskPetInputUtility");
            bool deskPet = false;
            try
            {
                var m = inputUtil?.GetMethod("IsDeskPetActive", BindingFlags.Public | BindingFlags.Static);
                if (m != null) deskPet = (bool)m.Invoke(null, null);
            }
            catch { }
            Say("[模式] IsDeskPetActive = " + deskPet);

            var dmType = asm.GetType("PetDisplayModeController");
            if (dmType != null)
            {
                foreach (var inst in UnityEngine.Object.FindObjectsOfType(dmType, true))
                {
                    object mode = null;
                    try
                    {
                        var pi = dmType.GetProperty("SurfaceMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?? dmType.GetProperty("CurrentMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (pi != null) mode = pi.GetValue(inst);
                    }
                    catch { }
                    Say("    PetDisplayModeController: SurfaceMode=" + (mode ?? "?"));
                }
            }

            // 诗萌本体：Spine GameObject (skeleton) 的屏幕位置
            var clickTargetType = asm.GetType("ShiMeng.DialogueV2.DialogueEntryClickTarget");
            if (clickTargetType != null)
            {
                foreach (var ct in UnityEngine.Object.FindObjectsOfType(clickTargetType, true))
                {
                    var comp = ct as Component;
                    if (comp == null) continue;
                    string tid = "";
                    try
                    {
                        var f = clickTargetType.GetField("targetId", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (f != null) tid = Convert.ToString(f.GetValue(comp));
                    }
                    catch { }
                    var col = comp.GetComponent<Collider2D>();
                    string pos = "?";
                    try
                    {
                        var cam = Camera.main;
                        if (cam != null && col != null && col.bounds.size.x > 0f)
                        {
                            var sp = cam.WorldToScreenPoint(col.bounds.center);
                            pos = "屏幕 (" + (int)sp.x + "," + (int)sp.y + ") 尺寸 " + (int)col.bounds.size.x + "x" + (int)col.bounds.size.y
                                + " 窗口 " + Screen.width + "x" + Screen.height;
                        }
                    }
                    catch { }
                    Say("    点击目标 targetId=" + tid + " " + pos);
                }
            }

            // ---- 2) 互动按钮（MainStoryTriggerButtonVariant）----
            var varType = asm.GetType("ShiMeng.DialogueV2.MainStoryTriggerButtonVariant");
            Say("[互动按钮] MainStoryTriggerButtonVariant x"
                + (varType == null ? "类型未找到" : UnityEngine.Object.FindObjectsOfType(varType, true).Length.ToString()));
            if (varType != null)
            {
                foreach (var v in UnityEngine.Object.FindObjectsOfType(varType, true))
                {
                    var comp = v as Component;
                    if (comp == null) continue;
                    string label = "";
                    try
                    {
                        var pi = varType.GetProperty("Label");
                        if (pi != null) label = Convert.ToString(pi.GetValue(v));
                    }
                    catch { }
                    var btn = comp.GetComponent<Button>();
                    string tmp = ProbePlugin.Label(SubtreeText(comp.transform));
                    Say("    - " + Chain(comp.transform) + " label=\"" + label + "\" tmp=" + tmp
                        + " active=" + comp.gameObject.activeInHierarchy
                        + " interactable=" + (btn != null ? btn.IsInteractable().ToString() : "无Button"));
                }
            }

            // ---- 3) 右键菜单（DeskPetContextMenu）----
            var ctxType = asm.GetType("PetDeskPetContextMenuController");
            if (ctxType != null)
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType(ctxType, true))
                {
                    var comp = c as Component;
                    if (comp == null) continue;
                    object open = null;
                    try { var pi = ctxType.GetProperty("IsOpen"); if (pi != null) open = pi.GetValue(c); } catch { }
                    Say("[右键菜单] " + Chain(comp.transform) + " IsOpen=" + open);
                    // 菜单根的子节点（可能是运行时解析的兄弟节点）
                    var menuRootF = ctxType.GetField("menuRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                    var menuRoot = menuRootF != null ? menuRootF.GetValue(c) as RectTransform : null;
                    var root = menuRoot != null ? menuRoot : comp.transform as RectTransform;
                    if (root != null)
                    {
                        Say("    菜单根 = " + Chain(root) + " 子节点 " + root.childCount + " 个");
                        for (int i = 0; i < root.childCount && i < 20; i++)
                        {
                            var ch = root.GetChild(i);
                            Say("      [" + i + "] " + ch.name + " " + Describe(ch.gameObject, 0)
                                + " label=" + ProbePlugin.Label(SubtreeText(ch)));
                        }
                    }
                }
            }

            // ---- 4) 悬停条 ----
            var hovType = asm.GetType("PetDeskPetHoverUiController");
            if (hovType != null)
            {
                foreach (var h in UnityEngine.Object.FindObjectsOfType(hovType, true))
                {
                    var comp = h as Component;
                    if (comp == null) continue;
                    Say("[悬停条] " + Chain(comp.transform) + " " + Describe(comp.gameObject, 0));
                }
            }

            // ---- 5) DialogueEntryManager 的条目结构（只读结构，不读剧本文本）----
            var demType = asm.GetType("ShiMeng.DialogueV2.DialogueEntryManager");
            Say("[入口管理器] DialogueEntryManager = " + (demType == null ? "未找到" : ("实例 " + UnityEngine.Object.FindObjectsOfType(demType, true).Length + " 个")));
            if (demType != null)
            {
                foreach (var f in demType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.Name.IndexOf("config", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Say("    字段 " + f.Name + " : " + f.FieldType.Name);
                }
            }
        }

        private static string Describe(GameObject go, int depth)
        {
            if (go == null) return "(null)";
            var cg = go.GetComponent<CanvasGroup>();
            var sels = go.GetComponentsInChildren<Selectable>(true);
            var tmps = go.GetComponentsInChildren<TMP_Text>(true);
            int liveSel = sels.Count(s => s != null && s.IsActive() && s.IsInteractable());
            int liveTmp = tmps.Count(t => t != null && t.gameObject.activeInHierarchy && !string.IsNullOrEmpty(t.text));
            var sb = new StringBuilder();
            sb.Append("activeSelf=").Append(go.activeSelf).Append(" activeInHier=").Append(go.activeInHierarchy);
            if (cg != null) sb.Append(" alpha=").Append(cg.alpha.ToString("0.##")).Append(" ray=").Append(cg.blocksRaycasts);
            sb.Append(" Selectable=").Append(liveSel).Append("/").Append(sels.Length);
            sb.Append(" TMP=").Append(liveTmp).Append("/").Append(tmps.Length);
            return sb.ToString();
        }

        private static string Chain(Transform t)
        {
            var sb = new StringBuilder();
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 6)
            {
                if (sb.Length > 0) sb.Insert(0, "/");
                sb.Insert(0, cur.name);
                cur = cur.parent;
            }
            return sb.ToString();
        }

        private static string SubtreeText(Transform t)
        {
            var tmp = t.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
            var ut = t.GetComponentInChildren<Text>(true);
            return ut != null ? ut.text : "";
        }

        private static float ParseFloat(string s, float d)
        {
            float v;
            return float.TryParse(s, out v) ? v : d;
        }

        /// <summary>戳一下诗萌：等价于鼠标左键点她（走游戏自己的 DialogueEntryClickTarget.FireClick）。</summary>
        private static void DoPoke()
        {
            var t = typeof(DialogueLine).Assembly.GetType("ShiMeng.DialogueV2.DialogueEntryClickTarget");
            if (t == null) { Say("[戳诗萌] 找不到 DialogueEntryClickTarget"); return; }
            var arr = UnityEngine.Object.FindObjectsOfType(t, true);
            if (arr == null || arr.Length == 0) { Say("[戳诗萌] 场景里没有点击目标"); return; }
            var m = t.GetMethod("FireClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { Say("[戳诗萌] 找不到 FireClick"); return; }
            m.Invoke(arr[0], null);
            Say("[戳诗萌] 已调用 FireClick（等价于左键点她）");
        }

        /// <summary>
        /// 把记忆翻牌的 8 张牌面（与牌背）导成 PNG，供人来看图起名。
        /// 牌面是 Sprite，一个字都没有 —— 名字只能靠看。
        /// 产物落在 BepInEx/cardfaces/，属于游戏美术资源，**不入库**。
        /// </summary>
        private static void ExportCardFaces()
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var asm = typeof(DialogueLine).Assembly;
            var skinType = asm.GetType("ShiMeng.Minigames.MemoryMatch.MemoryMatchCardSkin");
            var dir = Path.Combine(Paths.GameRootPath, "BepInEx", "cardfaces");
            Directory.CreateDirectory(dir);
            Say("");
            Say("################ 牌面导出 ################");
            if (skinType == null) { Say("找不到 MemoryMatchCardSkin"); return; }
            var objs = Resources.FindObjectsOfTypeAll(skinType);
            Say("MemoryMatchCardSkin 实例数 = " + objs.Length);
            if (objs.Length == 0) Say("（CardSkin 模式未使用；下面走 PairPrefabs / 场景实例）");

            // ---- 来源 2：MemoryMatchUIView.pairCardPrefabs（本作实际用的模式）----
            var viewType = asm.GetType("ShiMeng.Minigames.MemoryMatch.MemoryMatchUIView");
            var cardType = asm.GetType("ShiMeng.Minigames.MemoryMatch.MemoryMatchCardView");
            Say("MemoryMatchUIView = " + (viewType != null) + " / MemoryMatchCardView = " + (cardType != null));
            if (viewType != null)
            {
                var views = Resources.FindObjectsOfTypeAll(viewType);
                Say("MemoryMatchUIView 实例数 = " + views.Length);
                foreach (var v in views)
                {
                    var modeF = viewType.GetField("cardArtMode", F);
                    if (modeF != null) Say("  cardArtMode = " + modeF.GetValue(v));
                    var pfF = viewType.GetField("pairCardPrefabs", F);
                    var pf = pfF != null ? pfF.GetValue(v) as Array : null;
                    if (pf == null) { Say("  pairCardPrefabs 为空"); continue; }
                    Say("  pairCardPrefabs.Length = " + pf.Length);
                    for (int i = 0; i < pf.Length; i++)
                    {
                        var cv = pf.GetValue(i) as Component;
                        if (cv == null) { Say("    #" + i + " (null)"); continue; }
                        DumpCardView(cardType, cv, "prefab" + i, i, dir);
                    }
                }
            }

            // ---- 来源 3：场上真正在用的牌（**按屏幕位置排序**，用来钉死 Card_N ↔ 行列）----
            if (cardType != null)
            {
                var cards = Resources.FindObjectsOfTypeAll(cardType);
                Say("场上 MemoryMatchCardView 数 = " + cards.Length);
                var list = new List<(string name, float x, float y, string front)>();
                foreach (var c in cards)
                {
                    var cv = c as Component;
                    if (cv == null) continue;
                    var rt = cv.transform as RectTransform;
                    Vector3 sp = rt != null ? rt.position : Vector3.zero;
                    var fi = cardType.GetField("frontImage", F);
                    var img = fi != null ? fi.GetValue(cv) as UnityEngine.UI.Image : null;
                    list.Add((cv.gameObject.name, sp.x, sp.y, img != null && img.sprite != null ? img.sprite.name : "?"));
                }
                // 屏幕坐标：先上后下、同行先左后右（与 UiNav 的排序口径一致）
                list.Sort((a, b) =>
                {
                    int ay = Mathf.RoundToInt(a.y / 4f), by = Mathf.RoundToInt(b.y / 4f);
                    if (ay != by) return by.CompareTo(ay);
                    return a.x.CompareTo(b.x);
                });
                Say("---- 牌位（按屏幕位置：上→下、左→右）----");
                int i2 = 0;
                foreach (var it in list)
                {
                    if (i2++ >= 20) break;
                    Say("    " + i2 + ". " + it.name + "  屏幕(" + (int)it.x + "," + (int)it.y + ")  front=" + it.front);
                }
            }
            foreach (var o in objs)
            {
                var frontField = skinType.GetField("pairFronts", F);
                var backField = skinType.GetField("cardBack", F);
                var backs = backField != null ? backField.GetValue(o) as Sprite : null;
                if (backs != null) SaveSprite(backs, Path.Combine(dir, "back_" + Sanitize(backs.name) + ".png"));
                var arr = frontField != null ? frontField.GetValue(o) as Array : null;
                if (arr == null) { Say("pairFronts 为空"); continue; }
                Say("pairFronts.Length = " + arr.Length);
                for (int i = 0; i < arr.Length; i++)
                {
                    var sp = arr.GetValue(i) as Sprite;
                    if (sp == null) { Say("  #" + i + " (null)"); continue; }
                    string file = Path.Combine(dir, "pair" + i + "_" + Sanitize(sp.name) + ".png");
                    SaveSprite(sp, file);
                    Say("  #" + i + " sprite=" + sp.name + " tex=" + (sp.texture != null ? sp.texture.name : "?")
                        + " rect=" + sp.rect.width + "x" + sp.rect.height);
                }
            }
            Say("导出目录: " + dir);
        }

        /// <summary>从一张牌视图里把牌面 Sprite 抠出来存成 PNG。</summary>
        private static void DumpCardView(Type cardType, Component cv, string tag, int idx, string dir)
        {
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var frontF = cardType.GetField("frontImage", F);
                var backF = cardType.GetField("backImage", F);
                var front = frontF != null ? frontF.GetValue(cv) as UnityEngine.UI.Image : null;
                var back = backF != null ? backF.GetValue(cv) as UnityEngine.UI.Image : null;
                string fn = front != null && front.sprite != null ? front.sprite.name : "(无)";
                string bn = back != null && back.sprite != null ? back.sprite.name : "(无)";
                Say("    " + tag + "[" + idx + "] " + cv.gameObject.name
                    + "  front=" + fn + "  back=" + bn);
                if (front != null && front.sprite != null)
                    SaveSprite(front.sprite, Path.Combine(dir, tag + idx + "_front_" + Sanitize(front.sprite.name) + ".png"));
                if (back != null && back.sprite != null && idx == 0)
                    SaveSprite(back.sprite, Path.Combine(dir, "back_" + Sanitize(back.sprite.name) + ".png"));
            }
            catch (Exception e) { Say("    " + tag + "[" + idx + "] 失败: " + e.Message); }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        private static void SaveSprite(Sprite sp, string path)
        {
            try
            {
                Texture2D tex = sp.texture;
                if (tex == null) { Say("  纹理为空: " + sp.name); return; }
                Rect r = sp.rect;
                int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);
                var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes(path, copy.EncodeToPNG());
                UnityEngine.Object.Destroy(copy);
            }
            catch (Exception e) { Say("  存 PNG 失败(" + sp.name + "): " + e.Message); }
        }

        /// <summary>驱动补丁的小游戏层：右移一格 → 下移一格 → 翻牌（等价于玩家按方向键与回车）。</summary>
        private static void DriveMinigame(int step)
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("MeiKongA11y.Minigame", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Say("[小游戏] 找不到 MeiKongA11y.Minigame"); return; }
            const BindingFlags F = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var move = t.GetMethod("Move", F);
            var flip = t.GetMethod("FlipCurrent", F);
            var act = "";
            switch (step)
            {
                case 0: act = "FlipCurrent(第1张)"; if (flip != null) flip.Invoke(null, null); break;
                case 1: act = "Move(右)";           if (move != null) move.Invoke(null, new object[] { 1, 0 }); break;
                case 2: act = "FlipCurrent(第2张)"; if (flip != null) flip.Invoke(null, null); break;
                case 3: act = "Move(下)";           if (move != null) move.Invoke(null, new object[] { 0, 1 }); break;
                default: return;
            }
            Say("[小游戏] step" + step + " → " + act);
        }

        /// <summary>直接调游戏自己的显示模式入口（模拟玩家在设置里勾选）。</summary>
        private static void SwitchMode(string methodName, string label)
        {
            var t = typeof(DialogueLine).Assembly.GetType("PetDisplayModeController");
            if (t == null) { Say("[模式] 找不到 PetDisplayModeController"); return; }
            var arr = UnityEngine.Object.FindObjectsOfType(t, true);
            if (arr == null || arr.Length == 0) { Say("[模式] 没有实例"); return; }
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            if (m == null) { Say("[模式] 找不到 " + methodName); return; }
            m.Invoke(arr[0], null);
            Say("[模式] 已调用 " + methodName + "（" + label + "）");
        }

        /// <summary>调用 a11y 补丁的重读逻辑（等价于玩家按退格）。</summary>
        private static void CallA11yRepeat()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("MeiKongA11y.Reader", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Say("[重读] 找不到 MeiKongA11y.Reader"); return; }
            var f = t.GetField("HasChoicesBuffered", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var pi = t.GetProperty("HasChoicesBuffered", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            bool hasChoices = f != null ? (bool)f.GetValue(null) : (pi != null && (bool)pi.GetValue(null, null));
            Say("[重读] 调用 Repeat()，此刻缓冲区里有选项 = " + hasChoices);
            var m = t.GetMethod("Repeat", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m != null) m.Invoke(null, null);
        }

        /// <summary>调用 a11y 补丁的保命键逻辑（Pet.ExitDeskPet）。</summary>
        private static void CallA11yExitDeskPet()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("MeiKongA11y.Pet", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Say("[保命键] 找不到 MeiKongA11y.Pet（补丁没装？）"); return; }
            var m = t.GetMethod("ExitDeskPet", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { Say("[保命键] 找不到 ExitDeskPet"); return; }
            object ok = m.Invoke(null, null);
            Say("[保命键] ExitDeskPet 返回 " + ok);
        }

        /// <summary>把 a11y 补丁的整份导航表（所有组、所有项、按真实顺序）原样倒出来。</summary>
        private static void DumpNavAll()
        {
            Say("");
            Say("################ 导航表全量（用户按 PageUp/PageDown 能到达的一切）################");
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("MeiKongA11y.UiNav", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Say("找不到 MeiKongA11y.UiNav"); return; }

            var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            var fGroups = t.GetField("Groups", flags);
            var mDescribe = t.GetMethod("Describe", flags);
            var fIdx = t.GetField("_groupIndex", flags);
            var groups = fGroups != null ? fGroups.GetValue(null) as System.Collections.IList : null;
            if (groups == null) { Say("拿不到 Groups"); return; }
            object cur = fIdx != null ? fIdx.GetValue(null) : null;
            Say("共 " + groups.Count + " 组，当前组序号 = " + cur);
            for (int gi = 0; gi < groups.Count; gi++)
            {
                object g = groups[gi];
                var gt = g.GetType();
                var fRoot = gt.GetField("Root", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fItems = gt.GetField("Items", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var root = fRoot != null ? fRoot.GetValue(g) as Component : null;
                var items = fItems != null ? fItems.GetValue(g) as System.Collections.IList : null;
                Say("[组 " + (gi + 1) + "] 根=" + (root != null ? root.gameObject.name : "?")
                    + "  项数=" + (items != null ? items.Count : -1));
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    var s = items[i] as Selectable;
                    if (s == null) { Say("    #" + (i + 1) + " (null)"); continue; }
                    string desc = "?";
                    try { desc = mDescribe != null ? Convert.ToString(mDescribe.Invoke(null, new object[] { s })) : "?"; } catch { }
                    Say("    #" + (i + 1) + " " + desc + "   <" + Chain(s.transform) + ">");
                }
            }
        }

        /// <summary>按对象名找到功能条按钮并点它（模拟玩家开面板）。</summary>
        private static void OpenPanelByName(string objectName)
        {
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Button>(true))
            {
                if (b == null || b.gameObject == null) continue;
                if (!string.Equals(b.gameObject.name, objectName, StringComparison.Ordinal)) continue;
                if (!b.gameObject.activeInHierarchy) continue;
                b.onClick.Invoke();
                Say("[开面板] 已点击按钮「" + objectName + "」");
                return;
            }
            Say("[开面板] 找不到可用的按钮「" + objectName + "」");
        }

        /// <summary>调用 a11y 补丁的 UiNav.Toggle() 进入导航模式，触发它自己的诊断转储。</summary>
        private static void DoNavToggle()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("MeiKongA11y.UiNav", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Say("[导航] 找不到 MeiKongA11y.UiNav（补丁没装？）"); return; }
            var toggle = t.GetMethod("Toggle", BindingFlags.Public | BindingFlags.Static);
            if (toggle == null) { Say("[导航] 找不到 Toggle"); return; }
            toggle.Invoke(null, null);
            Say("[导航] 已调用 UiNav.Toggle()");
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
            try
            {
                var util = typeof(DialogueLine).Assembly.GetType("PetDeskPetInputUtility");
                var mi = util?.GetMethod("IsDeskPetActive", BindingFlags.Public | BindingFlags.Static);
                sb.Append(" 模式=").Append(mi != null && (bool)mi.Invoke(null, null) ? "桌宠" : "全屏");
            }
            catch { }

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

        // ⚠ 参数名必须与**运行时**一致（流水线 G2）：
        //   反编译里是 AddMessage(string messageText, bool isLeft, PhoneDialogue dialogue)，
        //   写错一个名字就会让整个补丁类 PatchAll 抛异常，而且其余补丁照常生效。
        [HarmonyPatch(typeof(PhoneDialogueManager), "AddMessage")]
        [HarmonyPrefix]
        private static void PhoneAdd_Pre(string messageText, bool isLeft)
        {
            ProbePlugin.Say(T + "PhoneDialogueManager.AddMessage isLeft=" + isLeft + " text=" + ProbePlugin.Shape(messageText));
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
