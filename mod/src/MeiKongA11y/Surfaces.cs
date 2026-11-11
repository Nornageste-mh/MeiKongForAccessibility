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
            new[] { "音乐",       "音乐列表" },
            new[] { "换装",       "换装" },
            new[] { "故事/收藏",  "收藏内容" },
            new[] { "联动内容",   "联动展架" },
            new[] { "鬼畜制造机", "鬼畜制造机" },
            new[] { "装饰",       "背景设置" },
            new[] { "设置",       "系统设置" },
            new[] { "隐藏UI",     "隐藏界面" },
            new[] { "退出",       "退出游戏" },
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
                Pet.WatchModeChange();
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
                // 换面板时游戏是先关再开：新面板已经接上焦点就不再念「面板已关闭」，
                // 否则玩家会听到「已关闭」紧跟「已打开」，像是误操作。
                if (!string.IsNullOrEmpty(prev) && !Focus.IsPanelActive) Announcer.Auto("面板已关闭。");
                return;
            }
            string cn = PanelCn(now);
            var root = PanelRoot(now);
            // 0.2.7.2 起面板打开由 Focus 在游戏自己的 OpenPanel/OpenUI 事件里播报
            // （「面板名，共 N 个可操作项。当前：第一项」），这里就不再重复念一遍。
            if (root != null && Focus.Root == root.transform) return;
            int sels = CountOperable(now);
            // 只报「打开了什么、有几项」。方向键/回车的含义不再重复解释。
            Announcer.Auto("已打开：" + cn + "，" + sels + " 个可操作项。");
        }

        /// <summary>面板 Id → 中文名（给焦点层播报面板标题用）。未知 Id 原样返回。</summary>
        internal static string NameOf(string id)
        {
            return string.IsNullOrEmpty(id) ? "面板" : PanelCn(id);
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
                case "meal_today":      return "今天吃啥";
                case "music_player":    return "音乐列表";
                case "anime_assistant": return "追番助手";
                case "costume":         return "换装";
                case "background":      return "背景设置";
                case "favorites":       return "收藏内容";
                case "minigame":        return "小游戏";
                case "meme_maker":      return "鬼畜制造机";
                case "collab_shelf":    return "联动展架";
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

        /// <summary>
        /// 关掉某个面板（走游戏注册表自己的 <c>ClosePanel(string)</c>）。
        /// 找不到注册表或方法就返回 false —— 调用方自行决定退化路径。
        /// </summary>
        internal static bool ClosePanel(string id)
        {
            ProbeRegistry();
            if (_regType == null || string.IsNullOrEmpty(id)) return false;
            try
            {
                var m = _regType.GetMethod("ClosePanel", BindingFlags.Instance | BindingFlags.Public,
                                           null, new[] { typeof(string) }, null);
                if (m == null) { A11yHost.Diag("[交互] PetPanelRegistry 没有 ClosePanel(string)"); return false; }
                foreach (var inst in Resources.FindObjectsOfTypeAll(_regType))
                {
                    m.Invoke(inst, new object[] { id });
                    A11yHost.Diag("[交互] 已关闭面板 " + id);
                    return true;
                }
            }
            catch (Exception e) { A11yHost.Diag("[交互] 关面板异常: " + e.Message); }
            return false;
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
        // 2) 功能菜单（F1）：念完编号，按数字直达（每页 9 项，按 0 翻页）
        // ==================================================================
        // ---- 界面隐藏/恢复（游戏侧是单向的，见 OpenMenu 里的注释） ----
        private static System.Type _hubType;
        private static System.Reflection.FieldInfo _chromeHiddenField;
        private static System.Reflection.MethodInfo _restoreUiMethod;

        private static bool EnsureHubRefs()
        {
            if (_hubType != null) return _chromeHiddenField != null && _restoreUiMethod != null;
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                _hubType = asm.GetType("PetUiIntegrationHub");
                if (_hubType == null) return false;
                _chromeHiddenField = _hubType.GetField("_chromeHidden",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                _restoreUiMethod = _hubType.GetMethod("RestoreAllUiRoots",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                return _chromeHiddenField != null && _restoreUiMethod != null;
            }
            catch (Exception e) { A11yHost.Diag("[交互] 探 PetUiIntegrationHub 失败: " + e.Message); return false; }
        }

        /// <summary>界面确实被游戏藏起来了吗（不是「面板没打开」那种空）。</summary>
        private static bool ChromeHidden()
        {
            if (!EnsureHubRefs()) return false;
            try
            {
                var hub = UnityEngine.Object.FindObjectOfType(_hubType);
                if (hub == null) return false;
                return (bool)_chromeHiddenField.GetValue(hub);
            }
            catch { return false; }
        }

        /// <summary>把被藏起来的界面恢复出来（调游戏自己的 RestoreAllUiRoots）。成功返回 true。</summary>
        internal static bool RestoreHiddenUi()
        {
            if (!EnsureHubRefs()) return false;
            try
            {
                var hub = UnityEngine.Object.FindObjectOfType(_hubType);
                if (hub == null) return false;
                _restoreUiMethod.Invoke(hub, null);
                A11yHost.Diag("[交互] 界面被藏起来了，已调 RestoreAllUiRoots() 恢复");
                return true;
            }
            catch (Exception e) { A11yHost.Diag("[交互] 恢复界面失败: " + e.Message); return false; }
        }

        private const int MenuPageSize = 9;
        private static int _menuPage;
        private static int _menuIndex;          // 菜单里光标所在项（0 起，跨页连续）
        private static int _quitArmedIndex = -1;
        private static float _quitArmedUntil;

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
                // 游戏把「隐藏界面」做成了单向的：再按一次同一个按钮才会恢复，可按钮自己也被藏了，
                // 所以键盘玩家按完就再也找不到入口（游戏的恢复条件只有「再点一次」/左键，
                // 见 PetUiIntegrationHub.ToggleHideAllUiRoots / Update）。F1 这时先替玩家把界面恢复。
                if (RestoreHiddenUi())
                {
                    Repeat.Say("界面已恢复显示。按方向键可以浏览主界面。", true);
                    return;
                }
                Repeat.Say("界面当前不可见，补丁没能自动恢复。请用鼠标左键点一下屏幕，或按 F5 切回全屏。", true);
                return;
            }
            _menuPage = 0;
            _menuIndex = 0;
            _menuUntil = Time.realtimeSinceStartup + 12f;
            AnnounceMenuCurrent(true);
        }

        /// <summary>
        /// 菜单当前项。第一项带总数，之后的移动只报「第 N 项：名字」——
        /// 和游戏自己的列表一样：进列表先说清规模，走一步报一步。
        /// 数字键仍然有效（老玩家习惯），但不再把「按数字键」念出来，
        /// 那会让这个菜单听起来像补丁自己加的外挂面板（用户 m04680 的意见）。
        /// </summary>
        private static void AnnounceMenuCurrent(bool withTotal)
        {
            if (_menuIndex < 0 || _menuIndex >= _menuNames.Count) return;
            var sb = new System.Text.StringBuilder();
            if (withTotal) sb.Append("菜单，共 ").Append(_menuNames.Count).Append(" 项。");
            sb.Append("第 ").Append(_menuIndex + 1).Append(" 项：")
              .Append(_menuNames[_menuIndex]).Append('。');
            _menuUntil = Time.realtimeSinceStartup + 12f;
            Announcer.Now(sb.ToString());
        }

        /// <summary>菜单开着的时候，方向键/回车归菜单，不归焦点层（避免同一下按键两处响应）。</summary>
        internal static bool MenuVisible()
        {
            return _menuNames.Count > 0 && Time.realtimeSinceStartup <= _menuUntil;
        }

        /// <summary>菜单光标位置（0 起）。只给探针/diag 用。</summary>
        internal static int MenuIndex { get { return _menuIndex; } }

        internal static void ActivateCurrentMenu() { ActivateMenuIndex(_menuIndex); }

        internal static void CloseMenu()
        {
            if (_menuNames.Count == 0) return;
            _menuUntil = 0f;
            _quitArmedIndex = -1;
            Announcer.Now("菜单已关闭。");
        }

        internal static void MoveMenu(int delta)
        {
            if (_menuNames.Count == 0) return;
            int next = _menuIndex + delta;
            if (next < 0 || next >= _menuNames.Count) { Announcer.Now("到头了。"); return; }
            _menuIndex = next;
            _menuPage = _menuIndex / MenuPageSize;
            AnnounceMenuCurrent(false);
        }

        internal static void ChangeMenuPage(int delta)
        {
            if (_menuNames.Count == 0) return;
            int pages = (_menuNames.Count + MenuPageSize - 1) / MenuPageSize;
            int page = Mathf.Clamp(_menuPage + delta, 0, pages - 1);
            if (page == _menuPage) { Announcer.Now("到头了。"); return; }
            _menuPage = page;
            _menuIndex = page * MenuPageSize;
            AnnounceMenuCurrent(true);
        }

        /// <summary>菜单里选第 N 个（1–9；10 = 下一页）。人工按键与探针共用。</summary>
        internal static void PickMenu(int digit)
        {
            if (_menuNames.Count == 0) return;
            if (_quitArmedIndex >= 0 && Time.realtimeSinceStartup > _quitArmedUntil) _quitArmedIndex = -1;
            if (digit <= 0) return;

            if (digit == 10) { ChangeMenuPage(1); return; }

            int idx = _menuPage * MenuPageSize + digit - 1;
            if (idx < 0 || idx >= _menuNames.Count) { Announcer.Now("没有这一项。"); return; }
            _menuIndex = idx;
            ActivateMenuIndex(idx);
        }

        /// <summary>激活菜单第 idx 项。方向键回车与数字键走同一条路，探针也用它。</summary>
        internal static void ActivateMenuIndex(int idx)
        {
            if (idx < 0 || idx >= _menuNames.Count) return;
            string objName = _menuObjectNames[idx];
            string cn = _menuNames[idx];

            // 退出游戏：游戏自己没有任何确认框（PetUiIntegrationHub.QuitGame → DialogueEntryManager
            // .RequestGracefulQuit 直接播完告别对话就关进程），键盘按错一个数字就没了。
            // 这里按 CfgQuitConfirm（默认 true）要求同一个键在 5 秒内按两次；等待期间菜单保持可用，
            // 玩家可以改选别的项（选别的项就等于取消）。
            bool needConfirm = cn == "退出游戏" && (A11yHost.CfgQuitConfirm == null || A11yHost.CfgQuitConfirm.Value);
            if (needConfirm && _quitArmedIndex != idx)
            {
                _quitArmedIndex = idx;
                _quitArmedUntil = Time.realtimeSinceStartup + 5f;
                _menuUntil = _quitArmedUntil;
                Announcer.Now("再按一次确认退出游戏。");
                return;
            }
            _quitArmedIndex = -1;
            _menuUntil = 0f;

            bool ok = ActivateBarButton(objName);
            if (!ok) { Announcer.Now(cn + " 现在打不开。"); return; }
            // 「隐藏界面」「退出游戏」不是「打开某个面板」，按完要说清楚发生了什么、怎么回来
            if (cn == "隐藏界面") { Announcer.Now("界面已隐藏，按 F1 恢复。"); return; }
            if (cn == "退出游戏") { Announcer.Now("正在退出游戏。"); return; }
            // 面板真的打开时焦点层会念「面板名，共 N 个可操作项。当前：…」，
            // 这里再补一句「打开X」就是重复；打不开的情况上面已经报过。
            // （旧版是数字键直达，没有焦点层接手，才需要这句确认。）
        }

        private static void HandleMenuKeys()
        {
            if (!MenuVisible()) return;
            if (_quitArmedIndex >= 0 && Time.realtimeSinceStartup > _quitArmedUntil) _quitArmedIndex = -1;

            // 上下走列表、左右翻页、回车激活、Esc 关菜单：和游戏自己的列表一个手感。
            // 数字键保留（老玩家习惯），但不作为唯一入口，也不在播报里教。
            if (KeyEdge.Pressed(KeyCode.DownArrow)) { MoveMenu(1); return; }
            if (KeyEdge.Pressed(KeyCode.UpArrow)) { MoveMenu(-1); return; }
            if (KeyEdge.Pressed(KeyCode.RightArrow)) { ChangeMenuPage(1); return; }
            if (KeyEdge.Pressed(KeyCode.LeftArrow)) { ChangeMenuPage(-1); return; }
            if (KeyEdge.Pressed(KeyCode.Return) || KeyEdge.Pressed(KeyCode.KeypadEnter)
                || KeyEdge.Pressed(KeyCode.Space))
            {
                ActivateMenuIndex(_menuIndex);
                return;
            }
            if (KeyEdge.Pressed(KeyCode.Escape)) { CloseMenu(); return; }

            int digit = Choices.ReadMenuDigit();
            if (digit > 0) PickMenu(digit);
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

            // F2 的价值是「我迷路了，告诉我现在在哪」。焦点层进着时优先报它的一句话状态
            // （面板名、共几项、当前项），比「当前面板：X，N 个可操作项」更能让人找回位置。
            // 剧情进行中：F2 要说清「现在在剧情里、按什么继续」，而不是谎报「没有打开的面板」
            if (Reader.InStory)
            {
                // 剧情期的按键本来就全归游戏，F2 只需要告诉玩家「你在剧情里」，
                // 不用再教怎么推进（回车/空格/方向键在剧情里是常识）。
                sb.Append("剧情中。");
                string panelLine = Focus.SpeakSummary();
                if (!string.IsNullOrEmpty(panelLine)) sb.Append(" 面板：").Append(panelLine).Append("。");
                string pomo3 = PomodoroSummary();
                if (!string.IsNullOrEmpty(pomo3)) sb.Append(pomo3);
                Announcer.Now(sb.ToString());
                return;
            }

            string focusLine = Focus.SpeakSummary();
            if (!string.IsNullOrEmpty(focusLine))
            {
                sb.Append(focusLine).Append("。");
            }
            else
            {
                string id = CurrentPanelId();
                if (string.IsNullOrEmpty(id))
                    sb.Append("当前没有打开的面板。按方向键可以浏览主界面。");
                else
                    sb.Append("当前面板：").Append(PanelCn(id)).Append("，")
                      .Append(CountOperable(id)).Append(" 个可操作项。");
            }

            string pomo = PomodoroSummary();
            if (!string.IsNullOrEmpty(pomo)) sb.Append(pomo);
            Announcer.Now(sb.ToString());
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
