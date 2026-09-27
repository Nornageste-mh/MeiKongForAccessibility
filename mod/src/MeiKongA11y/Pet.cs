using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// 桌宠交互层：怎么"戳"诗萌、她在哪儿、现在是什么显示模式。
    ///
    /// === 实机事实（P1 交互面勘查）===
    ///
    ///   · 诗萌本体 = 场景根对象 `Spine GameObject (skeleton)`，挂：
    ///       PetController / SkeletonAnimation / BoxCollider2D / **DialogueEntryClickTarget**
    ///       / PetDeskPetPointerTarget / PetDeskPetInteractionLock / SpineDialogueAnimPresenter
    ///   · 点她 = `DialogueEntryClickTarget.FireClick()`（private）：
    ///       播点击音效 → 若番茄钟专注中则 `PomodoroManager.OnClickDuringFocus()`
    ///       否则 `GlobalEvent.Trigger("DialogueEntry_ClickCharacter", targetId)`（targetId = "ShiMeng"）
    ///     之后由 `DialogueEntryManager` 按 `DialogueEntryConfig.Entries` 挑一段对话播出来。
    ///
    /// ★ 本层的公平性立场（**不许作弊**）：
    ///   本层提供的每一样东西，都必须是"正常人用鼠标/眼睛就能做到"的等价物：
    ///     · 「戳一下诗萌」= 用鼠标左键点她的**等价操作**，不是额外信息；
    ///     · 「诗萌在哪」= 正常人抬眼就能看到的位置，只是换成语言；
    ///     · **不**提供任何正常人拿不到的信息（不预告下一段对话、不读未解锁内容）。
    ///
    /// ★ 实现纪律：`FireClick` 用**反射调用游戏自己的方法**，而不是把它的四行逻辑抄一遍 ——
    ///   抄一遍就会在游戏更新时悄悄与本体行为分叉（音效、番茄钟分支都会漏）。
    /// </summary>
    internal static class Pet
    {
        private static Type _clickType;
        private static bool _probed;
        private static MethodInfo _fireClick;

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                _clickType = asm.GetType("ShiMeng.DialogueV2.DialogueEntryClickTarget");
                if (_clickType != null)
                    _fireClick = _clickType.GetMethod("FireClick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                A11yHost.Diag("[桌宠] DialogueEntryClickTarget=" + (_clickType != null) + " FireClick=" + (_fireClick != null));
            }
            catch (Exception e) { A11yHost.Diag("[桌宠] 探针异常: " + e.Message); }
        }

        /// <summary>「戳一下诗萌」：等价于用鼠标左键点她。</summary>
        internal static bool Poke()
        {
            Probe();
            if (_clickType == null) { Speech.Speak("找不到诗萌的点击目标，可能游戏改版了。", true); return false; }
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(_clickType);
                if (arr == null || arr.Length == 0) { Speech.Speak("现在点不到诗萌。", true); return false; }
                object target = arr[0];
                if (_fireClick != null)
                {
                    _fireClick.Invoke(target, null);
                    A11yHost.Diag("[桌宠] 已调用 FireClick（等价于左键点她）");
                    return true;
                }
                // 回退：直接触发游戏自己的全局事件（少一个音效与番茄钟分支，仅当 FireClick 找不到时）
                return TriggerClickEvent(target);
            }
            catch (Exception e)
            {
                A11yHost.Diag("[桌宠] Poke 异常: " + e.Message);
                Speech.Speak("戳诗萌的时候出错了。", true);
                return false;
            }
        }

        private static bool TriggerClickEvent(object clickTarget)
        {
            try
            {
                var tidF = _clickType.GetField("targetId", BindingFlags.Instance | BindingFlags.NonPublic);
                string tid = tidF != null ? Convert.ToString(tidF.GetValue(clickTarget)) : "ShiMeng";
                var asm = _clickType.Assembly;
                var ge = asm.GetType("GlobalEvent") ?? asm.GetType("ShiMeng.Core.GlobalEvent");
                if (ge == null) return false;
                var trigger = ge.GetMethod("Trigger", BindingFlags.Public | BindingFlags.Static, null,
                                           new[] { typeof(string), typeof(object) }, null)
                           ?? ge.GetMethod("Trigger", BindingFlags.Public | BindingFlags.Static, null,
                                           new[] { typeof(string), typeof(string) }, null);
                if (trigger == null) return false;
                trigger.Invoke(null, new object[] { "DialogueEntry_ClickCharacter", tid });
                A11yHost.Diag("[桌宠] 已回退到 GlobalEvent.Trigger(DialogueEntry_ClickCharacter, " + tid + ")");
                return true;
            }
            catch (Exception e) { A11yHost.Diag("[桌宠] TriggerClickEvent 异常: " + e.Message); return false; }
        }

        /// <summary>诗萌在屏幕上的方位（正常人抬眼就能看到的信息，换成语言）。</summary>
        internal static string WhereIsPet()
        {
            Probe();
            if (_clickType == null) return "";
            try
            {
                var arr = Resources.FindObjectsOfTypeAll(_clickType);
                if (arr == null || arr.Length == 0) return "现在看不到诗萌。";
                var comp = arr[0] as Component;
                if (comp == null) return "";
                var col = comp.GetComponent<Collider2D>();
                var cam = Camera.main;
                if (col == null || cam == null) return "";
                Vector3 sp = cam.WorldToScreenPoint(col.bounds.center);
                if (sp.z < 0f) return "诗萌现在不在画面里。";
                int w = Screen.width, h = Screen.height;
                string hz = sp.x < w / 3f ? "左" : (sp.x > w * 2f / 3f ? "右" : "中间");
                string vt = sp.y < h / 3f ? "下" : (sp.y > h * 2f / 3f ? "上" : "中间");
                string pos = (hz == "中间" && vt == "中间") ? "屏幕正中" : ("屏幕" + vt + hz + "部");
                return "诗萌在" + pos + "（" + (int)sp.x + "," + (int)(h - sp.y) + "）。";
            }
            catch (Exception e) { A11yHost.Diag("[桌宠] WhereIsPet 异常: " + e.Message); return ""; }
        }

        /// <summary>当前显示模式：全屏 / 桌宠。</summary>
        internal static string DisplayMode()
        {
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                var util = asm.GetType("PetDeskPetInputUtility");
                var m = util?.GetMethod("IsDeskPetActive", BindingFlags.Public | BindingFlags.Static);
                bool desk = m != null && (bool)m.Invoke(null, null);
                return desk ? "桌宠模式" : "全屏模式";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 「和诗萌聊聊」按钮组：`MainStoryTriggerButtonVariant`（实机 5 个变体，标签都取自
        /// `Label` 属性 —— variantLabel 为空时回退到对象名，所以别指望对象名有意义）。
        /// 找一个**当前激活**的变体点它；没有激活的就回退到「戳一下诗萌」。
        /// </summary>
        internal static bool ClickStoryButton()
        {
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                var t = asm.GetType("ShiMeng.DialogueV2.MainStoryTriggerButtonVariant");
                if (t == null) return Poke();
                foreach (var obj in Resources.FindObjectsOfTypeAll(t))
                {
                    var comp = obj as Component;
                    if (comp == null || !comp.gameObject.activeInHierarchy) continue;
                    var btn = comp.GetComponent<Button>() ?? comp.GetComponentInChildren<Button>(true);
                    if (btn == null || !btn.IsInteractable()) continue;
                    string label = Label(t, obj);
                    btn.onClick.Invoke();
                    Speech.Speak("已按下：" + label, true);
                    return true;
                }
                // 没有可见的互动按钮 → 等价于直接戳她
                return Poke();
            }
            catch (Exception e)
            {
                A11yHost.Diag("[桌宠] ClickStoryButton 异常: " + e.Message);
                return false;
            }
        }

        private static string Label(Type t, object inst)
        {
            try
            {
                var pi = t.GetProperty("Label");
                if (pi != null) return Convert.ToString(pi.GetValue(inst));
            }
            catch { }
            return "互动";
        }

        /// <summary>右键菜单里有什么（当前模式不支持时如实说明）。</summary>
        internal static string ContextMenuSummary()
        {
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                var t = asm.GetType("PetDeskPetContextMenuController");
                if (t == null) return "";
                var arr = Resources.FindObjectsOfTypeAll(t);
                if (arr == null || arr.Length == 0) return "";
                var comp = arr[0] as Component;
                if (comp == null) return "";
                var f = t.GetField("menuRoot", BindingFlags.Instance | BindingFlags.NonPublic);
                var root = f != null ? f.GetValue(arr[0]) as RectTransform : null;
                var use = root != null ? root : comp.transform as RectTransform;
                int n = use != null ? use.childCount : 0;
                if (n == 0) return "右键菜单当前没有条目。";
                var sb = new System.Text.StringBuilder("右键菜单 " + n + " 项：");
                for (int i = 0; i < n && i < 12; i++)
                {
                    if (i > 0) sb.Append('；');
                    sb.Append(i + 1).Append('、').Append(use.GetChild(i).name);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
