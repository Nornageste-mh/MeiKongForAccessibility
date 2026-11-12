using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// 界面焦点层（0.2.1 起）：拟态原生输入。
    ///
    /// === 它做什么 ===
    /// 面板打开时，把「当前项」放到 <c>EventSystem.currentSelectedGameObject</c>（uGUI 自己的选中态，
    /// 鼠标点击本来就写这个字段），方向键走 <c>Selectable.FindSelectableOn*</c>（游戏给控件的 Navigation 配置），
    /// 回车/空格走 <c>ExecuteEvents.submitHandler</c> → <c>Button.OnSubmit → onClick</c>（与鼠标点击同一条路）。
    /// 补丁**不建**自己的索引表、不扫全场景、不写 <c>sendNavigationEvents</c>、不加任何可见元素、不加模式键。
    ///
    /// === 0.2.1 相对 0.2.0 改了什么（用户验收「导航内容不全 / 不符合盲人逻辑 / 依赖识别错误」） ===
    ///   · 全量：不再只收「interactable 的控件」。未解锁/被禁用的控件也进导航，念「当前不可用」——
    ///     盲人玩家需要知道「这里有东西但现在用不了」，而不是不知道存在。
    ///   · 分组：每项都带分组名（游戏自己画的「显示设置」「音量设置」「环境音效」…），
    ///     跨组时先念组名，符合读屏软件的「分组/列表」习惯。
    ///   · 角色+状态：按钮/开关/滑条/输入框，开关念开或关、滑条念值、输入框念内容。
    ///   · 列表：一组里 ≥3 项时念「第 N 项，共 M 项」，让玩家知道自己在列表的哪里。
    ///   · 去重：嵌套重复的按钮（背景卡片在三个嵌套节点上各有一份）只保留一个；
    ///     同位置同尺寸的重复控件只保留一个。
    ///   · 依赖：每面板一句「怎么用」提示（来自反编译依赖链 + 截图核对），打开面板时念一次；
    ///     提交动作之后如果出现了新控件（应用/还原、确认/还原、停止/暂停/跳过…），主动念「现在可以：…」。
    ///   · 主界面：没有面板时按方向键就进入主界面浏览（功能条、快捷栏、唱片机），回车打开；
    ///     剧情开始自动退出，不会抢空格推进。
    /// </summary>
    internal static class Focus
    {
        internal enum Kind { None, Scene, Story, UiManager, PetPanel, Minigame }

        /// <summary>一个可导航项。全部字段都在采集时从游戏对象现读，不跨帧缓存（面板关了就丢）。</summary>
        internal sealed class Item
        {
            public Selectable S;
            public string Label;
            public string Role;
            public string State;
            public bool Locked;
            public string Section;
            public int Index;      // 组内序号（1 起）
            public int Count;      // 组内总数
            public bool InList;    // 组内 ≥3 项

            // 分块导航（0.2.2.0，用户 m05466）：块名 + 块序号 + 在全部项里的下标。
            // 「块」是按语义预先定死的（横向功能条 / 竖向图标列 / 唱片机 / 互动 / 面板名 / 对话选项），
            // 不是自己扫出来的分组；Tab 只切块，方向键只在块内走。
            public string BlockName;
            public int Block;
            public int Global;

            // 虚拟项：游戏里没有 Selectable，但玩家应该能用方向键走到的交互目标
            // （诗萌身体、和诗萌聊聊气泡）。它们不进 EventSystem 选中态，
            // 激活交给游戏自己的动作函数（不拄逻辑、不模拟鼠标）。
            public GameObject Go;
            public Action Activate;
            public bool Virtual;
        }

        // ==================== 分块导航（Tab） ====================
        // 用户口径：「横向一块、竖向一块、每个面板单独一块，按 Tab 在它们之间切换」。
        // 块按语义预设，不新增模式键：Tab 只在「已经能导航」的状态下换块，和 Windows 原生列表一致。
        private sealed class BlockInfo
        {
            public string Name;
            public readonly List<int> Items = new List<int>();
        }
        private static readonly List<BlockInfo> _blocks = new List<BlockInfo>();
        private static int _blockIndex;
        private static Transform _sceneRoot;

        internal static Transform Root;
        internal static string Title = "";
        internal static Kind Domain = Kind.None;
        internal static string DomainId = "";
        private static float _rootGoneSince;
        // 小游戏进行中收到的面板进入请求：等小游戏结束再落焦点
        private static Transform _pendingRoot;
        private static string _pendingTitle;
        private static Kind _pendingKind;
        private static string _pendingId;
        internal static bool Active { get { return Root != null; } }
        internal static bool IsPanelActive { get { return Active && (Domain == Kind.UiManager || Domain == Kind.PetPanel); } }
        internal static int GeometricFallbacks;

        private static string _specKey = "";
        private static string _hint = "";
        private static int _gen;
        private static readonly List<Item> _items = new List<Item>();

        // 虚拟项：游戏没给它们 Selectable（诗萌身体、和诗萌聊聊气泡）。
        // “另一部分玩家希望一切都能导航”（m04680）要求这两个入口不能只剩热键。
        private static readonly List<Item> _extras = new List<Item>();
        private static Item _virtualCur;

        // 面板展开动画期（PlayOpen 期间 CanvasGroup.interactable=false，此刻设焦点会被吞）：
        // 只在「刚打开」的这 1.5 秒里有界重试，不是常驻轮询。
        private static float _settleUntil;
        private static Transform _settleRoot;
        private static string _settleTitle, _settleId;
        private static Kind _settleKind;

        // 关面板后等游戏自己的返回栈出栈（CloseOrGoBack 是协程）
        private static bool _restorePending;
        private static float _restoreUntil;
        private static bool _announcedEmpty;

        // 提交之后「冒出来的新控件」：0.5 秒后看一次差异并播报（应用/还原、确认/还原、停止/暂停/跳过）
        private static float _afterActionAt;
        private static readonly List<string> _beforeActionLabels = new List<string>();
        private static readonly List<string> _beforeActionTexts = new List<string>();
        private static string _lastActivatedLabel = "";

        // 界面换掉之后落到新分组：刚重建出来的项有时还是「不可用」（游戏下一帧才 enable），
        // 这时先落上去播报分组与项名，再在 0.4 秒后补一次「可用项」重定位，不重复念。
        private static float _sectionRetryAt;
        private static float _sectionRetryUntil;
        private static string _sectionRetryName = "";

        // 剧情期间面板焦点挂起：剧情播完补报一次「回到 X，当前是 Y」。
        private static bool _storyWasActive;

        // 动作结果反馈：开关/滑条按下去之后游戏不回话，读屏用户听不到「开了没有」。
        // 记下激活前的状态，0.5 秒后比对同一个控件的状态，变了就念出来。
        private static Selectable _activatedSelectable;
        private static string _beforeActivatedState = "";

        // 音乐列表：点曲目游戏静默开播，用游戏自己的 CurrentEntry 做「正在播放」确认
        private static bool _musicFeedbackWanted;
        // 唱片机：记住上一次念过的曲目，游戏自己换歌（播完自动下一首、随机播放）时补一句
        private static string _lastMusicTitle = "";
        private static float _musicPollAt;

        // 场景域：懒进入（按方向键才进），剧情一开始立刻退出
        private static bool _sceneWanted;

        // ==================== 进入/退出 ====================

        internal static void Enter(Transform root, string title, Kind kind, string id)
        {
            if (root == null) return;
            try
            {
                // 同一个根重复进入（OpenPanel 内部会再调 PresentOpen，两条补丁都会通知到这里）
                if (Active && Root == root && Domain == kind) return;

                if (Minigame.IsPlaying())
                {
                    _pendingRoot = root;
                    _pendingTitle = title;
                    _pendingKind = kind;
                    _pendingId = id;
                    A11yHost.Diag("[焦点] 小游戏进行中，网格由 Minigame 接管；面板焦点等小游戏结束再进");
                    return;
                }
                Root = root;
                Domain = kind;
                DomainId = id ?? "";
                _specKey = PanelMap.KeyOf(root.gameObject.name);
                if (string.IsNullOrEmpty(_specKey) && kind == Kind.Scene) _specKey = "scene";
                Title = !string.IsNullOrEmpty(title) ? title : PanelMap.TitleOf(_specKey);
                if (string.IsNullOrEmpty(Title)) Title = TitleOf(root);
                _hint = kind == Kind.Scene ? "" : PanelMap.HintOf(_specKey);
                _gen++;
                _settleUntil = 0f;
                _restorePending = false;
                _announcedEmpty = false;
                _afterActionAt = 0f;
                _sectionRetryAt = 0f;
                _sectionRetryName = "";
                _lastActivatedLabel = string.Empty;
                _activatedSelectable = null;
                _beforeActivatedState = "";
                _musicFeedbackWanted = false;
                // 进门时把「当前曲目」记成基线：之后换歌才播报，进门不额外念一句
                _lastMusicTitle = MusicCurrentTitle() ?? "";
                _musicPollAt = 0f;
                _sceneWanted = kind == Kind.Scene;
                if (kind == Kind.Scene) _sceneRoot = root;

                A11yHost.Diag("[焦点] 进入 " + Title + "（" + Domain + " " + DomainId + "）");
                if (!TryFocusFirst(false))
                {
                    // 面板内容还没建好（OpenUI 是入队的、PetPanel 有淡入动画）：
                    // 这段时间**不要**念「没有可操作的控件」，否则会「先说没有、再说有 30 项」。
                    _settleUntil = Time.realtimeSinceStartup + 2.5f;
                    _settleRoot = root;
                    _settleTitle = Title;
                    _settleKind = kind;
                    _settleId = DomainId;
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 进入异常: " + e.Message); }
        }

        internal static void Leave()
        {
            _virtualCur = null;
            if (_editing != null) EndEditing(false);
            Root = null;
            Domain = Kind.None;
            DomainId = "";
            Title = "";
            _specKey = "";
            _hint = "";
            _settleUntil = 0f;
            _restorePending = false;
            _afterActionAt = 0f;
            _sectionRetryAt = 0f;
            _sectionRetryName = "";
            _announcedEmpty = false;
            _lastActivatedLabel = string.Empty;
                _activatedSelectable = null;
                _beforeActivatedState = "";
                _musicFeedbackWanted = false;
            _sceneWanted = false;
            _gen++;
            _items.Clear();
            ClearSelection();
        }

        /// <summary>关面板（协程出栈之后）把焦点交还给游戏返回栈的栈顶。</summary>
        internal static void LeaveWithRestore(float windowSeconds)
        {
            Leave();
            _restorePending = true;
            _restoreUntil = Time.realtimeSinceStartup + Mathf.Max(0.2f, windowSeconds);
            A11yHost.Diag("[焦点] 等关面板完成，再把焦点交还返回栈");
        }

        internal static void RestoreTo(GameObject go)
        {
            _restorePending = false;
            try
            {
                if (go == null) return;
                var sel = go.GetComponentInChildren<Selectable>(true);
                var es = EventSystem.current;
                if (es == null || sel == null) return;
                es.SetSelectedGameObject(sel.gameObject);
                A11yHost.Diag("[焦点] 焦点交还 " + go.name);
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 交还异常: " + e.Message); }
        }

        // ==================== 每帧 ====================

        // ==================== 剧情界面（主线/对话场景的操作栏） ====================
        // 剧情场景的按钮栏（存档、读档、音乐、画廊、设置、CG、历史…）不是通过 UIManager.OpenUI 打开的，
        // 它就是场景里常驻的一排按钮，所以面板事件钩子永远等不到它。
        // 这里用「游戏自己的对话行事件」当入口信号（每句台词游戏都会通知我们），只在那时确认一次域，
        // 不做每帧扫屏；离开同样由剧情结束事件/根失效触发。

        // ==================== 对话选项（剧情里的选择） ====================
        // 选项是游戏自己 Instantiate 的真 Button（ShiMeng.DialogueV2.DialogueChoiceItemView），
        // 但游戏没给它们任何焦点模型，键盘玩家只能靠补丁。这里把选项容器当成一个「块」：
        // 玩家听到「对话选项，共 N 项」，↑↓ 在选项间走、Tab 切块、回车走游戏自己的 onClick
        // （激活第 1/2 级，和鼠标同一条路）；空格推进与数字直选仍归游戏。
        private static void TickStoryChoices()
        {
            try
            {
                if (!Reader.InStory)
                {
                    if (Domain == Kind.Story && DomainId == "choices") Leave();
                    return;
                }
                if (Domain == Kind.PetPanel || Domain == Kind.UiManager || Domain == Kind.Minigame) return;
                if (Domain == Kind.Story && DomainId == "choices")
                {
                    if (Root == null || !Root.gameObject.activeInHierarchy) { Leave(); return; }
                    if (CountChoiceViews(Root) == 0)
                    {
                        A11yHost.Diag("[焦点] 选项已经选完，退出选项块");
                        Leave();
                    }
                    return;
                }
                var container = FindChoiceContainer();
                if (container == null) return;
                // 选项刚创建时还在 reveal 淡入（alpha 从 0 起），此时 Collect 一个也收不到，
                // 会先念一句「对话选项，共 0 项」。等游戏把按钮设成可用（reveal 结束、
                // SetInteractable(true) 已调用）再进门，一次就把「共 N 项」念对。
                if (CountUsableChoiceViews(container) == 0) return;
                if (Domain == Kind.Story) Leave();   // 从「对话操作栏」切到「对话选项」
                Enter(container, "对话选项", Kind.Story, "choices");
                if (Active && Root == container) TryFocusFirst(true);   // 进门要出声：对话选项，共 N 项
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 选项域异常: " + e.Message); }
        }

        private static int CountChoiceViews(Transform root)
        {
            int n = 0;
            try
            {
                var views = root.GetComponentsInChildren<ShiMeng.DialogueV2.DialogueChoiceItemView>(true);
                for (int i = 0; i < views.Length; i++)
                {
                    var v = views[i];
                    if (v != null && v.gameObject.activeInHierarchy) n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>选项里「已经能按」的有几个（reveal 结束、button.interactable=true）。</summary>
        private static int CountUsableChoiceViews(Transform root)
        {
            int n = 0;
            try
            {
                var views = root.GetComponentsInChildren<ShiMeng.DialogueV2.DialogueChoiceItemView>(true);
                for (int i = 0; i < views.Length; i++)
                {
                    var v = views[i];
                    if (v == null || !v.gameObject.activeInHierarchy) continue;
                    var buttons = v.GetComponentsInChildren<Button>(true);
                    for (int j = 0; j < buttons.Length; j++)
                    {
                        if (buttons[j] != null && buttons[j].IsInteractable()) { n++; break; }
                    }
                }
            }
            catch { }
            return n;
        }

        private static Transform FindChoiceContainer()
        {
            try
            {
                var views = Resources.FindObjectsOfTypeAll<ShiMeng.DialogueV2.DialogueChoiceItemView>();
                for (int i = 0; i < views.Length; i++)
                {
                    var v = views[i];
                    if (v == null || !v.gameObject.activeInHierarchy) continue;
                    var p = v.transform.parent;
                    if (p == null) continue;
                    return p;
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 找选项容器异常: " + e.Message); }
            return null;
        }

        private static Transform FindStoryBar()
        {
            try
            {
                var mu = UnityEngine.Object.FindObjectOfType<MainUIManager>();
                if (mu == null || mu.Buttons == null) return null;
                if (!mu.Buttons.activeInHierarchy) return null;
                return mu.Buttons.transform;
            }
            catch { return null; }
        }

        /// <summary>游戏每换一句台词调用一次：剧情中保证操作栏可导航，剧情结束就退出。</summary>
        internal static void OnStoryLine()
        {
            try
            {
                if (!Reader.InDialogue || !Reader.InStory)
                {
                    if (Domain == Kind.Story) Leave();
                    return;
                }
                if (Domain == Kind.PetPanel || Domain == Kind.UiManager || Domain == Kind.Minigame) return;
                if (Domain == Kind.Story && Root != null) return;
                var bar = FindStoryBar();
                if (bar == null) return;
                Enter(bar, "对话操作栏", Kind.Story, "story");
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 剧情界面异常: " + e.Message); }
        }
        internal static void Tick()
        {
            try
            {
                PumpEditing();
                // 0) 「和诗萌聊聊」气泡冒出来时说一句（只读镜像，见 PollStoryBubble 的说明）
                PollStoryBubble();
                // 0) 虚拟项的目标没了（气泡收起 / 换了面板 / 剧情结束）：别让看不见的头占着当前项
                if (_virtualCur != null && (_virtualCur.Go == null || !_virtualCur.Go.activeInHierarchy))
                {
                    A11yHost.Diag("[焦点] 虚拟项已消失：" + _virtualCur.Label);
                    _virtualCur = null;
                    if (Active) TryFocusFirst(false);
                }
                // 0) 剧情播完了：把「现在在哪、当前是什么」补报一次。
                // 剧情期间按键让给了游戏（HandleKeys 直接返回），玩家会失去上下文。
                if (_storyWasActive && !Reader.InStory)
                {
                    _storyWasActive = false;
                    if (Active && (Domain == Kind.PetPanel || Domain == Kind.UiManager))
                    {
                        var cur0 = Current;
                        if (cur0 != null && ItemOf(cur0) != null)
                        {
                            A11yHost.Diag("[焦点] 剧情结束，回到面板：" + Title);
                            Announcer.Now("回到" + (string.IsNullOrEmpty(Title) ? "面板" : Title)
                                          + "，当前：" + Describe(ItemOf(cur0), false) + "。");
                        }
                    }
                }
                if (Reader.InStory) _storyWasActive = true;
                // 0) 剧情里出现选项时，把「选项」做成一个块（游戏没给它们焦点模型）
                TickStoryChoices();
                // 0) 挂起的进入请求：小游戏结束后补落面板焦点
                if (_pendingRoot != null)
                {
                    if (!Minigame.IsPlaying())
                    {
                        var pr = _pendingRoot; _pendingRoot = null;
                        A11yHost.Diag("[焦点] 小游戏已结束，补落面板焦点");
                        Enter(pr, _pendingTitle, _pendingKind, _pendingId);
                    }
                    return;
                }

                // 0) 小游戏一旦开局，整层让位（网格由 Minigame 自己接管）
                if (Active && Minigame.IsPlaying())
                {
                    // 让位前把域记成挂起：一局打完（Session 停）后 Tick 会把它补回来，
                    // 这样面板上的关闭按钮又能用方向键摸到。
                    _pendingRoot = Root;
                    _pendingTitle = Title;
                    _pendingKind = Domain;
                    _pendingId = DomainId;
                    A11yHost.Diag("[焦点] 小游戏已开局，焦点层让位给 Minigame（打完自动接回）");
                    Leave();
                    return;
                }

                // 1) 剧情一开始，退出主界面浏览（否则回车会去点主界面图标，而不是推进剧情）
                if (Active && Domain == Kind.Scene && Reader.InStory)
                {
                    A11yHost.Diag("[焦点] 剧情开始，退出主界面浏览");
                    Leave();
                    return;
                }

                // 1.5) 域根被游戏自己关掉了（例如剧情开始会 CloseAllFeaturePanels，
                //      这不是走 ClosePanel 事件关的，焦点层收不到通知）→ 有界确认后退出，
                //      否则会卡在一个「共 0 项」的死域里，方向键全部无效。
                if (Active)
                {
                    bool gone = Root == null || !Root.gameObject.activeInHierarchy || AlphaOf(Root) < 0.05f;
                    if (gone)
                    {
                        if (_rootGoneSince <= 0f) _rootGoneSince = Time.realtimeSinceStartup;
                        else if (Time.realtimeSinceStartup - _rootGoneSince > 0.6f)
                        {
                            A11yHost.Diag("[焦点] 域根已被游戏关闭，退出焦点（" + Domain + " " + DomainId + "）");
                            _rootGoneSince = 0f;
                            Leave();
                            return;
                        }
                    }
                    else _rootGoneSince = 0f;
                }
                // 2) 面板刚打开、内容还没建好：有界重试
                if (_settleUntil > 0f && Time.realtimeSinceStartup <= _settleUntil)
                {
                    if (_settleRoot != null && _settleRoot.gameObject.activeInHierarchy
                        && AlphaOf(_settleRoot) >= 0.99f)
                    {
                        Root = _settleRoot;
                        Domain = _settleKind;
                        DomainId = _settleId;
                        Title = _settleTitle;
                        // 淡入结束还不够：游戏在 PlayOpen 期间把 CanvasGroup.interactable 置 false，
                        // 那一刻所有控件都「不可用」，等它放开再聚焦，否则第一句就是「当前不可用」。
                        Collect();
                        if (_items.Count == 0) { /* 继续等 */ }
                        else if (AnyInteractable()) { if (TryFocusFirst(true)) _settleUntil = 0f; }
                        else if (Time.realtimeSinceStartup > _settleUntil)
                        {
                            // 等到底也没有可用项（整块禁用/纯展示面板）：照样落焦点，如实念「当前不可用」
                            if (TryFocusFirst(true)) _settleUntil = 0f;
                        }
                    }
                }
                else if (_settleUntil > 0f)
                {
                    _settleUntil = 0f;
                    if (Root != null)
                    {
                        Collect();
                        if (_items.Count == 0) AnnounceEmpty();
                        else if (TryFocusFirst(true)) { /* 已聚焦并播报 */ }
                    }
                }

                // 3) 关面板后交还焦点
                if (_restorePending)
                {
                    if (Time.realtimeSinceStartup > _restoreUntil) { _restorePending = false; }
                    else if (!UiBusy())
                    {
                        var top = UiStackTop();
                        if (top != null) RestoreTo(top);
                        else _restorePending = false;
                    }
                }

                // 4) 不在域内：清掉鼠标留下的选中项（否则关面板后回车会点到看不见的按钮）
                if (!Active) ClearSelection();

                // 5) 动作之后冒出的新控件
                if (_afterActionAt > 0f && Time.realtimeSinceStartup >= _afterActionAt)
                {
                    _afterActionAt = 0f;
                    AnnounceNewItems();
                }

                // 5.5) 界面刚换完：新项常常晚一帧才 enable，补一次「可用项」定位
                if (_sectionRetryAt > 0f && Time.realtimeSinceStartup >= _sectionRetryAt)
                {
                    _sectionRetryAt = 0f;
                    bool moved = false;
                    try
                    {
                        Collect();
                        for (int i = 0; i < _items.Count; i++)
                        {
                            var it = _items[i];
                            if (!string.Equals(it.Section, _sectionRetryName, StringComparison.Ordinal)) continue;
                            if (it.Locked || IsDismissive(it.S)) continue;
                            if (ReferenceEquals(it, CurrentItem)) break;
                            MoveTo(it.S, "动作后");
                            A11yHost.Diag("[焦点] 换页后的可用项就绪，补落：" + it.Label);
                            moved = true;
                            break;
                        }
                    }
                    catch (Exception e) { A11yHost.Diag("[焦点] 补落异常: " + e.Message); }
                    if (!moved && Time.realtimeSinceStartup < _sectionRetryUntil)
                        _sectionRetryAt = Time.realtimeSinceStartup + 0.3f;   // 还没 enable，再等一帧
                    else
                        _sectionRetryName = "";
                }

                // 6) 唱片机自己换了歌（播完自动下一首/随机播放）时补一句
                PollMusicChange();
            }
            catch (Exception e) { A11yHost.Diag("[焦点] Tick 异常: " + e.Message); }
        }

        // ==================== 按键 ====================

        internal static bool HandleKeys()
        {
            try
            {
                // 剧情正在播（面板没关也会播 tool 剧情，例如「日程完成」「番茄钟启动」）：
                // 这时回车/空格属于**游戏**（推进剧情），补丁不能抢着去激活面板控件，
                // 否则玩家听到一句台词、按回车想continue，结果误触了面板上的按钮。
                if (Reader.InStory)
                {
                    if (_editing != null) EndEditing(true);
                    // 对话选项块：↑↓ 在选项之间走、Tab 切块、回车走游戏自己的 onClick。
                    // 空格（推进）、数字键（直选）仍归游戏，所以这里只接管这四个键。
                    if (Domain == Kind.Story && DomainId == "choices" && Active)
                    {
                        if (KeyEdge.Pressed(KeyCode.Tab)) { CycleBlock(+1); return true; }
                        if (KeyEdge.Pressed(KeyCode.UpArrow)) { MoveInBlock(-1); return true; }
                        if (KeyEdge.Pressed(KeyCode.DownArrow)) { MoveInBlock(+1); return true; }
                        if (KeyEdge.Pressed(KeyCode.Return) || KeyEdge.Pressed(KeyCode.KeypadEnter))
                        {
                            ActivateCurrent();
                            return true;
                        }
                    }
                    return false;
                }
                // 正在输入框里打字：所有键都交给游戏（TMP_InputField 自己处理光标/输入法），
                // 只有 Esc 是「打完收工」——否则玩家会被困在输入框里出不来。
                if (_editing != null)
                {
                    if (Input.GetKeyDown(KeyCode.Escape)) { EndEditing(true); return true; }
                    if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { _editing = null; }
                    string ins = Input.inputString;
                    if (!string.IsNullOrEmpty(ins))
                    {
                        if (_editPending.Length == 0) _editBefore = _editing.text ?? "";
                        _editPending += ins;
                        _editCheckAt = Time.realtimeSinceStartup + 0.3f;
                        string echo = ins.Replace("\b", "删除").Replace("\n", "").Replace("\r", "");
                        if (echo.Length > 0) Announcer.Now(echo);
                    }
                    return false;
                }

                if (!Active)
                {
                    // 主界面：没有面板、没有剧情时，按方向键开始浏览图标（不需要任何模式键）
                    bool arrow = AnyArrowPressed();
                    bool rawArrow = Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.DownArrow)
                                    || Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow);
                    if (arrow || rawArrow)
                        A11yHost.Diag("[焦点] 方向键到达 raw=" + rawArrow + " edge=" + arrow
                                      + " InDialogue=" + Reader.InDialogue + " InStory=" + Reader.InStory + " mini=" + Minigame.IsPlaying());
                    bool tabKey = KeyEdge.Pressed(KeyCode.Tab);
                    if (!Reader.InStory && !Minigame.IsPlaying() && (arrow || tabKey))
                    {
                        EnterScene();
                        if (tabKey) CycleBlock(+1);
                        return true;
                    }
                    return false;
                }

                // 分块导航（用户 m05466）：Tab 在块之间切换，↑↓ 在**当前块内**逐项移动，
                // ←→ 只对滑条调值（不做横向移动）。块内走到头就绕回另一端（用户口径「块内循环」）。
                if (KeyEdge.Pressed(KeyCode.Tab)) { CycleBlock(+1); return true; }
                if (KeyEdge.Pressed(KeyCode.UpArrow)) { MoveInBlock(-1); return true; }
                if (KeyEdge.Pressed(KeyCode.DownArrow)) { MoveInBlock(+1); return true; }
                bool leftKey = KeyEdge.Pressed(KeyCode.LeftArrow);
                bool rightKey = KeyEdge.Pressed(KeyCode.RightArrow);
                if (leftKey || rightKey)
                {
                    if (AdjustSlider(rightKey ? +1 : -1)) return true;
                    return false;
                }

                // 长列表（唱片 25 首、鬼畜制造机 52 个工程）一格一格挪太慢：Home/End 到首尾。
                // PageUp/PageDown 已按《痴情妹妹》的键概念删掉（参考只有 Home/End，多出来的键
                // 会让不同作品的手感分叉，见 docs/操作逻辑公约.md）。
                if (KeyEdge.Pressed(KeyCode.Home)) { JumpTo(0); return true; }
                if (KeyEdge.Pressed(KeyCode.End)) { JumpTo(-1); return true; }

                // 剧情界面里空格/小键盘回车是游戏自己的「推进剧情」，绝不能被我们吃掉；
                // 只有主回车是「按下当前按钮」（浏览与激活分离）。
                if (Domain == Kind.Story)
                {
                    if (KeyEdge.Pressed(KeyCode.Return)) { ActivateCurrent(); return true; }
                    return false;
                }

                if (KeyEdge.Pressed(KeyCode.Return) || KeyEdge.Pressed(KeyCode.KeypadEnter)
                    || KeyEdge.Pressed(KeyCode.Space))
                {
                    ActivateCurrent();
                    return true;
                }

                // Esc 一律交游戏（参考实现里 Esc 从不被补丁拦：它是游戏自己的「取消/返回」）。
                // 旧版主界面那声「退出浏览。」是补丁自造的模式语义，0.2.1.0 起删掉。
                if (KeyEdge.Pressed(KeyCode.Escape))
                {
                    if (Domain == Kind.PetPanel && !string.IsNullOrEmpty(DomainId))
                    {
                        Surfaces.ClosePanel(DomainId);
                        return true;
                    }
                    // 对话内面板：Esc 归游戏自己的返回（UIManager.CloseOrGoBack），不抢
                    return false;
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 按键异常: " + e.Message); }
            return false;
        }

        private static bool AnyArrowPressed()
        {
            return KeyEdge.Pressed(KeyCode.UpArrow) || KeyEdge.Pressed(KeyCode.DownArrow)
                || KeyEdge.Pressed(KeyCode.LeftArrow) || KeyEdge.Pressed(KeyCode.RightArrow);
        }

        internal static void EnterScene()
        {
            try
            {
                var canvas = FindSceneCanvas();
                if (canvas == null) { A11yHost.Diag("[焦点] 找不到主界面画布，放弃进入"); return; }
                Enter(canvas.transform, "主界面", Kind.Scene, "");
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 进入主界面异常: " + e.Message); }
        }

        /// <summary>临时取证：主界面根节点的解析诊断。</summary>
        internal static string SceneRootDebug()
        {
            try
            {
                var sb = new StringBuilder();
                var root = FindSceneCanvas();
                sb.Append("root=").Append(root == null ? "(null)" : PathOfT(root));
                if (root != null)
                {
                    sb.Append(" selectables=").Append(root.GetComponentsInChildren<Selectable>(true).Length);
                    for (int i = 0; i < root.childCount && i < 20; i++)
                    {
                        var c = root.GetChild(i);
                        sb.Append("\n  child=").Append(c.name)
                          .Append(" active=").Append(c.gameObject.activeInHierarchy)
                          .Append(" a=").Append(AlphaOf(c).ToString("0.00"))
                          .Append(" sel=").Append(c.GetComponentsInChildren<Selectable>(true).Length);
                    }
                }
                // 另外把 FullScreen 下所有子节点也报一遍，便于确认 WindowCanvas 唯一性
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                {
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.name.Trim() != "FullScreen") continue;
                    sb.Append("\nFullScreen children:");
                    for (int i = 0; i < t.childCount && i < 20; i++)
                    {
                        var c = t.GetChild(i);
                        sb.Append("\n  - ").Append(c.name).Append(" sel=").Append(c.GetComponentsInChildren<Selectable>(true).Length);
                    }
                    break;
                }
                return sb.ToString();
            }
            catch (Exception e) { return "scene 异常: " + e.Message; }
        }
        private static Transform FindSceneCanvas()
        {
            try
            {
                var arr = Resources.FindObjectsOfTypeAll<Transform>();

                // 1) 首选 dump 里确认过的路径：.../FullScreen/WindowCanvas
                for (int i = 0; i < arr.Length; i++)
                {
                    var t = arr[i];
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    if (t.name.Trim() != "WindowCanvas") continue;
                    if (HasChild(t, "Right") && (HasChild(t, "UpRight") || HasChild(t, "PetMusicPlayer_Window"))) return t;
                }

                // 2) 从功能条图标往上找：那个同时装着快捷栏/唱片机的祖先就是画布
                string[] anchors = { "联动内容", "日程表", "番茄钟", "换装", "设置", "退出", "隐藏UI" };
                for (int a = 0; a < anchors.Length; a++)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var t = arr[i];
                        if (t == null || !t.gameObject.activeInHierarchy) continue;
                        if (t.name.Trim() != anchors[a]) continue;
                        var p = t.parent;
                        int guard = 0;
                        while (p != null && guard++ < 8)
                        {
                            if (HasChild(p, "Right") && (HasChild(p, "UpRight") || HasChild(p, "PetMusicPlayer_Window"))) return p;
                            p = p.parent;
                        }
                        if (t.parent != null) return t.parent;
                    }
                }
            }
            catch { }
            return null;
        }

        private static bool HasChild(Transform parent, string name)
        {
            try
            {
                for (int i = 0; i < parent.childCount; i++)
                    if (parent.GetChild(i).name.Trim() == name) return true;
            }
            catch { }
            return false;
        }

        // ==================== 移动 ====================

        internal static void MoveBy(int dx, int dy)
        {
            var curItem = CurrentItem;
            var cur = curItem != null ? curItem.S : Current;
            // 还没聚焦（面板刚开、或鼠标把选中项清了）：方向键先当作「落进第一项」
            if (curItem == null || _items.Count == 0)
            {
                if (_settleUntil > 0f) return;   // 面板还在淡入，先别急着报「没有控件」
                if (TryFocusFirst(true)) return;
                Announcer.Now("没有可操作项。");
                return;
            }

            string oldSection = curItem.Section;

            // 当前是虚拟项（诗萌身体 / 对话气泡）：游戏没有给它导航图，按屏幕位置找最近的一项。
            // 这是「一次性最近邻查询」，不是常驻索引（与几何回退同一定性，详见交互形态判定）。
            if (cur == null || curItem.Virtual)
            {
                var nx = NearestItemInDirection(curItem, dx, dy);
                if (nx != null) { MoveToItem(nx, oldSection); return; }
                Announcer.Now("到头了。");
                return;
            }

            Selectable next = null;
            if (dx > 0) next = cur.FindSelectableOnRight();
            else if (dx < 0) next = cur.FindSelectableOnLeft();
            else if (dy > 0) next = cur.FindSelectableOnUp();
            else if (dy < 0) next = cur.FindSelectableOnDown();

            if (next != null && InDomain(next)) { MoveTo(next, oldSection); return; }

            // 滑条：左右 = 调值（滑条自己的语义）
            var slider = cur as Slider;
            if (slider != null && dx != 0)
            {
                float step = Mathf.Max((slider.maxValue - slider.minValue) / 20f, 0.0001f);
                slider.value = Mathf.Clamp(slider.value + dx * step, slider.minValue, slider.maxValue);
                var it = ItemOf(slider);
                Refresh(it);
                Announce(it, false);
                return;
            }

            // 几何回退：只在游戏自己的 Navigation 图给不出答案时用一次「最近邻查询」
            var geo = NearestInDirection(cur, dx, dy);
            if (geo != null)
            {
                GeometricFallbacks++;
                A11yHost.Diag("[焦点] 几何回退 #" + GeometricFallbacks + " " + geo.gameObject.name);
                MoveTo(geo, oldSection);
                return;
            }

            // 游戏导航图和真控件都给不出答案时才看虚拟项：气泡在诗萌身侧、诗萌在屏幕中部，
            // 从图标区往这两个方向走，通常就落在它们身上。
            var vx = NearestItemInDirection(curItem, dx, dy);
            if (vx != null) { MoveToItem(vx, oldSection); return; }
            Announcer.Now("到头了。");
        }

        // ==================== 分块移动（Tab / ↑↓） ====================

        /// <summary>只在当前块内逐项移动（块内循环：到头绕回另一端）。</summary>
        internal static void MoveInBlock(int delta)
        {
            try
            {
                var curItem = CurrentItem;
                if (curItem == null)
                {
                    if (_settleUntil > 0f) return;   // 面板还在淡入，先别急着报「没有控件」
                    if (TryFocusFirst(true)) return;
                    Announcer.Now("没有可操作项。");
                    return;
                }
                Collect();
                var cur2 = CurrentItem;
                if (cur2 == null || _blocks.Count == 0) return;
                int bi = cur2.Block;
                if (bi < 0 || bi >= _blocks.Count) bi = 0;
                var list = _blocks[bi].Items;
                if (list.Count == 0) return;
                int pos = list.IndexOf(cur2.Global);
                if (pos < 0) pos = 0;
                int n = list.Count;
                int np = ((pos + delta) % n + n) % n;
                var target = _items[list[np]];
                _blockIndex = bi;
                SelectItemSilent(target);
                Announce(target, false);
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 块内移动异常: " + e.Message); }
        }

        /// <summary>Tab：在「语义块」之间切换（横向功能条 / 竖向图标列 / 唱片机 / 互动 / 面板 / 对话选项）。</summary>
        internal static void CycleBlock(int delta)
        {
            try
            {
                if (!Active) { EnterScene(); if (!Active) return; }
                Collect();
                if (_blocks.Count == 0) { Announcer.Now("没有可操作项。"); return; }
                if (_blocks.Count == 1)
                {
                    var only = _blocks[0];
                    Item one = null;
                    for (int i = 0; i < only.Items.Count; i++)
                    {
                        var it = _items[only.Items[i]];
                        if (it.S != null && IsDismissive(it.S)) continue;
                        one = it;
                        break;
                    }
                    Announcer.Now(only.Name + "，共 " + only.Items.Count + " 项。当前："
                                  + (one != null ? Describe(one, false) : "没有可操作项") + "。");
                    return;
                }
                var curItem = CurrentItem;
                int bi = curItem != null ? curItem.Block : _blockIndex;
                if (bi < 0 || bi >= _blocks.Count) bi = 0;
                int nb = ((bi + delta) % _blocks.Count + _blocks.Count) % _blocks.Count;
                _blockIndex = nb;
                var blk = _blocks[nb];
                Item target = null;
                for (int i = 0; i < blk.Items.Count; i++)
                {
                    var it = _items[blk.Items[i]];
                    if (it.S != null && IsDismissive(it.S)) continue;
                    target = it;
                    break;
                }
                if (target == null) { Announcer.Now(blk.Name + "，" + blk.Items.Count + " 项，没有可操作项。"); return; }
                SelectItemSilent(target);
                Announcer.Now(blk.Name + "，共 " + blk.Items.Count + " 项。当前：" + Describe(target, false) + "。");
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 切块异常: " + e.Message); }
        }

        /// <summary>←→ 只对滑条生效（用户口径：左右方向键只调整滑条）。</summary>
        private static bool AdjustSlider(int dir)
        {
            try
            {
                var it = CurrentItem;
                var s = it != null ? it.S : Current;
                var slider = s as Slider;
                if (slider == null) return false;
                float step = Mathf.Max((slider.maxValue - slider.minValue) / 20f, 0.0001f);
                slider.value = Mathf.Clamp(slider.value + dir * step, slider.minValue, slider.maxValue);
                var sit = ItemOf(slider);
                Refresh(sit);
                Announce(sit, false);
                return true;
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 滑条调整异常: " + e.Message); return false; }
        }

        /// <summary>探针用：左右方向键的滑条调整。</summary>
        internal static bool DebugAdjustSlider(int dir) { return AdjustSlider(dir); }

        /// <summary>把选中表示设过去但不播报（切块后统一播一次块摘要，避免两句话打架）。</summary>
        private static void SelectItemSilent(Item it)
        {
            if (it == null) return;
            _blockIndex = it.Block;
            if (it.S != null)
            {
                _virtualCur = null;
                var es = EventSystem.current;
                if (es != null) es.SetSelectedGameObject(it.S.gameObject);
            }
            else
            {
                ClearSelection();
                _virtualCur = it;
            }
        }

        private static void MoveTo(Selectable s, string oldSection)
        {
            _virtualCur = null;
            var es = EventSystem.current;
            if (es == null) return;
            es.SetSelectedGameObject(s.gameObject);
            var it = ItemOf(s);
            if (it == null) { Collect(); it = ItemOf(s); }
            if (it != null) _blockIndex = it.Block;
            bool sectionChanged = it != null && !string.IsNullOrEmpty(it.Section) && it.Section != oldSection;
            A11yHost.Diag("[焦点] 选中 " + s.gameObject.name + (it != null ? "（" + it.Section + (it.InList ? " 第" + it.Index + "/" + it.Count : "") + "）" : ""));
            Announce(it, sectionChanged);
        }

        /// <summary>只把选中项设过去，不播报（用于「原项还在原地」的收尾）。</summary>
        private static void MoveToSilent(Selectable s)
        {
            var es = EventSystem.current;
            if (es == null || s == null) return;
            es.SetSelectedGameObject(s.gameObject);
            A11yHost.Diag("[焦点] 选中 " + s.gameObject.name + "（静默）");
        }

        // ==================== 提交 ====================

        /// <summary>跳到第 index 项（-1 = 最后一项）。只在当前采集到的列表里移动，不缓存。</summary>
        internal static void JumpTo(int index)
        {
            try
            {
                Collect();
                if (_items.Count == 0) { Announcer.Now("没有可操作项。"); return; }
                int target = index < 0 ? _items.Count - 1 : Mathf.Clamp(index, 0, _items.Count - 1);
                var it = _items[target];
                MoveTo(it.S, "跳转");
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 跳转异常: " + e.Message); }
        }

        private static bool _quitArmed;
        private static float _quitArmedUntil;

        /// <summary>这一项是不是「退出游戏」。默认按标签判，也能用配置里的对象名（CfgQuitNames）兜底。</summary>
        private static bool IsQuitTarget(Selectable s, Item it)
        {
            if (s == null) return false;
            string label = it != null ? (it.Label ?? "") : "";
            if (label == "退出游戏" || label == "退出") return true;
            try
            {
                string names = A11yHost.CfgQuitNames != null ? A11yHost.CfgQuitNames.Value : "退出游戏,退出,Quit";
                if (!string.IsNullOrEmpty(names))
                    foreach (var raw in names.Split(','))
                    {
                        string n = raw.Trim();
                        if (n.Length > 0 && string.Equals(s.gameObject.name, n, StringComparison.Ordinal)) return true;
                    }
            }
            catch { }
            return false;
        }

        /// <summary>相对当前项跳 delta 项（保留给探针/测试用；PageUp/PageDown 已按公约删除）。</summary>
        internal static void JumpBy(int delta)
        {
            try
            {
                Collect();
                if (_items.Count == 0) { Announcer.Now("没有可操作项。"); return; }
                int cur = -1;
                var s = Current;
                for (int i = 0; i < _items.Count; i++) if (_items[i].S == s) { cur = i; break; }
                if (cur < 0) { JumpTo(0); return; }
                JumpTo(Mathf.Clamp(cur + delta, 0, _items.Count - 1));
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 跳转异常: " + e.Message); }
        }
        internal static void ActivateCurrent()
        {
            var curItem0 = CurrentItem;
            if (curItem0 != null && curItem0.Virtual)
            {
                // 虚拟项的动作来自游戏自己的入口：气泡＝游戏按钮的 onClick，
                // 诗萌＝左键点她的等价调用（DialogueEntryClickTarget.FireClick）。
                try { if (curItem0.Activate != null) curItem0.Activate(); }
                catch (Exception e) { A11yHost.Diag("[焦点] 虚拟项激活异常: " + e.Message); }
                return;
            }
            var cur = Current;
            if (cur == null) { Announcer.Now("没有可操作项。"); return; }
            var ciCur = ItemOf(cur);
            if (!cur.IsInteractable())
            {
                Announcer.Now((ciCur != null && !string.IsNullOrEmpty(ciCur.Label) ? ciCur.Label : "这项") + "当前不可用。");
                return;
            }

            // 退出游戏没有任何确认框（PetUiIntegrationHub.QuitGame → RequestGracefulQuit
            // 播完告别对话就直接关进程），键盘玩家误按一次就没了。沿用旧口径：
            // 同一项 5 秒内按两次才真的退，中间选别的项即取消。
            if (IsQuitTarget(cur, ciCur))
            {
                bool needConfirm = A11yHost.CfgQuitConfirm == null || A11yHost.CfgQuitConfirm.Value;
                if (needConfirm)
                {
                    if (!_quitArmed || Time.realtimeSinceStartup > _quitArmedUntil)
                    {
                        _quitArmed = true;
                        _quitArmedUntil = Time.realtimeSinceStartup + 5f;
                        Announcer.Now("再按一次确认退出游戏。");
                        return;
                    }
                    _quitArmed = false;
                }
            }

            if (cur is TMPro.TMP_InputField)
            {
                var ciInput = ItemOf(cur);
                BeginEditing((TMPro.TMP_InputField)cur,
                             PanelMap.NumericInput(_specKey, ciInput != null ? ciInput.Section : null));
                return;
            }

            // 记下动作前的控件标签，0.5 秒后看「冒出来了什么」（应用/还原、确认/还原、停止/暂停/跳过）
            _beforeActionLabels.Clear();
            Collect();
            for (int i = 0; i < _items.Count; i++) _beforeActionLabels.Add(_items[i].Label);
            // 同时记下动作前面板上「不挂在控件上的说明文字」：今天吃啥推荐完只改文字、不加控件，
            // 不比对就会一声不吭（又是「按下去没反应」）。
            _beforeActionTexts.Clear();
            _beforeActionTexts.AddRange(InfoTexts());
            var ci0 = ItemOf(cur);
            _lastActivatedLabel = ci0 != null ? (ci0.Label ?? "") : "";
            _activatedSelectable = cur;
            _beforeActivatedState = ci0 != null ? (ci0.State ?? "") : "";
            _musicFeedbackWanted = _specKey == "music_player" && ci0 != null && ci0.Section == "曲目";
            _afterActionAt = Time.realtimeSinceStartup + 0.5f;

            // 联动展架的作品卡不是「在游戏里打开一个界面」，而是调 Application.OpenURL 把玩家
            // 送出游戏（Steam 商店页 / 浏览器）。盲人看不到窗口切换，必须当场说清楚，
            // 否则会以为「回车没反应」，接着在游戏里乱按（这是「控件依赖」里最容易误判的一类）。
            bool externalLink = false;
            try { externalLink = cur.GetComponent<CollabExternalLinkButton>() != null; } catch { }

            try
            {
                var data = new BaseEventData(EventSystem.current);
                ExecuteEvents.Execute(cur.gameObject, data, ExecuteEvents.submitHandler);
                A11yHost.Diag("[焦点] 提交 " + cur.gameObject.name);
                if (externalLink)
                {
                    string name = ci0 != null && !string.IsNullOrEmpty(ci0.Label) ? ci0.Label : "这个作品";
                    // 外链会把画面切出游戏，回来时只需要知道「刚才点开的是哪个」；
                    // 关面板/报位置的按键别处已经报过，这里不再重复。
                    Announcer.Now("已打开「" + name + "」。");
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 提交异常: " + e.Message); }
        }

        /// <summary>把输入框「点开」——等价于玩家用鼠标点进输入框，然后键盘直接打。</summary>
        private static bool _editNumericOnly;

        private static void BeginEditing(TMPro.TMP_InputField f, bool numericOnly)
        {
            if (f == null) return;
            _editing = f;
            _editRejectedWarned = false;
            _editNumericOnly = numericOnly;
            _editPending = "";
            _editBefore = f.text ?? "";
            try
            {
                // TMP 的键盘输入走 EventSystem 的 selected 对象分发（StandaloneInputModule.SendUpdateSelected），
                // 只调 ActivateInputField 不会让按键进入输入框，必须同时把它设成当前选中对象。
                // 交给补丁自己接管键入（见 PumpEditing 的说明）：本作环境里 TMP 的事件队列收不到注入键，
                // 而 ActivateInputField 又会让 TMP 延迟处理同一批按键导致重复。这里只把选中态摆好（供视觉/IME 用）。
                if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(f.gameObject);
                f.Select();
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 激活输入框异常: " + e.Message); }
            string now = f.text ?? "";
            // 只说「能打字了」和现有内容；怎么退出输入框（Esc）一句话带过。
            Announcer.Now("输入框，可以打字，Esc 结束。"
                   + (string.IsNullOrEmpty(now) ? "" : "现在是：" + now + "。"));
            A11yHost.Diag("[焦点] 进入输入 " + f.gameObject.name
                          + " contentType=" + f.contentType
                          + " validation=" + f.characterValidation
                          + " limit=" + f.characterLimit);
        }

        private static void EndEditing(bool announce)
        {
            var f = _editing;
            _editing = null;
            if (f == null) return;
            _editCheckAt = 0f;
            _editing = f;
            PumpEditing();
            _editing = null;
            try { Input.imeCompositionMode = IMECompositionMode.Auto; } catch { }
            string txt = "";
            try
            {
                txt = f.text ?? "";
                f.DeactivateInputField();
                // 我们没走 TMP 自己的键盘激活，游戏收不到 OnEndEdit —— 手动补上，
                // 否则「打完字关掉面板」时游戏拿不到新文本（日程表任务名、搜索词都靠这个回调落库）。
                if (f.onEndEdit != null) f.onEndEdit.Invoke(txt);
            }
            catch { }
            A11yHost.Diag("[焦点] 退出输入 " + (f != null ? f.gameObject.name : "") + " 内容长度=" + txt.Length);
            if (announce)
                Announcer.Now(string.IsNullOrEmpty(txt) ? "已结束输入，空。" : "已结束输入，内容：" + txt + "。");
        }

        private static void AnnounceNewItems()
        {
            if (!Active) return;
            var before = new HashSet<string>(_beforeActionLabels, StringComparer.Ordinal);
            Collect();
            // 先回「按下去有没有反应」：开关/滑条的状态、音乐有没有开始播、卡车轮播换成了哪一张。
            if (ReportActivatedState()) return;
            // 再回「界面上有没有多出新的字」：今天吃啥点完推荐只更新菜品名和状态文字，控件数不变。
            // 必须在下面的「原选中项失效」分支之前，否则会因为提前 return 而永远播不到。
            AnnounceTextChanges();
            // 回车之后游戏常常把当前界面换掉（收藏内容里点章节 → 换成节列表、点页签 → 换一批章节），
            // 原来的选中项会失效。这时要主动把焦点落到新列表的第一项并播报，
            // 否则玩家会听到「当前不可用 / 无」，然后以为卡住了。
            var cur = Current;
            bool lost = cur == null || !InDomain(cur) || ItemOf(cur) == null;
            if (lost)
            {
                // 先按「刚刚激活的那个标签」找回来：换装卡轮播会重排卡片顺序（服装7 从第 1 张变成第 2 张），
                // 直接跳到新列表第一项会让玩家以为自己换成了别的衣服。
                var anchor = FindByLabel(_lastActivatedLabel);
                if (anchor != null)
                {
                    // 标签没变（今天吃啥点完推荐，按钮还在原地）：界面文字那条已经播过了，
                    // 这里只把选中项收回来，不再念一遍「随机推荐，按钮，第 1 项」，免得连着说两遍。
                    if (!string.IsNullOrEmpty(_lastActivatedLabel))
                    {
                        A11yHost.Diag("[焦点] 动作后按标签找回原项（原样，静默）");
                        MoveToSilent(anchor);
                        return;
                    }
                    A11yHost.Diag("[焦点] 动作后按标签找回原项");
                    MoveTo(anchor, "动作后");
                    return;
                }
                if (_items.Count > 0)
                {
                    // 再按「内容最多的那一组」找：收藏内容里点章节/页签之后，新列表的主体是章节列表，
                    // 按「主要分组」规则会退回页签，玩家听到的还是「主线」，以为没反应。
                    // 界面可能刚换完，先重新采一次再判断。
                    Collect();
                    int main = LargestSectionIndex();
                    if (main >= 0)
                    {
                        var target = _items[main];
                        // 刚重建出来的项常常还是「不可用」（游戏下一帧才 enable）。
                        // 这时不要念「当前不可用」——那会让玩家以为刚点的东西坏了；
                        // 只说「已进入 章节，第1节」，再排队等它可用后补一次定位。
                        if (target.Locked)
                        {
                            if (target.S != null)
                            {
                                if (EventSystem.current != null)
                                    EventSystem.current.SetSelectedGameObject(target.S.gameObject);
                            }
                            else { ClearSelection(); _virtualCur = target; }
                            Announcer.Now("已进入" + (string.IsNullOrEmpty(target.Section) ? "" : target.Section)
                                          + "，" + target.Label + "，共 " + _items.Count + " 项。");
                            _sectionRetryAt = Time.realtimeSinceStartup + 0.4f;
                            _sectionRetryUntil = Time.realtimeSinceStartup + 2.0f;
                            _sectionRetryName = target.Section ?? "";
                            A11yHost.Diag("[焦点] 动作后换到新分组（项还在初始化）：" + target.Section);
                            return;
                        }
                        A11yHost.Diag("[焦点] 动作后原选中项失效，落到内容最多的分组：" + target.Section);
                        if (target.S != null) MoveTo(target.S, "动作后");
                        else MoveToItem(target, target.Section ?? "");
                        return;
                    }
                    A11yHost.Diag("[焦点] 动作后原选中项失效，重新聚焦");
                    if (TryFocusFirst(true)) return;
                }
                if (_items.Count == 0) return;
            }
            var added = new List<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Locked) continue;
                if (!string.IsNullOrEmpty(it.Label) && !before.Contains(it.Label)) added.Add(it.Label);
            }
            if (added.Count == 0) return;
            // 去重、限长
            var uniq = new List<string>();
            for (int i = 0; i < added.Count && uniq.Count < 6; i++)
                if (!uniq.Contains(added[i])) uniq.Add(added[i]);
            A11yHost.Diag("[焦点] 动作后新增 " + uniq.Count + " 项");
            Announcer.Now("现在可以：" + string.Join("、", uniq.ToArray()) + "。");
        }

        /// <summary>
        /// 面板上「不挂在控件上的说明文字」变了也要说回来（今天吃啥推荐完只更新菜品名/状态）。
        /// 只播新增的字，不猜结果的语义（G72）。
        /// </summary>
        private static void AnnounceTextChanges()
        {
            if (_beforeActionTexts.Count == 0) return;
            try
            {
                var beforeTextSet = new HashSet<string>(_beforeActionTexts, StringComparer.Ordinal);
                var newTexts = new List<string>();
                foreach (var t in InfoTexts())
                    if (!beforeTextSet.Contains(t) && !newTexts.Contains(t)) newTexts.Add(t);
                if (newTexts.Count == 0) return;
                // 去掉「被别的条包含」的短条：今天吃啥会同时冒出「回锅肉」和「本轮推荐：回锅肉」，
                // 只念后者就够了。
                var kept = new List<string>();
                for (int i = 0; i < newTexts.Count; i++)
                {
                    string a = newTexts[i];
                    bool redundant = false;
                    for (int j = 0; j < newTexts.Count; j++)
                    {
                        if (i == j) continue;
                        string b = newTexts[j];
                        if (b.Length > a.Length && b.IndexOf(a, StringComparison.Ordinal) >= 0) { redundant = true; break; }
                    }
                    if (!redundant && !kept.Contains(a)) kept.Add(a);
                }
                if (kept.Count == 0) kept = newTexts;
                var uniq = new List<string>();
                for (int i = 0; i < kept.Count && uniq.Count < 4; i++)
                {
                    // 刚按下的那张卡自己也常常是「新出现的字」（二级页顶部写着作品名），
                    // 玩家刚听过，不再重复念一遍。
                    if (!string.IsNullOrEmpty(_lastActivatedLabel) && string.Equals(kept[i].Trim(), _lastActivatedLabel.Trim(), StringComparison.Ordinal)) continue;
                    uniq.Add(kept[i]);
                }
                // 过滤之后可能一条都不剩（新出现的字就是刚按下的卡名、或者只有「游戏简介：」这种空标签），
                // 这时候不要念「现在显示：。」这种半截话。
                if (uniq.Count == 0) return;
                A11yHost.Diag("[焦点] 动作后界面文字变化 " + uniq.Count + " 条");
                Announcer.Now("现在显示：" + string.Join("；", uniq.ToArray()) + "。");
            }
            catch { }
        }

        /// <summary>
        /// 动作结果反馈：游戏对这些控件本身不回话（开关不报「已开启」、点歌不报「开始播放」），
        /// 读屏用户会以为按键没生效。这里用「动作前后的同一控件状态」和游戏自己的
        /// <c>PetMusicScrollListController.CurrentEntry</c> 做确认，播不到就不播，绝不猜。
        /// </summary>
        private static bool ReportActivatedState()
        {
            var s = _activatedSelectable;
            _activatedSelectable = null;
            bool music = _musicFeedbackWanted;
            _musicFeedbackWanted = false;
            string beforeState = _beforeActivatedState;
            _beforeActivatedState = "";
            if (s == null) return false;

            bool stillHere = Active && InDomain(s);
            if (stillHere)
            {
                var it = ItemOf(s);
                // 换装这类「卡车轮播」：按下去之后同一个控件显示的内容换了一张（服装7 → 服装6）。
                // 这时不该念「现在可以：服装6」（会让人以为凭空多出一件），而是把当前这张念清楚。
                if (it != null && !string.IsNullOrEmpty(_lastActivatedLabel)
                    && !string.Equals(it.Label, _lastActivatedLabel, StringComparison.Ordinal))
                {
                    A11yHost.Diag("[焦点] 动作后原控件内容变化：" + _lastActivatedLabel + " → " + (it.Label ?? ""));
                    MoveTo(s, "动作后");
                    return true;
                }
                if (it != null && !string.IsNullOrEmpty(beforeState) && !string.IsNullOrEmpty(it.Label)
                    && !string.Equals(it.State, beforeState, StringComparison.Ordinal))
                {
                    A11yHost.Diag("[焦点] 动作后状态变化：" + it.Label + " " + beforeState + " → " + it.State);
                    Announcer.Now(it.Label + "，" + it.State + "。");
                    return false;
                }
            }

            // 音乐列表：点曲目后游戏静默开播，读它自己的当前曲目做确认（只在对得上时报，避免误报）
            if (music)
            {
                var playing = MusicCurrentTitle();
                if (!string.IsNullOrEmpty(playing) && !string.IsNullOrEmpty(_lastActivatedLabel)
                    && string.Equals(playing, _lastActivatedLabel, StringComparison.Ordinal))
                {
                    A11yHost.Diag("[焦点] 动作后确认播放：" + playing);
                    Announcer.Now("正在播放：" + playing + "。");
                    return true;
                }
            }

            // 主界面唱片机与音乐列表的播放控制：游戏自己一声不吭，读它的播放状态确认一句。
            // 判据是「按的是哪个动作」（播放或暂停/上一首/下一首/随机播放/播放模式），
            // 而不是去猜按钮上的图片——念不出来就不念。
            string media = _lastActivatedLabel ?? "";
            if (media == "播放模式")
            {
                string mode = MusicPlayModeName();
                if (!string.IsNullOrEmpty(mode)) { Announcer.Now("播放模式：" + mode + "。"); return true; }
            }
            else if (media == "播放或暂停" || media == "上一首" || media == "下一首" || media == "随机播放")
            {
                bool playingNow = MusicPlaying();
                string now = MusicCurrentTitle();
                _lastMusicTitle = playingNow ? (now ?? "") : "";
                if (playingNow && !string.IsNullOrEmpty(now))
                {
                    A11yHost.Diag("[焦点] 动作后确认播放：" + now + "（" + media + "）");
                    Announcer.Now("正在播放：" + now + "。");
                    return true;
                }
                if (!playingNow) { Announcer.Now("已暂停。"); return true; }
            }
            return false;
        }

        /// <summary>
        /// 曲目自己变了（播完自动下一首、列表循环、随机播放）时补一句播报。
        /// 只读游戏自己的播放状态与当前曲目，不接管任何输入、不改游戏字段；
        /// 节流 0.5 秒，并且只在主界面或音乐面板里做。
        /// </summary>
        // ==================== 虚拟项（游戏里没有 Selectable 的交互目标） ====================

        /// <summary>
        /// 把「游戏里没有 Selectable、但玩家应该能走到」的目标登记成虚拟项。
        /// 只做只读查询：不打补丁、不改游戏对象、不往游戏里加任何东西。
        /// 目前两个：
        ///   · 和诗萌聊聊气泡：游戏真有这个按钮（MainStoryTriggerButtonVariant），
        ///     只是它挂在主界面图标根之外，方向键走不到，于是只剩 F4 热键——对「希望一切都能导航」
        ///     的玩家等于不存在（m04680）。
        ///   · 诗萌本人：游戏里是 DialogueEntryClickTarget，直接点身体，没有按钮可聚焦。
        /// </summary>
        /// <summary>
        /// 气泡按钮身上那段文字常常是 prefab 的对象名（`CommonButton (3)`），
        /// 念出来只会让玩家以为是坏掉的东西。这里只认「像人话」的标签（含中文），
        /// 其余一律用固定说法「和诗萌聊聊」—— 与玩家在画面上看到的气泡同义。
        /// </summary>
        private static bool LooksLikeButtonName(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (char c in s)
                if (c >= 0x4E00 && c <= 0x9FFF) return false;
            return true;
        }

        private static void CollectExtras()
        {
            _extras.Clear();
            // 界面被游戏自己的「隐藏界面」藏起来时，Root 仍然指向主界面根，
            // 但玩家眼前是黑的 —— 这时不该再把「诗萌 / 和诗萌聊聊」当成可浏览项。
            if (Root == null || Surfaces.ChromeHidden()) return;
            if (Domain != Kind.Scene && Domain != Kind.PetPanel && Domain != Kind.UiManager) return;
            try
            {
                GameObject bubble;
                string bubbleLabel;
                if (Pet.HasStoryBubble(out bubble, out bubbleLabel) && bubble != null)
                {
                    var b = bubble.GetComponent<Button>() ?? bubble.GetComponentInChildren<Button>(true);
                    // 气泡本来就挂在主界面根下时会被普通收集收进去，这里只补「收不到」的那份。
                    if (b != null && InDomain(b) == false)
                    {
                        _extras.Add(new Item
                        {
                            S = null,
                            Go = bubble,
                            Label = LooksLikeButtonName(bubbleLabel) ? "和诗萌聊聊" : bubbleLabel,
                            Role = "按钮",
                            Section = "互动",
                            Virtual = true,
                            Activate = () => Pet.ClickStoryButton(true)
                        });
                    }
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 气泡登记失败: " + e.Message); }
            try
            {
                GameObject body = Pet.ClickTargetObject();
                if (body != null && body.activeInHierarchy)
                {
                    _extras.Add(new Item
                    {
                        S = null,
                        Go = body,
                        Label = "诗萌",
                        Role = "按钮",
                        Section = "互动",
                        Virtual = true,
                        Activate = () => Pet.Poke()
                    });
                }
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 诗萌登记失败: " + e.Message); }
        }

        private static Vector3 ItemPosition(Item it)
        {
            if (it == null) return Vector3.zero;
            if (it.S != null) return PositionOf(it.S);
            if (it.Go != null) return it.Go.transform.position;
            return Vector3.zero;
        }

        /// <summary>在全部项（含虚拟项）里按方向找最近的一项；只做一次查询，不缓存顺序。</summary>
        private static Item NearestItemInDirection(Item from, int dx, int dy)
        {
            if (from == null) return null;
            Collect();
            if (_items.Count == 0) return null;
            Vector3 p0 = ItemPosition(from);
            Item best = null;
            float bestScore = float.MaxValue;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (ReferenceEquals(it, from)) continue;
                if (it.Locked) continue;
                Vector3 p = ItemPosition(it);
                float ddx = p.x - p0.x, ddy = p.y - p0.y;
                float u = ddx * dx + ddy * dy;
                if (u <= 4f) continue;
                float perp = Mathf.Abs(ddx * dy - ddy * dx);
                float score = perp * 4f + Mathf.Abs(u);
                if (score < bestScore) { bestScore = score; best = it; }
            }
            return best;
        }

        /// <summary>走到某一项：真控件复用 MoveTo，虚拟项只记选中态。</summary>
        private static void MoveToItem(Item it, string oldSection)
        {
            if (it == null) return;
            if (it.S != null) { MoveTo(it.S, oldSection); return; }
            // 虚拟项没有可被 EventSystem 选中的物件（游戏没给），屏幕上也不会出现高亮——
            // 这是「最小选中表示」的一部分，已登记在 docs/交互形态判定.md 的遗留缺陷里。
            ClearSelection();
            _virtualCur = it;
            _blockIndex = it.Block;
            bool sectionChanged = !string.IsNullOrEmpty(it.Section) && it.Section != oldSection;
            A11yHost.Diag("[焦点] 选中(虚拟) " + it.Label);
            Announce(it, sectionChanged);
        }

        private static bool _bubbleSeen;
        private static float _bubblePollAt;

        /// <summary>
        /// 「和诗萌聊聊」气泡出现时主动说一句（用户要求）。
        /// 定性：只读镜像（不动输入、不改游戏对象），与唱片机换歌那处同理；
        /// 读屏玩家看不见屏幕上冒出来的气泡，等他自己去按 F4 等于这个入口不存在。
        /// 措辞按「只报事实」：说气泡出现了，不解释能做什么。
        /// </summary>
        private static void PollStoryBubble()
        {
            try
            {
                if (Time.realtimeSinceStartup - _bubblePollAt < 0.5f) return;
                _bubblePollAt = Time.realtimeSinceStartup;
                GameObject bubble; string label;
                bool on = Pet.HasStoryBubble(out bubble, out label) && !Reader.InStory && !IsPanelActive;
                if (on && !_bubbleSeen)
                {
                    _bubbleSeen = true;
                    A11yHost.Diag("[焦点] 对话气泡出现");
                    Announcer.Now("诗萌旁边出现了对话气泡。");
                    if (Active && Domain == Kind.Scene) Collect();   // 立刻进列表，方向键就能走到
                }
                else if (!on && _bubbleSeen)
                {
                    _bubbleSeen = false;
                    if (Active && Domain == Kind.Scene) Collect();
                }
            }
            catch { }
        }

        private static void PollMusicChange()
        {
            if (!Active) { _lastMusicTitle = ""; return; }
            bool here = Domain == Kind.Scene || (Domain == Kind.PetPanel && _specKey == "music_player");
            if (!here) return;
            if (Time.realtimeSinceStartup < _musicPollAt) return;
            _musicPollAt = Time.realtimeSinceStartup + 0.5f;
            try
            {
                if (!MusicPlaying()) return;
                string t = MusicCurrentTitle();
                if (string.IsNullOrEmpty(t) || t == _lastMusicTitle) return;
                _lastMusicTitle = t;
                A11yHost.Diag("[焦点] 曲目自动切换：" + t);
                Announcer.Now("正在播放：" + t + "。");
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 轮询曲目失败: " + e.Message); }
        }

        /// <summary>读游戏自己的播放状态（PetMusicScrollListController.IsSourcePlaying）。读不到返回 false。</summary>
        private static bool MusicPlaying()
        {
            try
            {
                var t = FindType("PetMusicScrollListController");
                if (t == null) return false;
                var comp = UnityEngine.Object.FindObjectOfType(t) as MonoBehaviour;
                if (comp == null) return false;
                var p = t.GetProperty("IsSourcePlaying", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || p.PropertyType != typeof(bool)) return false;
                return (bool)p.GetValue(comp, null);
            }
            catch { return false; }
        }

        /// <summary>读游戏自己的播放模式（PetMusicPlayMode：LoopList/LoopOne/ShuffleOrder）。</summary>
        private static string MusicPlayModeName()
        {
            try
            {
                var t = FindType("PetMusicScrollListController");
                if (t == null) return null;
                var comp = UnityEngine.Object.FindObjectOfType(t) as MonoBehaviour;
                if (comp == null) return null;
                var p = t.GetProperty("PlayMode", BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return null;
                object v = p.GetValue(comp, null);
                if (v == null) return null;
                switch (v.ToString())
                {
                    case "LoopList": return "列表循环";
                    case "LoopOne": return "单曲循环";
                    case "ShuffleOrder": return "随机播放";
                    default: return null;
                }
            }
            catch { return null; }
        }

        /// <summary>读游戏自己的当前曲目（PetMusicScrollListController.CurrentEntry.displayName）。读不到返回 null。</summary>
        private static string MusicCurrentTitle()
        {
            try
            {
                var t = FindType("PetMusicScrollListController");
                if (t == null) return null;
                var comp = UnityEngine.Object.FindObjectOfType(t) as MonoBehaviour;
                if (comp == null) return null;
                var p = t.GetProperty("CurrentEntry", BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return null;
                var entry = p.GetValue(comp, null);
                if (entry == null) return null;
                var f = entry.GetType().GetField("displayName", BindingFlags.Public | BindingFlags.Instance);
                return f != null ? f.GetValue(entry) as string : null;
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 读当前曲目失败: " + e.Message); return null; }
        }

        private static Type FindType(string name)
        {
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    var t = asms[i].GetType(name, false);
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        // ==================== 选中项 ====================

        /// <summary>诊断/F2 用的一句话状态：进了哪个域、多少项、当前是哪一项。</summary>
        internal static string StateSummary()
        {
            try
            {
                if (!Active) return "焦点未进入（当前没有打开的面板）";
                Collect();
                var it = CurrentItem;
                return Title + "（" + Domain + "）共 " + _items.Count + " 项，当前："
                       + (it != null ? Describe(it, true) : "无");
            }
            catch (Exception e) { return "状态异常: " + e.Message; }
        }

        /// <summary>
        /// F2 用：给玩家听的一句话状态。与 <see cref="StateSummary"/> 的区别是不带内部域英文名
        /// （PetPanel / Scene 这些是补丁内部概念，玩家不需要知道），没有域时返回空串交给调用方兜底。
        /// </summary>
        // 文本输入：游戏里所有输入框都是鼠标点进去打字的（唯一焦点代码 AutoFocusInputField）。
        // 键盘/读屏要建日程、改名、搜曲目，就得有人替玩家把输入框「点开」并交还按键。
        private static TMPro.TMP_InputField _editing;
        internal static bool IsEditing { get { return _editing != null; } }

        // 有些环境里 Unity 的键盘事件进不到 TMP_InputField 的事件队列（本轮实测：Input.inputString 有字符，
        // 但输入框文本一直是空）。这里留一条「补打」通道：先让 TMP 自己处理，过一帧看文本有没有变；
        // 没变就由补丁把这一帧键入的字符补进去。TMP 正常的机器上文本已经变了，这里不会重复输入。
        private static string _editPending = "";
        private static string _editBefore = "";
        private static float _editCheckAt;

        private static string FrameLabel() { return Time.frameCount + "." + _editPending.Length; }

        // 测试钩子（只在探针构建里用）：把一段文本当成「玩家刚敲的键」送进补打通道。
        internal static bool DebugType(string text)
        {
            if (_editing == null || string.IsNullOrEmpty(text)) return false;
            if (_editPending.Length == 0) _editBefore = _editing.text ?? "";
            _editPending += text;
            _editCheckAt = 0f;
            string echo = text.Replace("\b", "删除");
            if (echo.Length > 0) Announcer.Now(echo);
            PumpEditing();
            return true;
        }

        private static void PumpEditing()
        {
            if (_editing == null || _editPending.Length == 0) return;
            if (Time.realtimeSinceStartup < _editCheckAt) return;
            var f = _editing;
            string pend = _editPending;
            string before = _editBefore ?? "";
            _editPending = "";
            _editCheckAt = 0f;
            if (f == null) return;
            if (f.text == before)
            {
                var sb = new System.Text.StringBuilder(before);
                bool rejected = false;
                for (int i = 0; i < pend.Length; i++)
                {
                    char c = pend[i];
                    if (c == '\b') { if (sb.Length > 0) sb.Length--; continue; }
                    if (c == '\n' || c == '\r') continue;
                    // 输入框自己声明了「只能输数字」时，替玩家挡掉字母：
                    // 直接写 text 会绕过 TMP 的 characterValidation，番茄钟那三个
                    // 「专注时长/休息时长/循环次数」输入框拿到字母会在游戏侧解析失败。
                    if (!AcceptsChar(f, c, sb.ToString(), _editNumericOnly)) { rejected = true; continue; }
                    sb.Append(c);
                }
                if (rejected && !_editRejectedWarned)
                {
                    _editRejectedWarned = true;
                    Announcer.Now(EditingHint(f));
                }
                A11yHost.Diag("[焦点] 代打 frame=" + Time.frameCount + " before=[" + before + "] pend=[" + pend + "]");
                try
                {
                    f.text = sb.ToString();
                    f.stringPosition = f.text.Length;
                    f.caretPosition = f.text.Length;
                }
                catch (Exception e) { A11yHost.Diag("[焦点] 补打失败: " + e.Message); }
                A11yHost.Diag("[焦点] 补丁代打输入 " + pend + " → 内容=" + (f.text ?? ""));
                // 搜索框这类输入框会随输入实时过滤列表：项数一变就报一声，
                // 否则盲人打完字完全不知道「到底筛出东西没有」。
                try
                {
                    int beforeCount = _items.Count;
                    Collect();
                    if (_items.Count != beforeCount)
                    {
                        _editCountAnnounced = _items.Count;
                        Announcer.Now(_items.Count == 0
                            ? "没有符合条件的项。"
                            : "现在共 " + _items.Count + " 项。");
                    }
                }
                catch { }
            }
        }

        private static bool _editRejectedWarned;
        private static int _editCountAnnounced = -1;

        // 依据 TMP_InputField 自己声明的校验规则挡住不可能被游戏接受的字符。
        // 注意：本作这些输入框多是 contentType=Standard + characterValidation=Digit 的组合，
        // 只看 contentType 会漏（番茄钟三个时长输入框就是这样）。
        private static bool AcceptsChar(TMPro.TMP_InputField f, char c, string current, bool numericOnly)
        {
            if (numericOnly) return char.IsDigit(c);
            switch (f.characterValidation)
            {
                case TMPro.TMP_InputField.CharacterValidation.Digit:
                    return char.IsDigit(c);
                case TMPro.TMP_InputField.CharacterValidation.Decimal:
                    return char.IsDigit(c) || (c == '-' && current.Length == 0) || (c == '.' && current.IndexOf('.') < 0);
            }
            switch (f.contentType)
            {
                case TMPro.TMP_InputField.ContentType.IntegerNumber:
                    return char.IsDigit(c) || (c == '-' && current.Length == 0);
                case TMPro.TMP_InputField.ContentType.DecimalNumber:
                    return char.IsDigit(c) || (c == '.' && current.IndexOf('.') < 0);
                case TMPro.TMP_InputField.ContentType.Alphanumeric:
                    return char.IsLetterOrDigit(c);
                case TMPro.TMP_InputField.ContentType.Name:
                    return char.IsLetterOrDigit(c) || c == ' ';
                default:
                    return true;
            }
        }

        private static string EditingHint(TMPro.TMP_InputField f)
        {
            if (_editNumericOnly) return "这个输入框只能输数字，其它字符已被忽略。";
            if (f.characterValidation == TMPro.TMP_InputField.CharacterValidation.Digit
                || f.characterValidation == TMPro.TMP_InputField.CharacterValidation.Decimal
                || f.contentType == TMPro.TMP_InputField.ContentType.IntegerNumber
                || f.contentType == TMPro.TMP_InputField.ContentType.DecimalNumber)
                return "这个输入框只能输数字，其它字符已被忽略。";
            if (f.contentType == TMPro.TMP_InputField.ContentType.Alphanumeric)
                return "这个输入框只能输字母和数字。";
            return "这个输入框不接受刚才那个字符。";
        }

        internal static string SpeakSummary()
        {
            try
            {
                if (!Active) return "";
                Collect();
                var it = CurrentItem;
                string title = string.IsNullOrEmpty(Title) ? "当前界面" : Title;
                return title + "，共 " + _items.Count + " 个可操作项，当前："
                       + (it != null ? Describe(it, true) : "无");
            }
            catch { return ""; }
        }

        private static Selectable Current
        {
            get
            {
                var es = EventSystem.current;
                if (es == null) return null;
                var go = es.currentSelectedGameObject;
                return go == null ? null : go.GetComponent<Selectable>();
            }
        }

        /// <summary>当前项：真控件从 EventSystem 的选中态来；虚拟项由补丁记（游戏里没有可被选中的物件）。</summary>
        private static Item CurrentItem
        {
            get
            {
                if (_virtualCur != null) return _virtualCur;
                return ItemOf(Current);
            }
        }

        private static void ClearSelection()
        {
            var es = EventSystem.current;
            if (es == null) return;
            if (es.currentSelectedGameObject != null) es.SetSelectedGameObject(null);
        }

        /// <summary>临时取证：把当前域里的所有项打出来（探针用）。</summary>
        internal static string ItemsSummary()
        {
            try
            {
                if (!Active) return "（未进入域）";
                Collect();
                var sb = new StringBuilder();
                sb.Append(Title).Append(" 共 ").Append(_items.Count).Append(" 项");
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    sb.Append("\n  #").Append(i + 1).Append(' ').Append(it.Section).Append(" | ")
                      .Append(it.Label).Append(" | ").Append(it.Role).Append(" | ")
                      .Append(it.S == null ? "virtual" : (it.S.IsInteractable() ? "ok" : "locked")).Append(" | ")
                      .Append(it.S != null ? PathOf(it.S) : (it.Go != null ? it.Go.name : ""));
                }
                return sb.ToString();
            }
            catch (Exception e) { return "items 异常: " + e.Message; }
        }

        internal static string PathOf(Selectable s) { return s == null ? "" : PathOfT(s.transform); }

        /// <summary>探针/日志用：当前域里的块清单。</summary>
        internal static string BlocksSummary()
        {
            try
            {
                Collect();
                var sb = new StringBuilder();
                sb.Append("blocks=").Append(_blocks.Count).Append(" cur=").Append(_blockIndex);
                for (int i = 0; i < _blocks.Count; i++)
                {
                    sb.Append("\n  #").Append(i + 1).Append(" ").Append(_blocks[i].Name)
                      .Append(" (").Append(_blocks[i].Items.Count).Append(")");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "blocks 异常: " + e.Message; }
        }

        private static string PathOfT(Transform tr)
        {
            try
            {
                var sb = new StringBuilder(tr.name);
                var p = tr.parent;
                int guard = 0;
                while (p != null && guard++ < 6) { sb.Insert(0, p.name + "/"); p = p.parent; }
                return sb.ToString();
            }
            catch { return ""; }
        }
        private static bool AnyInteractable()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                try { if (_items[i].S != null && _items[i].S.IsInteractable()) return true; } catch { }
            }
            return false;
        }

        private static bool TryFocusFirst(bool announce)
        {
            if (Root == null) return false;
            Collect();
            // 面板还在淡入（settle 窗口内）时不要念「没有可操作的控件」，那是假信息
            if (_items.Count == 0) { if (announce && _settleUntil <= 0f) AnnounceEmpty(); return false; }

            int start = PickFirstIndex();
            var first = _items[start];
            _blockIndex = first.Block;
            if (first.S != null) EventSystem.current.SetSelectedGameObject(first.S.gameObject);
            else { ClearSelection(); _virtualCur = first; }
            A11yHost.Diag("[焦点] 首项 " + (first.S != null ? first.S.gameObject.name : first.Go.name) + "（" + first.Section + "）共 " + _items.Count + " 项");

            if (announce)
            {
                var sb = new StringBuilder();
                sb.Append(Title);
                string hint = (_hint ?? "").TrimEnd('。', '.', '；', ';', ' ');
                if (hint.Length > 0) sb.Append("。").Append(hint);
                // 提示/确认框：正文在面板自己身上（不是控件标签），要单独念，否则玩家只听到两个按钮名
                if (PanelMap.HasBody(_specKey))
                {
                    string body = ContentText(Root, 120, true);
                    if (!string.IsNullOrEmpty(body)) sb.Append("。").Append(body.TrimEnd('。', '.', '？', '！', '；', ';', '，', ',', ' '));
                }
                sb.Append("。当前：");
                sb.Append(Describe(first, false));
                Announcer.Now(sb.ToString());
            }
            return true;
        }

        /// <summary>
        /// 动作把界面换掉之后，找「内容最多的那一组」的第一项。
        /// 依据：收藏内容里点章节/页签、鬼畜制造机里切工程，新内容的主体都是数量最多的那一组；
        /// 而「主要分组」是**打开面板时**的规则，此时会把焦点送回页签/工具栏，玩家会以为没反应。
        /// 只在原选中项已经失效时使用，不参与其它路径。
        /// </summary>
        private static int LargestSectionIndex()
        {
            if (_items.Count == 0) return -1;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var it in _items)
            {
                if (string.IsNullOrEmpty(it.Section)) continue;
                int c;
                counts.TryGetValue(it.Section, out c);
                counts[it.Section] = c + 1;
            }
            string best = null; int bestCount = 0;
            var stat = new StringBuilder();
            foreach (var kv in counts)
            {
                if (stat.Length > 0) stat.Append('，');
                stat.Append(kv.Key).Append('=').Append(kv.Value);
                if (kv.Value > bestCount) { best = kv.Key; bestCount = kv.Value; }
            }
            A11yHost.Diag("[焦点] 动作后分组统计：" + (stat.Length > 0 ? stat.ToString() : "（无）"));
            // 至少 2 项才算「一组内容」，单个项不足以说明界面换到了新列表
            if (best == null || bestCount < 2) return -1;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (!string.Equals(it.Section, best, StringComparison.Ordinal)) continue;
                if (IsDismissive(it.S)) continue;
                return i;
            }
            return -1;
        }

        /// <summary>
        /// 首焦点选谁。优先级（都要求「不是关闭/取消/返回」）：
        /// 1) 本面板「主要分组」里第一个**可用**的项 —— 打开音乐播放器先落到曲目，不是搜索框；
        /// 2) 任意分组里第一个可用的项 —— 背景面板最上面那个翻页按钮是禁用的，跳过；
        /// 3) 任意分组里第一个非关闭项；4) 第一个项。
        /// </summary>
        private static int PickFirstIndex()
        {
            string primary = PanelMap.PrimarySectionOf(_specKey);

            string preferRole = PanelMap.PreferredRoleOf(_specKey);
            string preferLabel = PanelMap.PreferredLabelOf(_specKey);

            if (primary.Length > 0)
            {
                // 「角色 + 标签关键词」都满足的最优先：番茄钟要先落在「专注时长」这个输入框上
                if (preferRole.Length > 0 && preferLabel.Length > 0)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        var it = _items[i];
                        if (it.Section == primary && it.Role == preferRole && it.Label != null
                            && it.Label.IndexOf(preferLabel, StringComparison.Ordinal) >= 0
                            && it.S != null && it.S.IsInteractable()) return i;
                    }
                }
                // 只满足「角色」：番茄钟的三个步进器里去找输入框                if (preferRole.Length > 0)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        var it = _items[i];
                        if (it.Section == primary && it.Role == preferRole && it.S != null && it.S.IsInteractable()) return i;
                    }
                }
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Section == primary && it.S != null && !IsDismissive(it.S) && it.S.IsInteractable()) return i;
                }
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Section == primary && !IsDismissive(it.S)) return i;
                }
            }

            for (int i = 0; i < _items.Count; i++)
                if (_items[i].S != null && !IsDismissive(_items[i].S) && _items[i].S.IsInteractable()) return i;

            for (int i = 0; i < _items.Count; i++)
                if (!IsDismissive(_items[i].S)) return i;

            for (int i = 0; i < _items.Count; i++)
                if (_items[i].S != null && _items[i].S.IsInteractable()) return i;

            return 0;
        }

        /// <summary>面板里一个可操作控件都没有：把画面上能读到的文字念出来（今天吃啥、追番助手这类信息面板）。</summary>
        private static void AnnounceEmpty()
        {
            if (_announcedEmpty) return;
            _announcedEmpty = true;
            try
            {
                string body = ContentText(Root, 300);
                string msg = string.IsNullOrEmpty(body)
                    ? Title + "，没有可操作的控件。"
                    : Title + "。" + body;
                A11yHost.Diag("[焦点] 空面板朗读 长度=" + (body != null ? body.Length : 0));
                Announcer.Now(msg);
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 空面板朗读异常: " + e.Message); }
        }

        // ==================== 采集 ====================

        /// <summary>
        /// 无字控件的标签关联：把「离它最近、且在同一行左侧或正上方」的可见文本当作它的标签。
        /// 只用于输入框/滑条这类游戏把名字画在旁边文本节点上的控件；找不到就留空，由角色名兜底。
        /// </summary>
        private static string AssocLabel(Selectable s)
        {
            try
            {
                if (s == null) return "";
                var rt = s.transform as RectTransform;
                if (rt == null) return "";
                Vector3 sc = rt.TransformPoint(rt.rect.center);
                var texts = Root != null ? Root.GetComponentsInChildren<TMPro.TMP_Text>(true) : null;
                if (texts == null) return "";
                string best = "";
                float bestScore = float.MaxValue;
                for (int i = 0; i < texts.Length; i++)
                {
                    var tx = texts[i];
                    if (tx == null || !tx.gameObject.activeInHierarchy) continue;
                    string str = (tx.text ?? "").Trim();
                    if (str.Length == 0 || str.Length > 12) continue;          // 标签都很短
                    if (UiNav.GameTextIsNoise(str)) continue;
                    var trt = tx.transform as RectTransform;
                    if (trt == null) continue;
                    if (trt.IsChildOf(rt)) continue;                            // 自己内部的输入文本
                    Vector3 tc = trt.TransformPoint(trt.rect.center);
                    float dy = Mathf.Abs(tc.y - sc.y);
                    float dx = sc.x - tc.x;
                    // 同一行（左或右），或正上/正下方一小段距离。
                    // 游戏的滑杆行是「名字 + 开关 + 滑条」横排，环境音效的名字就画在开关旁边。
                    float score;
                    if (dy <= Mathf.Max(rt.rect.height, 24f) && Mathf.Abs(dx) <= 220f) score = Mathf.Abs(dx) + dy * 3f;
                    else if (Mathf.Abs(dx) <= rt.rect.width * 0.5f + 40f && dy <= 90f) score = dy * 2f + Mathf.Abs(dx);
                    else continue;
                    if (score < bestScore) { bestScore = score; best = str; }
                }
                return best;
            }
            catch { return ""; }
        }
        private static void Collect()
        {
            _items.Clear();
            _blocks.Clear();
            if (Root == null) return;

            // 采集源：主域根 +（面板/对话打开时）主界面根。用户口径是「Tab 切到全屏所有可见块」，
            // 所以面板打开时主界面的块也要能 Tab 回去，但面板自己仍是一整块。
            var sources = new List<Transform>(2);
            sources.Add(Root);
            if (Domain != Kind.Scene && Domain != Kind.Story && !Surfaces.ChromeHidden())
            {
                var backing = SceneRoot();
                if (backing != null && backing != Root) sources.Add(backing);
            }

            var raw = new List<Selectable>(64);
            var sceneSourceOf = new Dictionary<Selectable, bool>();
            for (int si = 0; si < sources.Count; si++)
            {
                Selectable[] all;
                try { all = sources[si].GetComponentsInChildren<Selectable>(true); }
                catch { continue; }
                for (int i = 0; i < all.Length; i++)
                {
                    var s = all[i];
                    if (s == null || raw.Contains(s)) continue;
                    if (!Candidate(s, sources[si])) continue;
                    raw.Add(s);
                    sceneSourceOf[s] = si > 0;
                }
            }

            // 去重 1：真正的「同一块像素被套了三层」（背景卡片的 Mask/BGSprite/Box）。
            // 判据是「父控件的世界矩形完整盖住子控件，且子控件面积够大」，
            // 不能只看父子关系：唱片机的 上一首/播放/下一首 是进度滑条的子节点，它们是独立控件。
            var kept = new List<Selectable>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var s = raw[i];
                bool nested = false;
                for (int j = 0; j < raw.Count; j++)
                {
                    if (i == j) continue;
                    var o = raw[j];
                    if (o == null || s == null) continue;
                    if (s.transform != o.transform && s.transform.IsChildOf(o.transform) && Covers(o, s)) { nested = true; break; }
                }
                if (!nested) kept.Add(s);
            }
            // 去重 2：同位置同尺寸同类型的重复项
            var final = new List<Selectable>(kept.Count);
            for (int i = 0; i < kept.Count; i++)
            {
                var s = kept[i];
                bool dup = false;
                for (int j = 0; j < final.Count; j++)
                {
                    var o = final[j];
                    if (o == null || s == null) continue;
                    if (o.GetType() != s.GetType()) continue;
                    Vector3 a = PositionOf(s), b = PositionOf(o);
                    if (Mathf.Abs(a.x - b.x) <= 8f && Mathf.Abs(a.y - b.y) <= 8f
                        && Mathf.Abs(SizeOf(s) - SizeOf(o)) <= 40f) { dup = true; break; }
                }
                if (!dup) final.Add(s);
            }

            // 去重 3：列表行里的「标记开关」。
            // 例：音乐列表每一行 = 行按钮（曲名）+ 行内 CommonToggle（指示当前曲目，isOn 恒为 1）。
            // 行按钮本身已经代表这一行，再把行内开关算一项，玩家会听到每首歌出现两次，
            // 而且开关自己没有可读文字（只能念出「开关」）。判据限定得很死：
            // 「Toggle 且是另一个 Button 候选的后代」——设置/番茄钟/背景那些独立开关不受影响。
            var final2 = new List<Selectable>(final.Count);
            for (int i = 0; i < final.Count; i++)
            {
                var s = final[i];
                bool rowMarker = false;
                if (s is Toggle)
                {
                    for (int j = 0; j < final.Count; j++)
                    {
                        var o = final[j];
                        if (o == null || ReferenceEquals(o, s)) continue;
                        if (o is Button && s.transform.IsChildOf(o.transform)) { rowMarker = true; break; }
                    }
                }
                if (!rowMarker) final2.Add(s);
            }

            // 去重 4：挂在按钮上的装饰子图。例：musicButtons/导入音乐/Image —— 按钮内部的图标
            // 自己也挂了 Button（不可交互），会和真正的按钮重复念一遍。
            // 判据收得很死：「不可交互」且「是另一个可交互 Button 候选的后代」。
            // 滑条/输入框/开关永远不丢（它们的子节点可能就是可操作的手柄）。
            var final3 = new List<Selectable>(final2.Count);
            for (int i = 0; i < final2.Count; i++)
            {
                var s = final2[i];
                bool decoration = false;
                if (s is Button && !s.IsInteractable())
                {
                    for (int j = 0; j < final2.Count; j++)
                    {
                        var o = final2[j];
                        if (o == null || ReferenceEquals(o, s)) continue;
                        if (o is Button && o.IsInteractable() && s.transform.IsChildOf(o.transform))
                        { decoration = true; break; }
                    }
                }
                if (!decoration) final3.Add(s);
            }

            final3.Sort(CompareByPosition);

            // 建项 + 分组
            var sectionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < final3.Count; i++)
            {
                var it = BuildItem(final3[i]);
                string k = it.Section ?? "";
                int c;
                sectionCounts.TryGetValue(k, out c);
                sectionCounts[k] = c + 1;
                _items.Add(it);
            }
            // 虚拟项（游戏里没有 Selectable 的交互目标）也要参与分组与序号，
            // 否则玩家听到的“第 N 项，共 M 项”会跟能走到的数量对不上。
            CollectExtras();
            for (int i = 0; i < _extras.Count; i++)
            {
                var it = _extras[i];
                string k = it.Section ?? "";
                int c; sectionCounts.TryGetValue(k, out c);
                sectionCounts[k] = c + 1;
                _items.Add(it);
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                string k = it.Section ?? "";
                int n;
                seen.TryGetValue(k, out n);
                n++;
                seen[k] = n;
                int total;
                sectionCounts.TryGetValue(k, out total);
                it.Index = n;
                it.Count = total;
                it.InList = total >= 3;
            }

            // ---- 分块：按语义预设块名归类（Tab 在块之间切换） ----
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                it.Global = i;
                bool fromScene;
                if (it.Virtual) it.BlockName = "互动";
                else if (it.S != null && sceneSourceOf.TryGetValue(it.S, out fromScene) && fromScene)
                    it.BlockName = SceneBlockName(it.S.transform);
                else if (Domain == Kind.Scene) it.BlockName = SceneBlockName(it.S != null ? it.S.transform : Root);
                else if (Domain == Kind.Story) it.BlockName = "对话选项";
                else it.BlockName = string.IsNullOrEmpty(Title) ? "面板" : Title;
            }
            var order = new List<string>();
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _items.Count; i++)
            {
                string bn = _items[i].BlockName ?? "";
                if (!firstSeen.ContainsKey(bn)) { firstSeen[bn] = order.Count; order.Add(bn); }
            }
            order.Sort(delegate(string a, string b)
            {
                int ra = BlockRank(a), rb = BlockRank(b);
                if (ra != rb) return ra.CompareTo(rb);
                return firstSeen[a].CompareTo(firstSeen[b]);
            });
            var idxOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int bi = 0; bi < order.Count; bi++)
            {
                idxOf[order[bi]] = bi;
                _blocks.Add(new BlockInfo { Name = order[bi] });
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                int bi;
                if (idxOf.TryGetValue(it.BlockName ?? "", out bi))
                {
                    it.Block = bi;
                    _blocks[bi].Items.Add(i);
                }
                else it.Block = 0;
            }
        }

        /// <summary>主界面根（面板打开时用来并入主界面的块）。</summary>
        private static Transform SceneRoot()
        {
            try
            {
                if (_sceneRoot != null && _sceneRoot.gameObject.activeInHierarchy && AlphaOf(_sceneRoot) >= 0.05f) return _sceneRoot;
                var c = FindSceneCanvas();
                if (c != null) _sceneRoot = c;
                return _sceneRoot;
            }
            catch { return null; }
        }

        /// <summary>主界面按语义固定分块：顶部横向功能条 / 右侧竖向图标列 / 底部唱片机 / 互动。</summary>
        private static string SceneBlockName(Transform t)
        {
            string p = PathOfT(t);
            if (p.IndexOf("/UpRight/", StringComparison.Ordinal) >= 0) return "竖向图标列";
            if (p.IndexOf("/Right/", StringComparison.Ordinal) >= 0) return "横向功能条";
            if (p.IndexOf("PetMusicPlayer_Window", StringComparison.Ordinal) >= 0) return "唱片机";
            return "主界面";
        }

        private static int BlockRank(string name)
        {
            switch (name)
            {
                case "横向功能条": return 0;
                case "竖向图标列": return 1;
                case "唱片机": return 2;
                case "互动": return 3;
                case "对话选项": return 0;
                default: return 9;
            }
        }

        private static Item BuildItem(Selectable s)
        {
            var it = new Item();
            it.S = s;
            it.Section = PanelMap.SectionOf(_specKey, RelPath(s.transform));
            it.Locked = !s.IsInteractable();
            it.Label = LabelOf(s);
            it.Role = RoleOf(s);
            it.State = StateOf(s);
            return it;
        }

        private static void Refresh(Item it)
        {
            if (it == null || it.S == null) return;
            it.Label = LabelOf(it.S);
            it.State = StateOf(it.S);
            it.Locked = !it.S.IsInteractable();
        }

        private static Item ItemOf(Selectable s)
        {
            if (s == null) return null;
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].S == s) return _items[i];
            return null;
        }

        /// <summary>按标签找一个控件（动作后游戏把同一项挪了位置时用；标签唯一才认）。</summary>
        private static Selectable FindByLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            Selectable found = null;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.S == null || it.Label == null) continue;
                if (!string.Equals(it.Label, label, StringComparison.Ordinal)) continue;
                if (found != null) return null;   // 标签不唯一 → 不猜
                found = it.S;
            }
            return found;
        }

        private static bool Candidate(Selectable s, Transform scope = null)
        {
            if (s == null) return false;
            if (scope == null) scope = Root;
            try
            {
                if (!s.gameObject.activeInHierarchy) return false;
                if (scope != null && !s.transform.IsChildOf(scope)) return false;
                if (AlphaOf(s.transform) < 0.05f) return false;   // 幽灵面板：active 但透明
                if (Excluded(s)) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool InDomain(Selectable s)
        {
            if (s == null || Root == null) return false;
            try { return s.transform.IsChildOf(Root) && s.gameObject.activeInHierarchy && AlphaOf(s.transform) >= 0.05f; }
            catch { return false; }
        }

        private static int CompareByPosition(Selectable a, Selectable b)
        {
            Vector3 pa = PositionOf(a), pb = PositionOf(b);
            if (Mathf.Abs(pa.y - pb.y) > 4f) return pb.y.CompareTo(pa.y);   // 屏幕上方先
            return pa.x.CompareTo(pb.x);
        }

        private static Vector3 PositionOf(Selectable s)
        {
            try
            {
                var rt = s.transform as RectTransform;
                return rt != null ? rt.position : s.transform.position;
            }
            catch { return Vector3.zero; }
        }

        private static float SizeOf(Selectable s)
        {
            try
            {
                var rt = s.transform as RectTransform;
                return rt != null ? rt.rect.width : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>控件在屏幕/world 空间的轴对齐包围盒（用来判「同一块像素套了多层」）。</summary>
        private static bool WorldBox(Transform t, out Vector2 min, out Vector2 max)
        {
            min = Vector2.zero; max = Vector2.zero;
            try
            {
                var rt = t as RectTransform;
                if (rt == null) return false;
                var c = new Vector3[4];
                rt.GetWorldCorners(c);
                min = new Vector2(Mathf.Min(Mathf.Min(c[0].x, c[1].x), Mathf.Min(c[2].x, c[3].x)),
                                  Mathf.Min(Mathf.Min(c[0].y, c[1].y), Mathf.Min(c[2].y, c[3].y)));
                max = new Vector2(Mathf.Max(Mathf.Max(c[0].x, c[1].x), Mathf.Max(c[2].x, c[3].x)),
                                  Mathf.Max(Mathf.Max(c[0].y, c[1].y), Mathf.Max(c[2].y, c[3].y)));
                return true;
            }
            catch { return false; }
        }

        /// <summary>parent 是否「完整盖住」child 且面积接近（= 同一个视觉对象的多层包装）。</summary>
        private static bool Covers(Selectable parent, Selectable child)
        {
            try
            {
                if (parent == null || child == null) return false;
                if (parent is Slider || parent is Toggle) return false;   // 滑条/开关里的按钮是独立控件
                if (child is Slider || child is TMPro.TMP_InputField) return false;
                Vector2 pmin, pmax, cmin, cmax;
                if (!WorldBox(parent.transform, out pmin, out pmax)) return false;
                if (!WorldBox(child.transform, out cmin, out cmax)) return false;
                float pa = (pmax.x - pmin.x) * (pmax.y - pmin.y);
                float ca = (cmax.x - cmin.x) * (cmax.y - cmin.y);
                if (pa <= 1f || ca <= 1f) return false;
                bool inside = cmin.x >= pmin.x - 2f && cmax.x <= pmax.x + 2f
                           && cmin.y >= pmin.y - 2f && cmax.y <= pmax.y + 2f;
                return inside && (ca / pa) >= 0.5f;
            }
            catch { return false; }
        }

        private static float AlphaOf(Transform t)
        {
            float a = 1f;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                var cg = t.GetComponent<CanvasGroup>();
                if (cg != null) a *= cg.alpha;
                t = t.parent;
            }
            return a;
        }

        private static bool Excluded(Selectable s)
        {
            var cfg = A11yHost.CfgExcludeNames;
            if (cfg == null || string.IsNullOrEmpty(cfg.Value)) return false;
            string[] names = cfg.Value.Split(',');
            var t = s.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                string n = t.gameObject.name;
                for (int i = 0; i < names.Length; i++)
                {
                    string x = names[i].Trim();
                    if (x.Length > 0 && string.Equals(n, x, StringComparison.OrdinalIgnoreCase)) return true;
                }
                t = t.parent;
            }
            return false;
        }

        private static Selectable NearestInDirection(Selectable from, int dx, int dy)
        {
            Collect();
            if (_items.Count == 0) return null;
            Vector3 p0 = PositionOf(from);
            Selectable best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _items.Count; i++)
            {
                var s = _items[i].S;
                if (s == null) continue;   // 虚拟项没有 Selectable，不参与几何回退
                if (s == from) continue;
                Vector3 p = PositionOf(s);
                float ddx = p.x - p0.x, ddy = p.y - p0.y;
                float u = ddx * dx + ddy * dy;
                if (u <= 4f) continue;
                float perp = Mathf.Abs(ddx * dy - ddy * dx);
                float score = perp * 4f + Mathf.Abs(u);
                if (score < bestDist) { bestDist = score; best = s; }
            }
            return best;
        }

        // ==================== 朗读 ====================

        private static void Announce(Item it, bool sectionChanged)
        {
            if (it == null) return;
            Announcer.Now(Describe(it, sectionChanged));
        }

        /// <summary>
        /// 「分组，标签，角色，状态，第 N 项，共 M 项」。
        /// 读屏软件的常见读法，不编任何游戏没画出来的东西。
        /// </summary>
        private static string Describe(Item it, bool withSection)
        {
            var sb = new StringBuilder();
            if (withSection && !string.IsNullOrEmpty(it.Section)) sb.Append(it.Section).Append("，");
            string numPrefix = PanelMap.NumericLabelOf(_specKey, it.Section);
            string label = it.Label;
            if (numPrefix.Length > 0 && it.InList) label = numPrefix + " " + it.Index;
            sb.Append(string.IsNullOrEmpty(label) ? it.Role : label);
            // 角色只在「不知道这是什么控件就猜不出来」时才念：滑条要说明左右是调值，
            // 输入框要说明能打字。普通按钮/开关/页签的角色是噪声——状态（已开启/已关闭）
            // 和标签本身已经说明了它是什么，玩家也不需要每移动一下都听一遍「按钮」。
            if (it.Role == "滑条" || it.Role == "输入框")
                sb.Append("，").Append(it.Role);
            // 鬼畜制造机的 52 个工程槽里装的是台词，不是名字：只念槽名和序号，不念内容
            bool hideState = it.State != null && it.State.StartsWith("内容", StringComparison.Ordinal)
                && PanelMap.SuppressInputContent(_specKey, it.Section);
            if (!string.IsNullOrEmpty(it.State) && !hideState) sb.Append("，").Append(it.State);
            if (it.Locked) sb.Append("，当前不可用");
            // 位置只在换分组时报一次总数；在同一组里移动时只报「第 N 项」，
            // 免得每按一下方向键都拖一句「共 20 项」。
            if (it.InList)
            {
                sb.Append("，第 ").Append(it.Index).Append(" 项");
                if (withSection) sb.Append("，共 ").Append(it.Count).Append(" 项");
            }
            return sb.ToString();
        }

        /// <summary>
        /// 标签来源优先级（G70/G72：玩家听到的只能来自游戏可见文本或游戏自己的数据字段）：
        ///   1) 场景图标：游戏自己的对象名/入口名 + 截图核对（PanelMap.SceneIconLabel）
        ///   2) 游戏画面上可见的 TMP 文字
        ///   3) 逐作层的游戏字段/路径映射（UiNav.GameLabelOf）
        ///   4) 都不行：只念角色，不编名字（除非玩家手动打开 CfgSpeakRawName）
        /// </summary>
        internal static string LabelOf(Selectable s)
        {
            if (s == null) return "";
            try
            {
                if (Domain == Kind.Scene)
                {
                    string icon = PanelMap.SceneIconLabel(s.gameObject.name);
                    if (!string.IsNullOrEmpty(icon)) return icon;
                }
                // 纯图片按钮（关闭 X 等）没有任何可见文字，图片旁边最近的文本往往属于别的控件，
                // 这里按游戏自己的对象名给出约定标签（依据登记在 docs/交互形态判定.md §5）。
                string over = PanelMap.LabelOverride(s.gameObject.name);
                if (!string.IsNullOrEmpty(over)) return over;

                // 主界面唱片机的进度条：它子树里的文字是曲名（那是曲目按钮的内容），
                // 滑条本身表达的是「播放到哪了」，直接念曲名会误导。
                if (s is Slider && IsMusicProgress(s)) return "播放进度";
                // 点「音量」展开的音量滑条（…/Slider/Buttons/VolumeButton/Common_HSlider）：
                // 路径映射会把它也认成「播放进度」，还会和进度条撞名（新增项就播不出来）。
                if (s is Slider && IsMusicVolumeSlider(s)) return "音乐音量";

                string text = VisibleText(s);
                if (!string.IsNullOrEmpty(text) && !UiNav.GameTextIsNoise(text)) return text;

                string mapped = "";
                try { mapped = UiNav.GameLabelOf(s); } catch { }

                // 输入框/滑条/开关的名字常常画在旁边的独立文本节点上（游戏没有 Label 关联），
                // 这里按「同一行旁边 / 最近一小段可见文本」关联，找不到再退回路径映射。
                // 例：背景设置的环境音效开关自己没有文字，名字「仲夏虫吟」画在它旁边；
                // 路径映射只能给出「环境音效 4」，对盲人没有信息量。
                if (s is TMPro.TMP_InputField || s is Slider || s is Toggle)
                {
                    string assoc = AssocLabel(s);
                    if (!string.IsNullOrEmpty(assoc) && (s is Toggle || string.IsNullOrEmpty(mapped))) return assoc;
                }

                if (!string.IsNullOrEmpty(mapped)) return mapped;

                bool raw = A11yHost.CfgSpeakRawName != null && A11yHost.CfgSpeakRawName.Value;
                if (raw && !string.IsNullOrEmpty(s.gameObject.name)) return RoleOf(s) + "，" + s.gameObject.name;
            }
            catch (Exception e) { A11yHost.Diag("[焦点] 取标签异常: " + e.Message); }
            return "";
        }

        /// <summary>主界面唱片机的播放进度条（FullScreen/WindowCanvas/PetMusicPlayer_Window/Slider）。</summary>
        private static bool IsMusicProgress(Selectable s)
        {
            try
            {
                var t = s.transform;
                return t.name == "Slider" && t.parent != null && t.parent.name == "PetMusicPlayer_Window";
            }
            catch { return false; }
        }

        /// <summary>主界面唱片机「音量」按钮展开的音量滑条（…/Slider/Buttons/VolumeButton/Common_HSlider）。</summary>
        private static bool IsMusicVolumeSlider(Selectable s)
        {
            try
            {
                var t = s.transform;
                for (int i = 0; i < 3 && t != null; i++, t = t.parent)
                    if (t.name == "VolumeButton") return true;
                return false;
            }
            catch { return false; }
        }

        private static string VisibleText(Selectable s)
        {
            try
            {
                var tmp = s.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text.Replace("\n", " ").Trim();
                var p = s.transform.parent;
                if (p != null)
                {
                    // 只有「这个文本确实只属于这一小组控件」时才能借父节点的文本。
                    // 面板根下第一个 TMP 往往是面板标题（关闭按钮会因此被念成「特别」「系统设置」），
                    // 所以焦点域的根节点一律不借；但像背景设置的环境音效槽（名字 + 开关 + 滑条）这种
                    // 小组是可以借的：名字就画在槽里，开关自己没有任何文字。
                    int cnt = p.GetComponentsInChildren<Selectable>(true).Length;
                    bool borrow = cnt <= 1 || (p != Root && cnt <= 4);
                    if (borrow)
                    {
                        var t2 = p.GetComponentInChildren<TMPro.TMP_Text>(true);
                        if (t2 != null && !string.IsNullOrEmpty(t2.text)) return t2.text.Replace("\n", " ").Trim();
                    }
                }
            }
            catch { }
            return "";
        }

        private static string StateOf(Selectable s)
        {
            try
            {
                var tg = s as Toggle;
                if (tg != null) return tg.isOn ? "已开启" : "已关闭";
                var sl = s as Slider;
                if (sl != null) return ValueText(sl);
                var inp = s.GetComponent<TMPro.TMP_InputField>();
                if (inp != null)
                {
                    string v = inp.text;
                    if (string.IsNullOrEmpty(v)) return "空";
                    return "内容：" + (v.Length > 40 ? v.Substring(0, 40) + "…" : v);
                }
            }
            catch { }
            return "";
        }

        /// <summary>滑条当前值：整数滑条念整数，小数滑条念百分比（都是控件自己的数据）。</summary>
        private static string ValueText(Slider s)
        {
            if (s == null) return "";
            if (s.wholeNumbers) return Mathf.RoundToInt(s.value).ToString();
            float span = s.maxValue - s.minValue;
            if (span <= 0f) return s.value.ToString("0.##");
            return Mathf.RoundToInt((s.value - s.minValue) / span * 100f) + "%";
        }

        private static string RoleOf(Selectable s)
        {
            if (s == null) return "控件";
            if (s is Button) return "按钮";
            if (s is Toggle) return "开关";
            if (s is Slider) return "滑条";
            if (s is Scrollbar) return "滚动条";
            if (s.GetComponent<TMPro.TMP_InputField>() != null) return "输入框";
            return "控件";
        }

        /// <summary>是不是「关闭/取消/返回」这类一按就离开面板的控件（按标签判断，不看对象名）。</summary>
        private static bool IsDismissive(Selectable s)
        {
            if (s == null) return false;
            string label = LabelOf(s) ?? "";
            if (label.IndexOf("关闭", StringComparison.Ordinal) >= 0
                || label.IndexOf("取消", StringComparison.Ordinal) >= 0
                || label.IndexOf("返回", StringComparison.Ordinal) >= 0) return true;
            // 纯图片关闭按钮：本作无本地化表、读不到文字，只用对象名做分类，绝不念给玩家（G72）
            string n = s.gameObject.name ?? "";
            return n.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Back", StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string RelPath(Transform t)
        {
            if (t == null || Root == null) return "";
            var parts = new List<string>();
            int guard = 0;
            while (t != null && t != Root && guard++ < 24)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            /* 面板根之前的层级不要（面板根的名字已经在 _specKey 里） */
            if (t == null) return "";
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string TitleOf(Transform root)
        {
            if (root == null) return "面板";
            try
            {
                var t = root.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (t != null && !string.IsNullOrEmpty(t.text))
                {
                    string s = t.text.Replace("\n", " ").Trim();
                    if (s.Length > 24) s = s.Substring(0, 24);
                    return s;
                }
            }
            catch { }
            return "面板";
        }

        /// <summary>把面板里可见的文字按屏幕位置串起来（信息型面板：今天吃啥、追番助手）。</summary>
        /// <summary>
        /// 面板上「不挂在任何控件上的说明文字」（菜品名、状态提示、推荐结果…）。
        /// 只用来比对「按下去之后有没有新的字冒出来」，不参与标签生成（G72：对象名/内部名不进朗读）。
        /// </summary>
        private static List<string> InfoTexts()
        {
            var list = new List<string>();
            if (Root == null) return list;
            try
            {
                var texts = Root.GetComponentsInChildren<TMPro.TMP_Text>(true);
                for (int i = 0; i < texts.Length && list.Count < 40; i++)
                {
                    var t = texts[i];
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (!t.gameObject.activeInHierarchy) continue;
                    if (AlphaOf(t.transform) < 0.05f) continue;
                    // 挂在控件里的文字属于控件标签，已经在控件的描述里念过，这里排除，避免重复
                    if (IsInsideSelectable(t.transform)) continue;
                    string s = t.text.Replace("\n", " ").Trim();
                    if (s.Length == 0 || s.Length > 40) continue;
                    if (UiNav.GameTextIsNoise(s)) continue;
                    // 「游戏简介：」这种只有名字没有内容的标签不念，等正文填上再来
                    string trimmed = s.TrimEnd();
                    if (trimmed.EndsWith("：") || trimmed.EndsWith(":")) continue;
                    if (!list.Contains(s)) list.Add(s);
                }
            }
            catch { }
            return list;
        }

        private static bool IsInsideSelectable(Transform t)
        {
            if (t == null || Root == null) return false;
            for (var p = t; p != null && p != Root; p = p.parent)
                if (p.GetComponent<Selectable>() != null) return true;
            return false;
        }

        private static string ContentText(Transform root, int maxLength, bool excludeSelectable = false)
        {
            if (root == null) return "";
            try
            {
                var texts = root.GetComponentsInChildren<TMPro.TMP_Text>(true);
                var found = new List<KeyValuePair<Vector3, string>>();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || string.IsNullOrEmpty(t.text)) continue;
                    if (!t.gameObject.activeInHierarchy) continue;
                    if (AlphaOf(t.transform) < 0.05f) continue;
                    // 提示/确认框的正文要排除按钮上的字，否则会把「好的 / 不好也得好」也当正文念一遍
                    if (excludeSelectable && IsInsideSelectable(t.transform)) continue;
                    string s = t.text.Replace("\n", " ").Trim();
                    if (s.Length == 0) continue;
                    if (UiNav.GameTextIsNoise(s)) continue;
                    // 纯中文句子里的换行会变成空格，念起来像断句异常；没有字母数字时直接去掉空格
                    if (!System.Text.RegularExpressions.Regex.IsMatch(s, "[A-Za-z0-9]")) s = s.Replace(" ", "");
                    found.Add(new KeyValuePair<Vector3, string>(t.transform.position, s));
                }
                found.Sort(delegate (KeyValuePair<Vector3, string> a, KeyValuePair<Vector3, string> b)
                {
                    if (Mathf.Abs(a.Key.y - b.Key.y) > 6f) return b.Key.y.CompareTo(a.Key.y);
                    return a.Key.x.CompareTo(b.Key.x);
                });
                var sb = new StringBuilder();
                var uniq = new List<string>();
                for (int i = 0; i < found.Count; i++)
                {
                    string s = found[i].Value;
                    if (uniq.Contains(s)) continue;
                    uniq.Add(s);
                    if (sb.Length > 0) sb.Append("。");
                    sb.Append(s);
                    if (sb.Length >= maxLength) break;
                }
                string result = sb.ToString();
                if (result.Length > maxLength) result = result.Substring(0, maxLength) + "…";
                return result;
            }
            catch { return ""; }
        }

        // ==================== 游戏自己的返回栈（只读） ====================

        private static bool UiBusy()
        {
            try
            {
                var ui = UiInstance();
                if (ui == null) return false;
                var f = ui.GetType().GetField("isInTransition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return false;
                object v = f.GetValue(ui);
                return v is bool && (bool)v;
            }
            catch { return false; }
        }

        private static GameObject UiStackTop()
        {
            try
            {
                var ui = UiInstance();
                if (ui == null) return null;
                var f = ui.GetType().GetField("uiStack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (f == null) return null;
                var stack = f.GetValue(ui) as System.Collections.IEnumerable;
                if (stack == null) return null;
                foreach (var o in stack)
                {
                    var go = o as GameObject;
                    if (go != null) return go;
                }
            }
            catch { }
            return null;
        }

        private static UIManager UiInstance()
        {
            try
            {
                var p = typeof(UIManager).GetProperty("Instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p != null) return p.GetValue(null, null) as UIManager;
            }
            catch { }
            try { return UnityEngine.Object.FindObjectOfType<UIManager>(); } catch { }
            return null;
        }
    }
}
