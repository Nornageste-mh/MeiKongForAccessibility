using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// **交互层**：面板感知 + 功能直达 + 状态播报。
    ///
    /// 这一层解决的是「盲人怎么流畅地跟控件打交道」，而不是「怎么念台词」——
    /// 本作 97.2% 的台词有配音，念台词反而不是难点；难点在 247 个 Selectable 里
    /// 摸到那 18 个此刻真正可用的，并且知道自己在哪个面板、面板里有什么。
    ///
    /// === 实机事实（P1 探针 · 场景树 848 行 + 面板勘查）===
    ///
    ///   · 游戏有 **15 个功能面板**，全部注册在 `PetPanelRegistry._map`
    ///     （schedule / pomodoro / meal_today / music_player / anime_assistant / costume /
    ///       background / favorites / minigame / meme_maker / collab_shelf / voice_collect /
    ///       setting / Tips1 / Tips2）；
    ///   · 打开面板靠 **6 个功能条按钮**：小游戏 / 番茄钟 / 日程表 / 设置 / 故事·收藏 / 换装
    ///     （对象名就是中文，子树里有 TMP 标签 —— 所以它们本身是可朗读的）；
    ///   · 活动场景 247 个 Selectable 里只有 **18 个**「既 active 又可交互」；
    ///     252 个 TMP 里只有 **3 个**有字 —— 面板没打开时，面板里的控件全是死的。
    ///
    /// 所以本层的三个动作是：
    ///   1) **面板变化播报**：谁被打开了、谁被关掉了，主动说一句；
    ///   2) **功能菜单**（默认 F1）：把 6 个入口一次念完并编号，按数字直达 ——
    ///      盲人不需要在控件树里翻；
    ///   3) **状态播报**（默认 F2）：报当前面板、可操作项数量、番茄钟剩余时间等。
    ///
    /// 纪律：本层只**读**游戏状态、只**点游戏自己的按钮**（`Button.onClick.Invoke()`），
    /// 不复制游戏逻辑、不绕过任何确认（流水线 §4）。
    /// </summary>
    internal static class Surfaces
    {
        // ---- 功能条按钮（对象名来自探针场景树实查）----
        private static readonly string[][] BarEntries =
        {
            new[] { "小游戏",     "小游戏" },
            new[] { "番茄钟",     "番茄钟" },
            new[] { "日程表",     "日程表" },
            new[] { "设置",       "设置" },
            new[] { "故事/收藏",  "故事与收藏" },
            new[] { "故事 收藏",  "故事与收藏" },
            new[] { "换装",       "换装" },
        };

        // ---- 面板注册表（反射，不硬编码；游戏改版删掉该类只会让这一层降级）----
        private static Type _regType;
        private static bool _regProbed;
        private static FieldInfo _mapField;

        private static string _lastPanel = "";
        private static float _menuUntil;
        private static readonly List<string> _menuNames = new List<string>();
        private static readonly List<string> _menuObjectNames = new List<string>();

        internal static void Tick()
        {
            try
            {
                AnnouncePanelChange();
                HandleMenuKeys();
            }
            catch (Exception e) { A11yHost.Diag("[交互] Tick 异常: " + e.Message); }
        }

        // ==================================================================
        // 1) 面板感知
        // ==================================================================
        private static void ProbeRegistry()
        {
            if (_regProbed) return;
            _regProbed = true;
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                _regType = asm.GetType("PetPanelRegistry");
                if (_regType == null) { A11yHost.Diag("[交互] 找不到 PetPanelRegistry，面板感知关闭"); return; }
                var flds = _regType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in flds)
                    if (typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType)) { _mapField = f; break; }
                A11yHost.Diag("[交互] PetPanelRegistry=" + _regType.FullName + " 字段=" + (_mapField != null ? _mapField.Name : "未找到"));
            }
            catch (Exception e) { A11yHost.Diag("[交互] 探注册表异常: " + e.Message); }
        }

        /// <summary>此刻「真正打开着」的面板 Id；没有就返回空串。</summary>
        internal static string CurrentPanelId()
        {
            ProbeRegistry();
            if (_regType == null || _mapField == null) return "";
            try
            {
                foreach (var inst in Resources.FindObjectsOfTypeAll(_regType))
                {
                    var dict = _mapField.GetValue(inst) as System.Collections.IDictionary;
                    if (dict == null) continue;
                    foreach (System.Collections.DictionaryEntry e in dict)
                    {
                        var go = e.Value as GameObject;
                        if (go == null || !go.activeInHierarchy) continue;
                        var cg = go.GetComponent<CanvasGroup>();
                        if (cg != null && cg.alpha < 0.5f) continue;   // 「幽灵面板」：active 但透明
                        return Convert.ToString(e.Key);
                    }
                }
            }
            catch (Exception e) { A11yHost.Diag("[交互] 读面板异常: " + e.Message); }
            return "";
        }

        private static void AnnouncePanelChange()
        {
            if (Plugin.CfgAnnouncePanel == null || !Plugin.CfgAnnouncePanel.Value) return;
            string now = CurrentPanelId();
            if (now == _lastPanel) return;
            string prev = _lastPanel;
            _lastPanel = now;
            if (string.IsNullOrEmpty(now))
            {
                if (!string.IsNullOrEmpty(prev)) Speech.Speak("面板已关闭。", false);
                return;
            }
            string cn = PanelCn(now);
            int sels = CountOperable(now);
            Speech.Speak("已打开：" + cn + "，" + sels + " 个可操作项。按 Tab 进入导航。", false);
        }

        /// <summary>当前打开面板的中文名（给 UiNav 的组名用）。没打开面板时返回空串。</summary>
        internal static string CurrentPanelNameCn()
        {
            string id = CurrentPanelId();
            return string.IsNullOrEmpty(id) ? "" : PanelCn(id);
        }

        /// <summary>面板 Id → 中文（取自面板标题 TMP，探针实查）。</summary>
        private static string PanelCn(string id)
        {
            switch (id)
            {
                case "schedule":        return "日程表";
                case "pomodoro":        return "番茄钟";
                case "meal_today":      return "今日饭点";
                case "music_player":    return "音乐播放器";
                case "anime_assistant": return "动画助手";
                case "costume":         return "换装";
                case "background":      return "背景与氛围设置";
                case "favorites":       return "故事与收藏";
                case "minigame":        return "小游戏";
                case "meme_maker":      return "表情包工坊";
                case "collab_shelf":    return "联动收藏架";
                case "voice_collect":   return "语音收藏";
                case "setting":         return "系统设置";
                case "Tips1":           return "提示";
                case "Tips2":           return "提示";
                default:                return id;
            }
        }

        private static GameObject PanelRoot(string id)
        {
            ProbeRegistry();
            if (_regType == null || _mapField == null) return null;
            try
            {
                foreach (var inst in Resources.FindObjectsOfTypeAll(_regType))
                {
                    var dict = _mapField.GetValue(inst) as System.Collections.IDictionary;
                    if (dict == null || !dict.Contains(id)) continue;
                    return dict[id] as GameObject;
                }
            }
            catch { }
            return null;
        }

        private static int CountOperable(string id)
        {
            var root = PanelRoot(id);
            if (root == null) return 0;
            int n = 0;
            foreach (var s in root.GetComponentsInChildren<Selectable>(true))
                if (s != null && s.IsActive() && s.IsInteractable()) n++;
            return n;
        }

        // ==================================================================
        // 2) 功能菜单（F1）：念完编号，按数字直达
        // ==================================================================
        internal static void OpenMenu()
        {
            _menuNames.Clear();
            _menuObjectNames.Clear();
            foreach (var e in BarEntries)
            {
                if (FindBarButton(e[0]) == null) continue;
                if (_menuObjectNames.Contains(e[0])) continue;
                _menuObjectNames.Add(e[0]);
                _menuNames.Add(e[1]);
            }
            if (_menuNames.Count == 0)
            {
                Speech.Speak("界面当前不可见，先按 Esc 呼出界面再试。", true);
                return;
            }
            var sb = new System.Text.StringBuilder("功能菜单，");
            for (int i = 0; i < _menuNames.Count; i++)
            {
                if (i > 0) sb.Append('；');
                sb.Append(i + 1).Append('、').Append(_menuNames[i]);
            }
            sb.Append("。按数字键打开。");
            _menuUntil = Time.realtimeSinceStartup + 10f;
            Speech.Speak(sb.ToString(), true);
        }

        private static void HandleMenuKeys()
        {
            if (Time.realtimeSinceStartup > _menuUntil) return;
            if (_menuNames.Count == 0) return;
            int pick = Choices.ReadDigit();
            if (pick <= 0 || pick > _menuObjectNames.Count) return;
            string objName = _menuObjectNames[pick - 1];
            string cn = _menuNames[pick - 1];
            _menuUntil = 0f;
            bool ok = ActivateBarButton(objName);
            Speech.Speak(ok ? ("打开" + cn) : (cn + " 现在打不开。"), true);
        }

        private static Button FindBarButton(string objectName)
        {
            try
            {
                foreach (var b in Resources.FindObjectsOfTypeAll<Button>())
                {
                    if (b == null || b.gameObject == null) continue;
                    if (!string.Equals(b.gameObject.name, objectName, StringComparison.Ordinal)) continue;
                    if (!b.gameObject.activeInHierarchy || !b.IsInteractable()) continue;
                    return b;
                }
            }
            catch { }
            return null;
        }

        private static bool ActivateBarButton(string objectName)
        {
            var b = FindBarButton(objectName);
            if (b == null) return false;
            try { b.onClick.Invoke(); return true; }
            catch (Exception e) { A11yHost.Diag("[交互] 激活按钮异常: " + e.Message); return false; }
        }

        // ==================================================================
        // 3) 状态播报（F2）
        // ==================================================================
        internal static void ReportState()
        {
            var sb = new System.Text.StringBuilder();
            string mode = Pet.DisplayMode();
            if (!string.IsNullOrEmpty(mode)) sb.Append(mode).Append("。");

            string where = Pet.WhereIsPet();
            if (!string.IsNullOrEmpty(where)) sb.Append(where);

            string id = CurrentPanelId();
            if (string.IsNullOrEmpty(id)) sb.Append("当前没有打开的面板。");
            else sb.Append("当前面板：").Append(PanelCn(id)).Append("，").Append(CountOperable(id)).Append(" 个可操作项。");

            string pomo = PomodoroSummary();
            if (!string.IsNullOrEmpty(pomo)) sb.Append(pomo);

            int live = 0;
            try
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<Selectable>())
                    if (s != null && s.IsActive() && s.IsInteractable()) live++;
            }
            catch { }
            sb.Append("全屏共 ").Append(live).Append(" 个可操作项。");
            Speech.Speak(sb.ToString(), true);
        }

        /// <summary>
        /// 番茄钟状态。反编译实查：`PomodoroManager.State / IsPaused / PhaseTimeLeft`，
        /// 而面板上的文字标签会被运行时的 `PomodoroStateSpriteDisplay` 顶掉（arch-map §五），
        /// 所以**不能读 TMP**，必须读管理器本身。
        /// </summary>
        private static string PomodoroSummary()
        {
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                var t = asm.GetType("PomodoroManager");
                if (t == null) return "";
                var arr = Resources.FindObjectsOfTypeAll(t);
                if (arr == null || arr.Length == 0) return "";
                object mgr = arr[0];
                object state = ReadMember(mgr, t, "State", "CurrentState", "Phase");
                object paused = ReadMember(mgr, t, "IsPaused", "Paused");
                object left = ReadMember(mgr, t, "PhaseTimeLeft", "TimeLeft", "Remaining");
                if (state == null && left == null) return "";
                string s = " 番茄钟：状态 " + (state != null ? state.ToString() : "?");
                if (paused is bool && (bool)paused) s += "（已暂停）";
                if (left != null)
                {
                    double sec;
                    if (double.TryParse(left.ToString(), out sec) && sec >= 0)
                        s += "，剩余 " + FormatSeconds(sec);
                    else
                        s += "，剩余 " + left;
                }
                return s + "。";
            }
            catch { return ""; }
        }

        private static object ReadMember(object o, Type t, params string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var p = t.GetProperty(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null && p.CanRead) return p.GetValue(o);
                    var f = t.GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) return f.GetValue(o);
                }
                catch { }
            }
            return null;
        }

        private static string FormatSeconds(double sec)
        {
            int s = (int)Math.Round(sec);
            int m = s / 60; s %= 60;
            return m > 0 ? (m + " 分 " + s + " 秒") : (s + " 秒");
        }
    }
}
