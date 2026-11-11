// ============================================================================
// ⚠⚠  0.2.x 遗留实现 —— 新作默认不启用  ⚠⚠
//
// 本文件是「UiNav 通用导航模式」：Tab 进入 / 退出 → 扫屏收集 Selectable →
// 自己维护游标 → 接管 submit。它**不符合第 0 条纪律**（docs/交互形态纪律.md）：
// 补丁不应该另造一个操作状态，界面的键盘操作应当是**游戏本身键盘能力的
// 复用 / 补全**。
//
// 新作品先做 docs/交互形态判定.md 的 A / B / C 判定：
//   · A 类（游戏已有完全键盘控制）：只朗读，**不要**调用 UiNav.Update()；
//   · B / C 类：把键盘能力补在**游戏自己的**输入 / 焦点 / 动作函数上；
//   · 任何情况下都不要新增「进入 / 退出模式」的开关。
//
// 保留本文件只为让已发布老作品**逐字节不变**（平台层是三作复用的冻结层）。
// 保留不等于推荐 —— 新作不要照抄这个形态。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MeiKongA11y
{
    // ========================================================================
    // A11yFramework · L1 平台层（**冻结层**，逐作整份复用，不重写）
    //
    // 来源：三个已发布仓库里最完整的一份（《钟塔》BelfryForAccessibility），
    //       并已按《无障碍补丁流水线》§2.2 把对 Plugin.* 的引用改指 A11yHost.*。
    //
    // 逐作使用时**只允许改两处**：
    //   1) namespace —— 由 tools/new_game.py 自动替换，不要手改
    //   2) 本文件里所有 A11yHost.* 成员由契约层（contract/A11yHost.cs）提供，
    //      逐作不需要改这里的代码，只需要在 Plugin.Awake 里把值填进去
    //
    // 下文注释里出现的《钟塔》/《透明的她与真实的我》/《不/存在的你，和我》
    // 是**实测背景的记录**。保留原文是为了不丢掉「这条规则是被哪次事故逼出来的」
    // 这一证据（见流水线 §1.3、§5）。不要因为「看起来像别的项目」而删掉。
    //
    // 纪律：本层**绝不允许**引用任何游戏类型（Assembly-CSharp 里的任何东西）、
    //       任何逐作配置项、Il2CppInterop / Il2CppSystem。
    //       由 tools/compile_check.ps1 编译验证（不引用 Assembly-CSharp）。
    // ========================================================================
    /// <summary>
    /// 菜单 / 存档 / 设置等界面的键盘导航与朗读。
    ///
    ///   Tab          进入 / 退出导航模式（**0.2.2 起可换**：优先问 A11yHost.NavTogglePressed，
    ///                逐作可配 <c>Ctrl+Alt+G</c> 这类组合键；钩子没填时退回 Tab）
    ///   上 / 下      上一项 / 下一项
    ///   左 / 右      调整滑条
    ///   回车 / 空格  激活（按钮点击、开关切换、输入框聚焦）
    ///   Home / End   第一项 / 最后一项
    ///   PageUp/PageDown  切换面板组（默认只导航最上层那一组）
    ///
    /// === 标签的降级（0.2.2，用户反馈的问题 1）===
    ///
    ///   Bound    控件子树里的文本 / 逐作 GameLabelOf 给出的名字 → 照念
    ///   Nearby   别名表 / 同一行的兄弟文本 → 照念
    ///   Semantic 一条可读文字都没有 → 念「未命名按钮，第 3 项，共 7 项，面板设置」
    ///   Raw      原始对象名 / 类型名 / ID → **默认不念**（A11yHost.CfgSpeakRawName），只进 Diag 日志
    ///
    ///   推断出来的标签（Semantic / Raw）下，激活键第一次只重复朗读并要求再按一次
    ///   （A11yHost.CfgUnknownConfirm 默认 true）—— 见 Update 里的降级确认状态机。
    ///
    /// === IL2CPP 纪律（0.2.2，阻断级修复）===
    ///
    ///   本文件**不许**用 <c>as</c> / <c>is</c> 判断 Unity 组件类型。Il2CppInterop 是按
    ///   「方法声明返回类型」造代理对象的：<c>s.transform</c> 拿到的代理声明类型是
    ///   Transform，所以 <c>s.transform is RectTransform</c> 恒 false、<c>as</c> 恒 null，
    ///   而且静默不报错（界面明明有控件，一个都扫不到；文本一个都读不到）。
    ///   一律改用 <c>GetComponent&lt;T&gt;()</c>，它按原生类做兼容性判断。
    ///
    /// === 回车 / 空格的归属（状态机，见 F）===
    ///
    ///   非导航模式                      → 不归我们，交给游戏推进剧情
    ///   导航模式 + 当前没有可用控件      → 退出导航，交回游戏推进剧情
    ///   导航模式 + 当前有可用控件        → 归我们，激活该控件
    ///
    ///   判定点是「本帧开始时导航模式是否有效且选中项还活着」，
    ///   而不是「EventSystem 里有没有选中对象」—— 后者会被鼠标点击和
    ///   游戏的 AutoFocusInputField 干扰，不能用来决定按键归属。
    ///
    /// === 设计约束（每条都对应一次实测故障）===
    ///
    /// A) 【只导航最上层面板组】
    ///    实测存档界面一次能扫到 13~20 个控件，其中只有 5 个是存档槽，
    ///    其余是设置页签、画廊等无关按钮，读屏用户要在一堆噪声里找目标。
    ///    现在按「Canvas 下的顶层祖先」分组，默认只进入渲染层级最高的
    ///    那一组，用 PageUp/PageDown 手动切换。
    ///
    /// B) 【排序按完整渲染路径，而不是只看屏幕坐标】
    ///    确认框是覆盖层：LoadSlotUI.OnSaveSlotClicked 直接
    ///    confirmLoadPanel.SetActive(true)，背后存档槽按钮仍然 active。
    ///    只按坐标排会让弹窗按钮混在底层按钮中间。
    ///    现在按兄弟序号路径逐级比较（Unity UI 中后渲染的在上层）。
    ///
    /// C) 【标签必须拼接多个文本】
    ///    SaveSlot 有 saveNameText / saveTimeText / chapterText 三个字段，
    ///    只取「子物体里第一个 TMP」会让 5 个空槽位读出完全相同的
    ///    「无存档」，用户无法区分，表现就像「每页只能选一个槽」。
    ///    现在把不同的文本拼起来（最多 3 段）。
    ///
    /// D) 【绝不定时重扫 + 场景切换必须复位】
    ///    v0.3.0 曾在激活按钮后 0.35 秒重扫界面。「开始游戏」「读档」会立刻
    ///    SceneManager.LoadSceneAsync，重扫恰好落在场景销毁/激活瞬间，
    ///    对正在销毁的 Selectable 调用 FindObjectsOfTypeAll 会触发
    ///    无 C# 异常的原生崩溃。
    ///    现在只在激活后的「下一帧」重扫（旧场景仍完整存活），
    ///    且必须通过 SceneStable 门禁。
    ///
    /// E) 【恢复视觉反馈】
    ///    用 EventSystem.SetSelectedGameObject 让游戏自己的按钮高亮态生效。
    ///
    /// F) 【按键归属必须是显式的，不能靠「有没有选中对象」推断】
    ///    实测：退出导航模式后按空格推进剧情，会把上一次导航到的那个按钮
    ///    又点一遍。原因是 EventSystem.currentSelectedGameObject 在我们退出后
    ///    仍然指着那个按钮，而 StandaloneInputModule 一见回车/空格就向它发
    ///    submit。鼠标点过的按钮同理（Selectable.OnPointerDown 会自动选中自己）。
    ///
    ///    现在改成两件事：
    ///      1) 导航模式**确实要接管这一下提交**时，掐掉 uGUI 那条 submit 通路
    ///         （见 ShouldMuteUnitySubmit，由 Patches 挂在
    ///         StandaloneInputModule.SendSubmitEventToSelectedObject 上）；
    ///      2) 回车/空格在我们自己手里时才算「提交」（见 Update 里的状态机），
    ///         并且同一帧拦住游戏自身那次多余的推进（见 BlockGameAdvance）。
    ///
    ///    这一作**不能**像《透明的她与真实的我》那样常驻关掉
    ///    EventSystem.sendNavigationEvents —— 《钟塔》自己就在用选中态做
    ///    横条导航、面板提交、焦点修正（见 ShouldMuteUnitySubmit 的注释）。
    /// </summary>
    internal static partial class UiNav
    {
        private class Group
        {
            public Transform Root;
            public int CanvasOrder;
            public int SiblingIndex;
            public readonly List<Selectable> Items = new List<Selectable>();

            // 本组所在 Canvas 的世界矩形与相机：用来判断成员是不是真的在画面上
            public Rect CanvasRect;
            public bool HasCanvasRect;
            public Camera Camera;
        }

        private static readonly List<Group> Groups = new List<Group>();
        private static int _groupIndex;
        private static List<Selectable> Items { get { return Groups[_groupIndex].Items; } }

        private static bool _active;
        private static int _index;
        private static int _pendingRescanFrame = -1;
        // 稍晚再补一次重扫：面板常常有滑入/淡入动画，下一帧新控件可能还没激活
        private static int _pendingRescanFrame2 = -1;

        // 视觉反馈用：我们自己借 EventSystem 选中过的对象，退出时要清掉
        private static GameObject _selectedByUs;

        // 「提交键」状态机：本帧是否已由我们处理，以及是否正在执行我们发起的动作
        private static int _submitHandledFrame = -1;
        private static bool _inOurActivation;

        // 上一次朗读过的控件。重扫后如果这个位置换了别的控件，必须重新播报 ——
        // 否则玩家以为还停在刚才听的那一项上，按下去却是另一个东西。
        private static Selectable _announcedItem;

        // 当前项失效时我们请求过一次重扫，记下来免得每帧都重扫
        private static Selectable _rescanRequestedFor;

        // 场景切换防护
        private static int _lastSceneHandle = int.MinValue;
        private static float _sceneChangedAt = float.NegativeInfinity;
        private const float SceneSettleSeconds = 1.5f;

        // 输入框角色缓存（实例 ID -> 「姓氏」/「名字」/空串）。
        // 声明留在引擎侧的理由：Scan() 每次重扫都要 Clear() 它（实例 ID 会在
        // 对象销毁后被复用），而真正算出角色的是逐作区的 InputFieldRoleName
        // —— 那边把游戏自己的类型名写死在反射里，所以那块属于逐作层。
        private static readonly Dictionary<int, string> _inputRoles = new Dictionary<int, string>();

        public static bool Active { get { return _active; } }

        /// <summary>
        /// 只在「界面诊断日志」打开时写日志。
        ///
        /// 导航模式的状态迁移（进入 / 退出 / 重扫）光看朗读序列分不清是谁触发的，
        /// 实机排查时把帧号和当前状态一并写下来，才能对上「哪一次按键产生了
        /// 哪几次迁移」。关掉时是一行空判断，没有开销。
        /// </summary>
        // ================================================================
        // ★ 本文件里所有对玩家的播报一律走 Repeat.Say —— **不许直接调 Speech.Speak**。
        //
        // 理由：直接调 = 玩家按重读键听不到它。这个坑踩过两次
        // （《钟塔》0.1.0.1「选项播报绕过了缓冲区」/《妹控计划》0.1.0.0 有选项时按退格念不出选项）。
        // 由 tools\lint_repeat.ps1 机械保证：platform\ 下只有 Repeat.cs 允许出现 Speech.Speak。
        // ================================================================

        private static void Diag(string msg)
        {
            A11yHost.Diag("[UiNav] " + msg);
        }

        // ================= 进入 / 退出 =================

        public static void Toggle()
        {
            if (_active) ExitInternal(true);
            else Enter();
        }

        private static void Enter()
        {
            Diag("Enter  frame=" + Time.frameCount);
            if (!SceneStable())
            {
                Repeat.Say("场景正在切换，请稍候再试。", true);
                return;
            }

            Scan();
            if (Groups.Count == 0 || Items.Count == 0)
            {
                Repeat.Say("当前界面上没有可操作的项目。", true);
                return;
            }

            _active = true;
            _index = 0;
            AnnounceGroup(true);
        }

        private static void ExitInternal(bool announce)
        {
            Diag("ExitInternal announce=" + announce + " frame=" + Time.frameCount
                + " active=" + _active + " groups=" + Groups.Count);
            _active = false;
            Groups.Clear();
            _index = 0;
            _pendingRescanFrame = -1;
            _announcedItem = null;
            ClearQuitConfirm();
            // 退出导航 = 之前武装的降级确认作废，否则 8 秒内重新进入后按回车
            // 会莫名其妙“确认”一个旧控件（与退出确认同一条清理纪律）。
            ClearDegradedConfirm();
            ReleaseSelection();
            if (announce)
            {
                try { Repeat.Say("已退出导航模式。", true); } catch { }
            }
        }

        /// <summary>
        /// 是否该掐掉 uGUI 自己那条 submit 通路。
        ///
        /// === 这一作和上一作的关键差别 ===
        ///
        /// 《透明的她与真实的我》全代码没有一处 SetSelectedGameObject，
        /// 所以上一作是**常驻**把 EventSystem.sendNavigationEvents 关掉，
        /// 一劳永逸地消灭「按空格推进剧情会顺手把上次选中的按钮再点一次」。
        ///
        /// 《钟塔》不能那么干：游戏自己就在用 EventSystem 选中态 ——
        ///   · MainBarNavigationController  用 SetSelectedGameObject 做横条导航
        ///   · MainUIController.ExecuteClickOnSelectedElement
        ///                                  面板打开时「回车 = 点当前选中项」
        ///   · GlobalFocusFixer             每 0.2 秒检查并修正焦点
        ///   · SaveSlotUI.OnSubmit          存档槽自己实现了 ISubmitHandler
        /// 常驻关掉会连带打断这些原生功能（尤其是手柄的 Submit）。
        ///
        /// 所以这一作改成**只在导航模式确实要接管这一下按键时**才掐：
        /// 判据与 BlockGameAdvance 完全一致，由 Patches 挂在
        /// StandaloneInputModule.SendSubmitEventToSelectedObject 上。
        /// 非导航模式下游戏的一切按键行为原样保留。
        /// </summary>
        internal static bool ShouldMuteUnitySubmit
        {
            get { return _active && BlockGameAdvance; }
        }

        /// <summary>清掉我们自己设的选中态，去掉高亮、也不给 uGUI 留提交目标。</summary>
        private static void ReleaseSelection()
        {
            GameObject go = _selectedByUs;
            _selectedByUs = null;
            if (go == null) return;   // 已被销毁的也算 null，直接跳过
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject == go)
                    es.SetSelectedGameObject(null);
            }
            catch { }
        }

        /// <summary>把某个对象设成当前选中（用于让游戏自己的高亮态生效）。</summary>
        private static void SelectByUs(GameObject go)
        {
            if (go == null) return;
            try
            {
                EventSystem es = EventSystem.current;
                if (es == null) return;
                es.SetSelectedGameObject(go);
                _selectedByUs = go;
            }
            catch { }
        }

        private static bool SceneStable()
        {
            try
            {
                Scene sc = SceneManager.GetActiveScene();
                if (!sc.isLoaded) return false;
                return Time.realtimeSinceStartup - _sceneChangedAt >= SceneSettleSeconds;
            }
            catch { return false; }
        }

        // ================= 扫描与分组 =================

        private static void Scan()
        {
            int keepGroupSibling = (Groups.Count > 0 && _groupIndex >= 0 && _groupIndex < Groups.Count)
                ? Groups[_groupIndex].SiblingIndex : int.MinValue;
            Groups.Clear();
            _inputRoles.Clear();   // 实例 ID 会在对象销毁后被复用，每次重扫都重算
            _rescanRequestedFor = null;
            if (!SceneStable()) return;

            try
            {
                bool diag = A11yHost.CfgDiagLog != null && A11yHost.CfgDiagLog.Value;
                List<string> excluded = diag ? new List<string>() : null;
                int inactive = 0, noCanvas = 0;
                Scene activeScene = SceneManager.GetActiveScene();

                Selectable[] all = Resources.FindObjectsOfTypeAll<Selectable>();
                for (int i = 0; i < all.Length; i++)
                {
                    Selectable s = all[i];
                    if (s == null) continue;                       // Unity 伪空：已销毁对象在此拦下
                    if (!s.isActiveAndEnabled)
                    {
                        // 诊断：把「场景里有、但没纳入导航」的控件也记下来。
                        // 只看当前场景，且最多 20 条，否则隐藏面板会淹没日志。
                        if (diag && inactive < 20 && s.gameObject.scene.IsValid()
                            && s.gameObject.scene == activeScene)
                        {
                            excluded.Add("[未激活] " + PathOf(s.transform));
                            inactive++;
                        }
                        continue;
                    }
                    if (!s.gameObject.scene.IsValid()) continue;   // 排除预制体资源

                    // 只处理 UI。**不能**写 `s.transform is RectTransform`：
                    // IL2CPP（Il2CppInterop）按「方法的声明返回类型」造代理，
                    // s.transform 的声明类型是 Transform，所以哪怕底层真是
                    // RectTransform，`is` 也恒 false（`as` 恒 null），而且静默不报错。
                    // 这行是 Scan 的第一道过滤器 —— 写成 is 的后果是「一个控件都扫不到」，
                    // 玩家听到的是「当前界面上没有可操作的项目」。
                    // GetComponent<T>() 走原生类兼容性判断，是 IL2CPP 下的正确写法。
                    if (s.GetComponent<RectTransform>() == null) continue;

                    // 人工排除名单。默认排除标题画面的社交链接条：
                    // ClockwiseMenuController 的五个图标是玩家要的，它旁边那条
                    // 「Steam / 小黑盒 / Bilibili」是外部链接，读屏用户会在
                    // 10 项里撞见 5 项噪音，而且里面还嵌套了一层同名按钮。
                    // 名字来自 probe 的运行时场景树转储。
                    if (IsExcluded(s.transform))
                    {
                        if (diag) excluded.Add("[人工排除] " + PathOf(s.transform));
                        continue;
                    }

                    Group g = GroupOf(s);
                    if (g != null) g.Items.Add(s);
                    else if (diag && noCanvas < 10)
                    {
                        excluded.Add("[不在 Canvas 下] " + PathOf(s.transform));
                        noCanvas++;
                    }
                }

                // 只留下**真的在画面上**的控件。这一步必须在排序之前做：
                // 画面外/被挡住的控件不但念了没用，还会把编号撑大（「1 / 13」）。
                if (A11yHost.CfgVisibleOnly == null || A11yHost.CfgVisibleOnly.Value)
                {
                    for (int i = 0; i < Groups.Count; i++)
                    {
                        Group g = Groups[i];
                        if (diag)
                        {
                            g.Items.RemoveAll(s =>
                            {
                                bool ok = VisiblyClickable(s, g);
                                if (!ok && excluded.Count < 60) excluded.Add("[画面外或点不到] " + PathOf(s.transform));
                                return !ok;
                            });
                        }
                        else
                        {
                            g.Items.RemoveAll(s => !VisiblyClickable(s, g));
                        }
                    }
                }

                // 组排序：先问逐作钩子要权重（小的在前），权重相同才回到
                // 「Canvas 层级高的、兄弟序号靠后的（在更上层）排前面」。
                // 钩子没填时 GroupPriority 全部相等 → 排序结果与从前完全一致。
                Groups.Sort((a, b) =>
                {
                    int pa = GroupPriority(a.Root), pb = GroupPriority(b.Root);
                    if (pa != pb) return pa.CompareTo(pb);
                    if (a.CanvasOrder != b.CanvasOrder) return b.CanvasOrder.CompareTo(a.CanvasOrder);
                    return b.SiblingIndex.CompareTo(a.SiblingIndex);
                });
                Groups.RemoveAll(g => g.Items.Count == 0);
                for (int i = 0; i < Groups.Count; i++) SortWithin(Groups[i]);

                _groupIndex = 0;
                if (keepGroupSibling != int.MinValue)
                {
                    int found = Groups.FindIndex(g => g.SiblingIndex == keepGroupSibling);
                    if (found >= 0) _groupIndex = found;
                }

                if (A11yHost.Log != null)
                {
                    var sb = new StringBuilder();
                    sb.Append("[UiNav] 扫描到 ").Append(Groups.Count).Append(" 组：");
                    for (int i = 0; i < Groups.Count && i < 6; i++)
                    {
                        sb.Append(Groups[i].Root != null ? Groups[i].Root.name : "?")
                          .Append('(').Append(Groups[i].Items.Count).Append(") ");
                    }
                    A11yHost.Log.LogInfo(sb.ToString());
                }

                DumpScan(excluded);
            }
            catch (Exception e)
            {
                A11yHost.Log.LogError("扫描界面控件失败: " + e.Message);
                Groups.Clear();
            }
        }

        /// <summary>
        /// 控件自己或它的任意祖先名字命中排除名单时返回 true。
        ///
        /// 用「祖先链」而不是只看自己：标题那条社交链接是一个容器
        /// （Attention [Image,VerticalLayoutGroup,LinkManager,Button]）
        /// 里面套着 Steam / Xiaoheihe / Bilibili，其中一个还再套了一层按钮。
        /// 只比自己的话，容器被排掉了、子按钮还会留在列表里。
        /// </summary>
        private static bool IsExcluded(Transform t)
        {
            try
            {
                string cfg = A11yHost.CfgExcludeNames != null ? A11yHost.CfgExcludeNames.Value : "";
                if (string.IsNullOrEmpty(cfg)) return false;
                string[] wants = cfg.Split(new char[] { ',', '，' });

                for (Transform cur = t; cur != null; cur = cur.parent)
                {
                    string name = cur.name ?? "";
                    for (int i = 0; i < wants.Length; i++)
                    {
                        string want = wants[i].Trim();
                        if (want.Length == 0) continue;
                        if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (cur.parent == null) break;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>取该控件在 Canvas 下的顶层祖先作为分组依据。</summary>
        private static Group GroupOf(Selectable s)        {
            try
            {
                Canvas c = s.GetComponentInParent<Canvas>();
                if (c == null) return null;
                Transform canvasT = c.transform;

                Transform top = s.transform;
                Transform t = s.transform;
                while (t != null && t != canvasT)
                {
                    top = t;
                    t = t.parent;
                }
                if (t == null) return null;   // 不在该 Canvas 下

                Group g = Groups.Find(x => x.Root == top);
                if (g == null)
                {
                    g = new Group
                    {
                        Root = top,
                        CanvasOrder = c.sortingOrder,
                        SiblingIndex = top.GetSiblingIndex(),
                        Camera = c.renderMode == RenderMode.ScreenSpaceOverlay ? null : c.worldCamera
                    };
                    // IL2CPP：`c.transform as RectTransform` 恒 null（代理按声明类型），
                    // 用 GetComponent<RectTransform>()。
                    RectTransform crt = c.GetComponent<RectTransform>();
                    if (crt != null)
                    {
                        g.CanvasRect = WorldRect(crt);
                        g.HasCanvasRect = true;
                    }
                    Groups.Add(g);
                }
                return g;
            }
            catch { return null; }
        }

        /// <summary>
        /// 组内排序：**按屏幕位置**（先上后下，同一行先左后右），
        /// 位置重合时才退回渲染层级（兄弟序号路径）决定先后。
        ///
        /// === 为什么改成位置优先 ===
        /// 原来以兄弟序号路径为主。但路径表达的是「谁后渲染、谁在上层」，
        /// 跟画面上看到的上下左右没有必然关系：实测有界面出现
        /// 「上面的控件是 2/x、下面的反而是 1/x」。
        /// 读屏用户是靠「第几项」建立空间印象的（部分视力用户还会
        /// 对着屏幕找），顺序必须和画面一致，否则每项都要重新试。
        ///
        /// 行号用「向上量化到 4 像素」的格子算，而不是直接比浮点 y：
        /// 比较函数必须是**可传递**的全序。若用「差值小于阈值就算同一行」
        /// 这种写法，可能出现 a≈b、b≈c 但 a 与 c 不同行的情况，
        /// List.Sort 会抛「比较函数不一致」，而这里被 try 兜住后
        /// 会把整组清空、界面导航直接退出。量化不存在这个问题。
        /// </summary>
        private static void SortWithin(Group g)
        {
            // 开关见配置「按屏幕位置排序控件」：关掉就退回旧的渲染层级排序，
            // 方便对比「顺序错乱 / 控件定位不到」到底是不是这个改动引起的。
            bool byPosition = A11yHost.CfgSortByPosition == null || A11yHost.CfgSortByPosition.Value;

            var keys = new List<ItemKey>(g.Items.Count);
            for (int i = 0; i < g.Items.Count; i++)
            {
                Selectable s = g.Items[i];
                var k = new ItemKey { S = s, Row = int.MinValue, Left = float.MaxValue, Path = null };
                try
                {
                    // IL2CPP：不能用 `s.transform as RectTransform`（恒 null），
                    // 用 GetComponent<RectTransform>()。这一行决定「第几项」的顺序，
                    // 失败的话整组退化成按层级排，和画面对不上。
                    RectTransform rt = s != null ? s.GetComponent<RectTransform>() : null;
                    if (byPosition && rt != null)
                    {
                        rt.GetWorldCorners(_corners);   // 0=左下 1=左上 2=右上 3=右下
                        float top = Mathf.Max(_corners[1].y, _corners[2].y);
                        k.Left = Mathf.Min(_corners[0].x, _corners[1].x);
                        k.Row = Mathf.RoundToInt(top / 4f);
                    }
                    if (s != null) k.Path = PathIndices(s.transform, g.Root).ToArray();
                }
                catch { }
                keys.Add(k);
            }

            if (byPosition) keys.Sort(CompareKey);
            else keys.Sort(ComparePathOnly);

            g.Items.Clear();
            for (int i = 0; i < keys.Count; i++) g.Items.Add(keys[i].S);
        }

        private struct ItemKey
        {
            public Selectable S;
            public int Row;        // 屏幕上下：值越大越靠上
            public float Left;     // 屏幕左右：值越小越靠左
            public int[] Path;     // 渲染层级，仅用于位置完全重合时
        }

        private static readonly Vector3[] _corners = new Vector3[4];

        private static int CompareKey(ItemKey a, ItemKey b)
        {
            if (a.Row != b.Row) return b.Row.CompareTo(a.Row);                        // 上 → 下
            if (Mathf.Abs(a.Left - b.Left) > 0.01f) return a.Left.CompareTo(b.Left);  // 左 → 右
            return CompareIndexPath(a.Path, b.Path);                                  // 重合看渲染层级
        }

        private static int ComparePathOnly(ItemKey a, ItemKey b)
        {
            return CompareIndexPath(a.Path, b.Path);
        }

        private static int CompareIndexPath(int[] pa, int[] pb)
        {
            if (pa == null || pb == null) return 0;
            int n = Mathf.Min(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                if (pa[i] != pb[i]) return pb[i].CompareTo(pa[i]);   // 后渲染的在上层
            }
            return pb.Length.CompareTo(pa.Length);   // 更深的（更靠内层）在后
        }

        private static List<int> PathIndices(Transform t, Transform root)
        {
            var list = new List<int>();
            Transform cur = t;
            while (cur != null && cur != root)
            {
                list.Add(cur.GetSiblingIndex());
                cur = cur.parent;
            }
            list.Reverse();
            return list;
        }

        // ================= 可见性判断 =================

        /// <summary>RectTransform 的世界坐标外接矩形。</summary>
        private static Rect WorldRect(RectTransform rt)
        {
            rt.GetWorldCorners(_corners);   // 0=左下 1=左上 2=右上 3=右下
            float minX = Mathf.Min(Mathf.Min(_corners[0].x, _corners[1].x), Mathf.Min(_corners[2].x, _corners[3].x));
            float maxX = Mathf.Max(Mathf.Max(_corners[0].x, _corners[1].x), Mathf.Max(_corners[2].x, _corners[3].x));
            float minY = Mathf.Min(Mathf.Min(_corners[0].y, _corners[1].y), Mathf.Min(_corners[2].y, _corners[3].y));
            float maxY = Mathf.Max(Mathf.Max(_corners[0].y, _corners[1].y), Mathf.Max(_corners[2].y, _corners[3].y));
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        /// <summary>CanvasGroup alpha 低于这个值就不算「看得见」（0.2.4）。</summary>
        private const float AlphaVisibleThreshold = 0.05f;

        /// <summary>
        /// 从 t 往上最多 12 层，任意一层 CanvasGroup.alpha ≤ 0.05 就算「被隐藏」。
        ///
        /// === 为什么 (0.2.4) ===
        /// 本作 PlayerLine 那批预制体占位按钮是 **active 的**，但靠 CanvasGroup
        /// alpha≈0 隐藏：isActiveAndEnabled、Canvas 矩形相交、UI 射线**全都能过**，
        /// 于是 16 个占位按钮进了导航（实机日志：`[组 2] ... 项数=16`）。
        /// 射线打得到不等于玩家看得见 —— 必须补 alpha 这道门槛。
        /// </summary>
        private static bool AlphaHidden(Transform t)
        {
            try
            {
                Transform cur = t;
                for (int i = 0; cur != null && i < 12; i++)
                {
                    CanvasGroup cg = cur.GetComponent<CanvasGroup>();
                    if (cg != null && cg.alpha <= AlphaVisibleThreshold) return true;
                    cur = cur.parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 这个 Transform 上是不是一个「真在画面里」的控件（给行标签判断用；不发射线，
        /// 因为 FirstTextOutside / HasOtherSelectable 是静态逻辑，不该依赖 EventSystem）。
        /// </summary>
        private static bool VisibleControlOn(Transform t)
        {
            try
            {
                if (t == null || !t.gameObject.activeInHierarchy) return false;
                if (t.GetComponent<Selectable>() == null) return false;
                return !AlphaHidden(t);
            }
            catch { return false; }
        }

        /// <summary>
        /// 控件是不是**真的在画面上**：矩形要和 Canvas 相交，而且从它中心发出的一次
        /// UI 射线要能打到它（或它的子物体）身上。
        ///
        /// === 为什么必须加这道判断 ===
        /// 标题场景里 `Canvas/Panel/Phone/...` 那整套按钮是 active 的，但手机面板
        /// 停在画面外（anchoredPosition.x = 1260，而 START/EXIT 在 -2713），
        /// 屏幕上一个像素都看不到。只判断 isActiveAndEnabled 会把它们全纳入导航 ——
        /// 于是画面上明明是 START/EXIT，方向键却在走「读档 / 存档」，
        /// 按回车还真的弹出读档确认框，因为 **Unity 的按钮根本不关心自己有没有被画出来**。
        ///
        /// 判不出来时一律「不排除」：少一个控件是功能缺失，多一个控件只是噪声。
        /// </summary>
        private static bool VisiblyClickable(Selectable s, Group g)
        {
            try
            {
                // 0.2.4【实机缺陷】CanvasGroup alpha≈0 的预制体占位：active、矩形在画布内、
                // 射线也打得到，但不是画给玩家看的。先过 alpha 门槛（上溯 12 层），
                // 再谈矩形与遮挡，免得白打一条射线。
                if (AlphaHidden(s.transform)) return false;

                // IL2CPP：`s.transform as RectTransform` 恒 null → 会误判成「判不出来、
                // 一律不排除」，画面外控件全被收进来。用 GetComponent<RectTransform>()。
                RectTransform rt = s.GetComponent<RectTransform>();
                if (rt == null) return true;

                // 1) 和 Canvas 矩形完全不相交 = 画面外
                if (g.HasCanvasRect)
                {
                    Rect r = WorldRect(rt);
                    if (r.xMax < g.CanvasRect.xMin - 2f || r.xMin > g.CanvasRect.xMax + 2f) return false;
                    if (r.yMax < g.CanvasRect.yMin - 2f || r.yMin > g.CanvasRect.yMax + 2f) return false;
                }

                // 2) 从中心打一条 UI 射线，看能不能打到自己
                EventSystem es = EventSystem.current;
                if (es == null) return true;

                Vector3 world = rt.TransformPoint(rt.rect.center);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(g.Camera, world);
                var ped = new PointerEventData(es) { position = screen };
                var hits = new List<RaycastResult>();
                // 第二个参数的类型**两边不一样**：Mono 版（BepInEx 5 / PC）是
                // System.Collections.Generic.List<RaycastResult>，IL2CPP 版
                // （Il2CppInterop）是 Il2CppSystem.Collections.Generic.List<RaycastResult>。
                // 同一份平台层源码要在两边都编得过，就不能写死任何一边，所以走
                // RaycastAllCompat（反射拿运行时真正的参数类型）。详见该方法的注释。
                if (!RaycastAllCompat(es, ped, hits)) return true;   // 判不出 → 不排除
                if (hits.Count == 0) return true;      // 射线本身打不到任何东西 → 判不出来，不排除

                for (int i = 0; i < hits.Count; i++)
                {
                    GameObject hit = hits[i].gameObject;
                    if (hit == null) continue;
                    // 只认「打到自己或自己的子物体」。**不能**认祖先：
                    // 整屏背景通常就是所有控件的共同祖先，认了它等于这道判断失效。
                    if (hit == s.gameObject || hit.transform.IsChildOf(s.transform)) return true;
                }
                return false;
            }
            catch { return true; }
        }

        // ---- EventSystem.RaycastAll 的跨运行时调用（Mono / IL2CPP）----
        //
        // 为什么不能直接写 `es.RaycastAll(ped, hits)`：
        //   · Mono（BepInEx 5 / PC 版引用集）签名是 List<RaycastResult>（System 集合）；
        //   · IL2CPP（Il2CppInterop）签名是 Il2CppSystem.Collections.Generic.List<RaycastResult>，
        //     System 的 List 传进去是编译错误 CS1503（本作实机编译实测）。
        // 平台层是同一份源码被两边复用，写死任何一边都会让另一边编不过；而引用
        // Il2CppInterop 又是契约层红线（平台层不得依赖 IL2CPP 专用程序集）。
        //
        // 所以：反射拿 EventSystem.RaycastAll 的 MethodInfo（按名字 + 两个参数找，
        // 不写参数类型，否则同样会绑死一种运行时），按它**运行时**的第二个参数类型
        // 造一个空列表传进去，再把结果读回调用方的普通 List<RaycastResult>。
        // 只有在「真的拿不到射线结果」时才返回 false，调用方按「判不出遮挡」处理
        // （宁可多收控件，也不能因为一次反射失败把所有项都排除掉）。
        private static MethodInfo _raycastAllMethod;
        private static bool _raycastAllLookupDone;

        /// <summary>
        /// 调用 EventSystem.RaycastAll，把结果填进 results。返回 false = 这套运行时下
        /// 判不出遮挡（调用方不应该据此排除控件）。
        /// </summary>
        private static bool RaycastAllCompat(EventSystem es, PointerEventData ped, List<RaycastResult> results)
        {
            if (es == null || ped == null || results == null) return false;
            try
            {
                if (!_raycastAllLookupDone)
                {
                    _raycastAllLookupDone = true;
                    MethodInfo[] methods = es.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    for (int i = 0; i < methods.Length; i++)
                    {
                        MethodInfo m = methods[i];
                        if (m.Name != "RaycastAll") continue;
                        if (m.GetParameters().Length != 2) continue;
                        _raycastAllMethod = m;
                        break;
                    }
                }
                if (_raycastAllMethod == null) return false;

                ParameterInfo[] pars = _raycastAllMethod.GetParameters();
                object runtimeList = Activator.CreateInstance(pars[1].ParameterType);
                if (runtimeList == null) return false;

                _raycastAllMethod.Invoke(es, new object[] { ped, runtimeList });

                results.Clear();
                System.Collections.IEnumerable seq = runtimeList as System.Collections.IEnumerable;
                if (seq != null)
                {
                    foreach (object item in seq)
                    {
                        if (item is RaycastResult) results.Add((RaycastResult)item);
                    }
                    return true;
                }

                // 少数运行时不给非泛型 IEnumerable：退回 Count / Item 索引器。
                Type lt = runtimeList.GetType();
                PropertyInfo countProp = lt.GetProperty("Count");
                PropertyInfo itemProp = lt.GetProperty("Item");
                if (countProp == null || itemProp == null) return false;
                int n = (int)countProp.GetValue(runtimeList, null);
                for (int i = 0; i < n; i++)
                    results.Add((RaycastResult)itemProp.GetValue(runtimeList, new object[] { i }));
                return true;
            }
            catch
            {
                // 反射/代理构造失败都当「判不出」，绝不把异常抛给 Scan。
                return false;
            }
        }

        // ================= 诊断（配置「界面诊断日志」打开时才输出）=================

        /// <summary>控件在层级里的路径，形如 Canvas/Panel/Confirm/Yes。</summary>
        private static string PathOf(Transform t)
        {
            try
            {
                var stack = new List<string>();
                Transform cur = t;
                int guard = 0;
                while (cur != null && guard++ < 12)
                {
                    stack.Add(cur.gameObject.name);
                    cur = cur.parent;
                }
                var sb = new StringBuilder();
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    if (sb.Length > 0) sb.Append('/');
                    sb.Append(stack[i]);
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static string RowLeftOf(Selectable s)
        {
            try
            {
                // IL2CPP：不能用 `s.transform as RectTransform`，见 Scan 里的说明。
                RectTransform rt = s != null ? s.GetComponent<RectTransform>() : null;
                if (rt == null) return "位置=?";
                rt.GetWorldCorners(_corners);
                float top = Mathf.Max(_corners[1].y, _corners[2].y);
                float left = Mathf.Min(_corners[0].x, _corners[1].x);
                return "行=" + Mathf.RoundToInt(top / 4f) + " 顶=" + Mathf.RoundToInt(top)
                     + " 左=" + Mathf.RoundToInt(left);
            }
            catch { return "位置=?"; }
        }

        /// <summary>
        /// 把这次扫描的结果写进日志：每一组的每一项念什么、在屏幕哪儿、层级路径是什么，
        /// 以及「场景里有、但没被纳入导航」的控件。
        /// 我这边没法启动游戏，只能靠这份日志定位「某个控件找不到」「只念类型不念文字」。
        /// </summary>
        private static void DumpScan(List<string> excluded)
        {
            try
            {
                if (A11yHost.CfgDiagLog == null || !A11yHost.CfgDiagLog.Value) return;
                if (A11yHost.Log == null) return;

                var sb = new StringBuilder();
                sb.Append("[UiNav] ===== 诊断：共 ").Append(Groups.Count).Append(" 组 =====");
                for (int gi = 0; gi < Groups.Count; gi++)
                {
                    Group g = Groups[gi];
                    sb.Append("\n[组 ").Append(gi + 1).Append(gi == _groupIndex ? " ★当前" : "")
                      .Append("] ").Append(g.Root != null ? PathOf(g.Root) : "?")
                      .Append("  项数=").Append(g.Items.Count);
                }
                if (Groups.Count > 0 && _groupIndex >= 0 && _groupIndex < Groups.Count)
                {
                    Group g = Groups[_groupIndex];
                    for (int i = 0; i < g.Items.Count; i++)
                    {
                        Selectable s = g.Items[i];
                        string desc = "?";
                        try { desc = Describe(s); } catch (Exception e) { desc = "<Describe 抛异常: " + e.Message + ">"; }
                        sb.Append("\n  #").Append(i + 1).Append(' ').Append(desc)
                          .Append("   [").Append(s != null ? RowLeftOf(s) : "null").Append(']')
                          .Append("  ").Append(s != null ? PathOf(s.transform) : "null");
                    }
                }
                if (excluded != null && excluded.Count > 0)
                {
                    sb.Append("\n[有控件但没纳入导航 ").Append(excluded.Count).Append(" 条]");
                    for (int i = 0; i < excluded.Count; i++) sb.Append("\n  ").Append(excluded[i]);
                }
                sb.Append("\n[UiNav] ==== 诊断结束 ====");
                A11yHost.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                try { A11yHost.Log.LogError("诊断输出失败: " + e.Message); } catch { }
            }
        }

        // ================= 描述 =================

        /// <summary>
        /// 拼接该控件及其子物体里的文本。
        /// 只取第一个会踩坑：SaveSlot 的 saveNameText / saveTimeText /
        /// chapterText 是三个字段，5 个空槽的第一个文本都是「无存档」，
        /// 读出来完全一样，用户无法区分。
        /// </summary>
        /// <summary>把一段文本压成可朗读的一行（去换行、去空白）。</summary>
        private static string Norm(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return "";
            return txt.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
        }

        /// <summary>控件自己子树里的文本（最多 3 段）。没有则返回空串。</summary>
        private static string OwnTextOf(Selectable s)
        {
            var parts = new List<string>();
            try
            {
                TextMeshProUGUI[] all = s.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < all.Length && parts.Count < 3; i++)
                {
                    if (all[i] == null) continue;
                    // 必须先过 Noise（逐作 GameTextIsNoise），再判「有没有文字」。
                    // 顺序反了的话，预制体占位文本（`New Text` / `Button`）会被当成
                    // 可读标签，GameLabelOf 那条逐作钩子永远轮不到被调用 ——
                    // 而「有文本但读错」的控件正是钩子唯一能纠正的那类。
                    string txt = Noise(Norm(all[i].text));
                    if (txt.Length == 0) continue;
                    bool dup = false;
                    for (int j = 0; j < parts.Count; j++)
                        if (parts[j] == txt) { dup = true; break; }
                    if (!dup) parts.Add(txt);
                }
            }
            catch { }
            return parts.Count == 0 ? "" : string.Join("，", parts.ToArray());
        }


        /// <summary>
        /// 没有信息量的样板对象名。上溯找「有意义的名字」时要跳过它们，
        /// 否则永远停在 Slider / Toggle / ControlButton 上。
        /// </summary>
        private static bool IsBoilerplateName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            switch (n.ToLowerInvariant())
            {
                case "button": case "controlbutton": case "toggle": case "slider":
                case "text": case "image": case "textimage": case "rawimage":
                case "panel": case "option": case "options": case "labels":
                case "background": case "checkmark": case "line": case "staticpic":
                case "content": case "item": case "root": case "group":
                case "fill": case "handle": case "area":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 从 parent 的子树里取第一段**不属于 exclude 子树**的文本。
        ///
        /// 两个排除条件都很重要：
        ///   - 排除自己那一支，否则可能读到自己内部的字；
        ///   - 排除落在**别的控件**（按钮/开关/滑条）里的文本，
        ///     那是那个控件的标签，不是这一行的标签。
        /// 另外只接受在层级中处于激活状态的文本，免得读到隐藏面板的字。
        ///
        /// 这里同时认 TMP 和旧版 UnityEngine.UI.Text：设置面板的 Text 节点
        /// 从场景里读不到静态文字（运行期才由本地化表填进去），无法确认是哪一种，
        /// 两种都认最省事，也不会有副作用。
        /// </summary>
        /// <summary>
        /// parent 子树里除了 exclude（这个控件自己那一支）之外，还有没有别的 Selectable。
        ///
        /// 用来区分「一行」和「一个面板」：
        ///   · 一行（设置面板的 BGMSlider 之类）里只有这一个控件 + 一段标签文字
        ///   · 面板（手机面板的 Buttons/Image）里塞着一堆控件
        /// 面板里的文字是标题，不能当成本控件的标签 —— 手机面板顶上那行「通讯」
        /// 原来就是这样被当成了里面**每一个**按钮的标签，整屏按钮全念「通讯」。
        /// </summary>
        private static bool HasOtherSelectable(Transform parent, Transform exclude)
        {
            try
            {
                Selectable[] all = parent.GetComponentsInChildren<Selectable>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Selectable s = all[i];
                    if (s == null) continue;
                    Transform t = s.transform;
                    if (t == exclude || t.IsChildOf(exclude)) continue;

                    // === 0.2.4【实机缺陷】只算「真在画面里」的控件 ===
                    //
                    // 旧实现用 GetComponentsInChildren<Selectable>(true)，把**未激活**的
                    // 控件也算成「这一行里还有别的控件」，于是判定为「面板」→
                    // FirstTextOutside 直接 return ""，行标签彻底拿不到。
                    // 本作设置面板的 Item_Switch / Item_Toggle 内部就各带一个未激活的
                    // ToggleItem/Toggle，结果是同一行的 text_Name（「游戏时不显示」）
                    // 永远没机会被读到，开关只剩「未命名按钮」。
                    if (!s.gameObject.activeInHierarchy) continue;
                    if (AlphaHidden(t)) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string FirstTextOutside(Transform parent, Transform exclude)
        {
            try
            {
                // 这个容器里还有别的控件 → 它是面板，不是「一行」
                if (HasOtherSelectable(parent, exclude)) return "";

                var cands = new List<Component>();
                try { cands.AddRange(parent.GetComponentsInChildren<TextMeshProUGUI>(true)); }
                catch { }
                try { cands.AddRange(parent.GetComponentsInChildren<Text>(true)); }
                catch { }

                for (int i = 0; i < cands.Count; i++)
                {
                    Component c = cands[i];
                    if (c == null) continue;
                    if (!c.gameObject.activeInHierarchy) continue;
                    // 0.2.4：alpha≈0 的文字也是「看不见」的，不能当行标签。
                    if (AlphaHidden(c.transform)) continue;

                    Transform tt = c.transform;
                    if (tt == exclude || tt.IsChildOf(exclude)) continue;

                    // 从这段文字往上走，只要在本行范围内遇到别的**可见**控件，就说明
                    // 它属于那个控件，不是行标签。未激活/alpha=0 的隐藏控件不算
                    // （0.2.4：否则设置行的 text_Name 会被内部未激活的 Toggle 挡掉）。
                    bool other = false;
                    Transform cur = tt;
                    while (cur != null && cur != parent)
                    {
                        if (VisibleControlOn(cur)) { other = true; break; }
                        cur = cur.parent;
                    }
                    if (other) continue;

                    string txt = TextOn(c);
                    if (txt.Length > 0) return txt;
                }
            }
            catch { }
            return "";
        }

        /// <summary>取组件上的文本，TMP 与旧版 Text 都认。</summary>
        private static string TextOn(Component c)
        {
            if (c == null) return "";

            // === IL2CPP：这里是「文本读取的唯一收口」，绝不能写 as ===
            //
            // FirstTextOutside 传进来的是按 Component 取出来的对象；Il2CppInterop
            // 按「方法声明返回类型」造代理，所以 `c as TextMeshProUGUI` / `c as Text`
            // 恒 null —— 后果不是报错，而是所有控件都读不到文字、全部退化到
            // 对象名 / 语义描述（而且第一版连对象名都念）。用 GetComponent<T>()
            // 让原生层判断「这个 GameObject 上有没有这个组件」。
            TextMeshProUGUI tmp = c.GetComponent<TextMeshProUGUI>();
            if (tmp != null) return Noise(Norm(tmp.text));
            Text legacy = c.GetComponent<Text>();
            if (legacy != null) return Noise(Norm(legacy.text));
            return "";
        }

        /// <summary>
        /// 逐作钩子：「这段文字是预制体占位，别念」。
        /// 钩子没填时原样返回 —— 其它三作的行为与从前逐字节一致。
        /// </summary>
        private static string Noise(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            try
            {
                var f = A11yHost.GameTextIsNoise;
                if (f != null && f(s)) return "";
            }
            catch { }
            return s;
        }

        /// <summary>同一个「行」里的标签文本：从自己往上找，最多两层。</summary>
        private static string RowTextOf(Selectable s)
        {
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 2 && t != null; up++)
                {
                    Transform p = t.parent;
                    if (p == null) break;
                    string found = FirstTextOutside(p, t);
                    if (found.Length > 0) return found;
                    t = p;
                }
            }
            catch { }
            return "";
        }

        /// <summary>别名命中的是不是控件自己（而不是某一级祖先行名）。</summary>
        private static bool AliasOnSelf(Selectable s)
        {
            try
            {
                string n = s.gameObject.name;
                return n != null && NameAlias.ContainsKey(n);
            }
            catch { return false; }
        }

        /// <summary>从自己往上（最多 4 层）找第一个命中中文别名表的祖先名。</summary>
        private static string AliasAncestorOf(Selectable s)
        {
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 4 && t != null; up++)
                {
                    string n = t.gameObject.name;
                    string alias;
                    if (n != null && NameAlias.TryGetValue(n, out alias)) return alias;
                    t = t.parent;
                }
            }
            catch { }
            return "";
        }

        // ================= 标签解析（0.2.2：分层质量）=================
        //
        // 用户反馈的问题 1：控件绑定的 Text 读不到时，平台层直接把 GameObject 名 /
        // 类型名 / ID 当标签念出来，而且**没有任何降级行为**就执行激活。
        //
        // 根因是旧 TextOf 的最后一行 `return AncestorNameOf(s)`（AncestorNameOf 最后
        // `return s.gameObject.name`）—— 它保证 TextOf 永远返回非空字符串，调用方
        // 无法区分「真标签」和「对象名」。对象名还经常长得像人话（Exit / Close），
        // 玩家会以为自己听到的就是那个按钮的名字。
        //
        // 现在把「标签」和「它的可信度」一起返回（LabelResult），调用方据此决定：
        //   Bound    直接念（子树文字 / 逐作 GameLabelOf）
        //   Nearby   直接念（别名表 / 同行兄弟文字）
        //   Semantic 念结构化语义描述（SemanticText），并进入激活前二次确认
        //   Raw      原始对象名；只有 CfgSpeakRawName=true 才念，否则同 Semantic。
        //            原始名一律写 Diag 日志，方便排查「这个控件到底是什么」。

        /// <summary>标签可信度。数值越大越不可信；调用方用 &gt;= Semantic 判「这是推断出来的」。</summary>
        internal enum LabelQuality
        {
            Bound = 0,     // 控件自己的文本 / 逐作钩子给出的名字
            Nearby = 1,    // 别名表 / 同一行的兄弟文本
            Semantic = 2,  // 没有任何可读文字 → 结构化语义描述
            Raw = 3        // 原始对象名 / 类型名 / ID
        }

        /// <summary>一次标签解析的结果。</summary>
        internal struct LabelResult
        {
            public string Text;
            public LabelQuality Quality;
            public string Source;   // own / gamelabel / alias / row / placeholder / role / ancestor / semantic

            public LabelResult(string text, LabelQuality quality, string source)
            {
                Text = text;
                Quality = quality;
                Source = source;
            }
        }

        /// <summary>原始标识（对象名 / 类型名）是否允许念出来。为 null 按 false 处理。</summary>
        private static bool SpeakRawName()
        {
            try { return A11yHost.CfgSpeakRawName != null && A11yHost.CfgSpeakRawName.Value; }
            catch { return false; }
        }

        /// <summary>
        /// 控件的可读标签。
        ///
        /// === 为什么要分三层找（设置面板实测结构）===
        ///
        ///   TextSpeed        [行]
        ///     Text            ← 中文「文本显示速度」，是控件的**兄弟**，不是子节点
        ///     Slider          ← 控件，子树里只有 Background / Fill Area / Handle
        ///   ScreenPixel1080  [单选项]
        ///     Text            ← 「1280×720」
        ///     Toggle          ← 控件
        ///
        /// 只查控件自己的子树，slider 和 toggle 都会一个字都找不到，
        /// 于是退回对象名，读出「Slider」「Toggle」——只有类型，没有用途。
        ///
        ///   1) 自己子树有文本 → 直接用（存档槽、按钮等绝大多数情况走这条，行为不变）
        ///   2) 同一行的兄弟文本 → 用，并在前面补上所属行名（窗口分辨率、画面模式…）
        ///   3) 逐作钩子 / 别名表 → 见下面的顺序
        ///   4) 都没有 → 不念对象名，返回 Semantic（调用方换成 SemanticText）
        /// </summary>
        private static LabelResult ResolveLabel(Selectable s)
        {
            if (s == null) return new LabelResult("", LabelQuality.Semantic, "semantic");

            // 输入框走自己的规则：OwnTextOf 读到的是「已输入的内容 + 占位提示」，
            // 拼成标签会变成「<内容>，输入框，当前内容 <内容>」。
            TMP_InputField inf = s.GetComponent<TMP_InputField>();
            if (inf != null) return ResolveInputLabel(inf);

            // 1) 自己子树里的文本（已过 Noise）
            string own = OwnTextOf(s);
            if (own.Length > 0) return new LabelResult(own, LabelQuality.Bound, "own");

            // 2) 逐作钩子放在「同行兄弟文字」**之前**，让它有覆盖权：
            // 有些控件的同行文字不是它的标签（实测：音乐播放器滑条的同行是**当前曲名**，
            // 念出来成了「<曲名>，滑条」——把曲名当成了控件名）。
            // 钩子返回空串时照旧往下走，所以对没覆盖到的控件没有任何影响。
            try
            {
                var fGame = A11yHost.GameLabelOf;
                if (fGame != null)
                {
                    string gameFirst = fGame(s);
                    if (!string.IsNullOrEmpty(gameFirst))
                        return new LabelResult(gameFirst, LabelQuality.Bound, "gamelabel");
                }
            }
            catch { }

            // 3) 别名表 + 同行兄弟文字
            string row = AliasAncestorOf(s);
            string near = RowTextOf(s);
            if (near.Length > 0)
            {
                if (row.Length > 0 && row != near)
                {
                    // 别名命中控件**自己**时（确认框的 Yes/No 这类纯图片按钮），
                    // 面板上的问题读在前、按钮名读在后：
                    //   「<确认文案>，确定，按钮」
                    // 别名来自祖先行名时保持原顺序（「窗口分辨率，1280×720」）。
                    if (AliasOnSelf(s))
                        return new LabelResult(near + "，" + row, LabelQuality.Nearby, "alias+row");
                    return new LabelResult(row + "，" + near, LabelQuality.Nearby, "alias+row");
                }
                return new LabelResult(near, LabelQuality.Nearby, "row");
            }

            if (row.Length > 0) return new LabelResult(row, LabelQuality.Nearby, "alias");

            // 4) 一条可读文字都没有：默认不念原始名（CfgSpeakRawName）
            return RawOrSemantic(s);
        }

        /// <summary>兜底：CfgSpeakRawName 打开时返回原始名（Raw），否则返回 Semantic（空文本）。</summary>
        private static LabelResult RawOrSemantic(Selectable s)
        {
            string raw = "";
            try { raw = AncestorNameOf(s); } catch { }

            // 原始名只进诊断日志：没开「界面诊断日志」时它永远不会出现在任何地方。
            Diag("未命名控件：类型=" + ControlTypeName(s)
                 + " 原始名=" + (raw.Length > 0 ? raw : "?")
                 + " 路径=" + (s != null ? PathOf(s.transform) : "?"));

            if (SpeakRawName() && raw.Length > 0)
                return new LabelResult(raw, LabelQuality.Raw, "ancestor");
            return new LabelResult("", LabelQuality.Semantic, "semantic");
        }

        /// <summary>
        /// 旧调用点的兼容包装：只要字符串（不关心质量时用）。
        /// **不要**再用它判「有没有标签」—— 推断标签的 Text 是空串，
        /// 请用 ResolveLabel + SemanticText。
        /// </summary>
        private static string TextOf(Selectable s)
        {
            return ResolveLabel(s).Text;
        }

        /// <summary>朗读用的标签：可信标签照念，推断标签换成结构化语义描述。</summary>
        private static string LabelText(Selectable s, LabelResult r)
        {
            if (r.Quality == LabelQuality.Semantic) return SemanticText(s);
            if (r.Quality == LabelQuality.Raw && !SpeakRawName()) return SemanticText(s);
            return string.IsNullOrEmpty(r.Text) ? SemanticText(s) : r.Text;
        }

        /// <summary>
        /// 结构化语义描述：「未命名<控件类型>，第 N 项，共 M 项，面板<组名>」。
        ///
        /// 这是平台层补上的 L2 层：旧实现从 L1（同行文字）直接跳到 L3（对象名），
        /// 中间没有任何「说清楚它在哪」的能力。序号用 Scan 排好的 Items 顺序，
        /// 因此和画面上「第几项」一致（CfgSortByPosition=false 时按层级，见 README）。
        /// </summary>
        private static string SemanticText(Selectable s)
        {
            var sb = new StringBuilder();
            sb.Append("未命名").Append(ControlTypeName(s));

            int n = ItemNumber(s);
            int total = ItemCount(s);
            if (n > 0) sb.Append("，第 ").Append(n).Append(" 项");
            if (total > 0) sb.Append("，共 ").Append(total).Append(" 项");

            string grp = GroupLabelOf(s);
            if (!string.IsNullOrEmpty(grp)) sb.Append("，").Append(grp);
            return sb.ToString();
        }

        /// <summary>
        /// 控件类型的中文说法。
        ///
        /// **不能**用 GetType().Name：IL2CPP 代理的 GetType() 只给出声明类型
        /// （念出来是 `Selectable`），连真实类型名都没有；也不能用 `is`/`as`
        /// （见文件头「IL2CPP 纪律」）。用 GetComponent<T>() 让原生层回答。
        /// </summary>
        private static string ControlTypeName(Selectable s)
        {
            if (s == null) return "控件";
            try
            {
                if (s.GetComponent<TMP_InputField>() != null) return "输入框";
                if (s.GetComponent<Toggle>() != null) return "开关";
                if (s.GetComponent<Slider>() != null) return "滑条";
                if (s.GetComponent<Scrollbar>() != null) return "滚动条";
                if (s.GetComponent<Button>() != null) return "按钮";
            }
            catch { }
            return "控件";
        }

        /// <summary>控件在当前组里的序号（1 基）；找不到返回 0。</summary>
        private static int ItemNumber(Selectable s)
        {
            try
            {
                int total;
                int idx = IndexOfItem(s, out total);
                return idx >= 0 ? idx + 1 : 0;
            }
            catch { return 0; }
        }

        /// <summary>控件所在组的项数；找不到返回 0。</summary>
        private static int ItemCount(Selectable s)
        {
            try
            {
                int total;
                IndexOfItem(s, out total);
                return total;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 控件在 Groups 里的位置：返回值是组内 0 基下标（-1 = 没找到），
        /// total 是该组的项数（找不到时 0）。扫全部组，因为诊断转储和重扫过程中
        /// 会 Describe 不在当前组里的控件。
        /// </summary>
        private static int IndexOfItem(Selectable s, out int total)
        {
            total = 0;
            if (s == null) return -1;
            for (int g = 0; g < Groups.Count; g++)
            {
                List<Selectable> list = Groups[g].Items;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == s)   // Unity ==：伪空对象也认
                    {
                        total = list.Count;
                        return i;
                    }
                }
            }
            return -1;
        }

        /// <summary>
        /// 兜底：从自己往上找第一个不是样板名的对象名。
        ///
        /// **只用于诊断日志**，以及 CfgSpeakRawName 明确打开时的 Raw 标签。
        /// 绝不允许把它直接当朗读标签 —— 那正是用户反馈里「念 GameObject 名 /
        /// 类型名 / ID」的来源。这里保留最后的 `return s.gameObject.name`
        /// 是为了日志能指认到底是哪个控件（排查「念不出名字」时唯一的线索）。
        /// </summary>
        private static string AncestorNameOf(Selectable s)
        {
            try
            {
                Transform t = s.transform;
                for (int up = 0; up < 4 && t != null; up++)
                {
                    string n = t.gameObject.name;
                    if (!IsBoilerplateName(n)) return n;
                    t = t.parent;
                }
            }
            catch { }
            return s.gameObject.name;
        }


        /// <summary>输入框的占位提示文字（TMP 与旧版 Text 都认）。</summary>
        private static string PlaceholderText(TMP_InputField inf)
        {
            try
            {
                Graphic g = inf.placeholder;
                return g != null ? TextOn(g) : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 输入框的标签。**不能**走 ResolveLabel 的普通分支：它的第一层 OwnTextOf
        /// 读的是输入框自己子树里的 TMP，那是「已输入的内容」和占位提示，
        /// 念出来会变成「<已输入内容>，输入框，当前内容 <已输入内容>」这种重复噪声。
        ///
        /// 顺序与旧 InputFieldLabel 一致（逐作角色名 → 逐作钩子 → 占位提示 →
        /// 别名 → 同行），只是最后一步从「念对象名」改成「返回 Semantic」。
        /// </summary>
        private static LabelResult ResolveInputLabel(TMP_InputField inf)
        {
            string role = InputFieldRoleName(inf);
            if (role.Length > 0) return new LabelResult(role, LabelQuality.Bound, "role");

            // 逐作钩子优先：输入框的同行标签（「循环次数」「专注时长」）比占位提示可靠。
            // 但要先挡掉「本地化 key 被当标签」的情况 —— 那属于第 4 类退化
            // （玩家听到的是 `UI.Setting.Name` 这种内部编号）。
            try
            {
                var fGame = A11yHost.GameLabelOf;
                if (fGame != null)
                {
                    string gameLabel = fGame(inf);
                    if (!string.IsNullOrEmpty(gameLabel))
                    {
                        if (LooksLikeLocKey(gameLabel))
                            Diag("输入框标签像本地化 key，已跳过：" + gameLabel);
                        else
                            return new LabelResult(gameLabel, LabelQuality.Bound, "gamelabel");
                    }
                }
            }
            catch { }

            string ph = PlaceholderText(inf);
            if (ph.Length > 0 && LooksLikeLocKey(ph))
            {
                Diag("输入框占位提示像本地化 key，已跳过：" + ph);
                ph = "";
            }
            if (ph.Length > 0) return new LabelResult(ph, LabelQuality.Nearby, "placeholder");

            string row = AliasAncestorOf(inf);
            string near = RowTextOf(inf);
            if (near.Length > 0)
                return new LabelResult((row.Length > 0 && row != near) ? row + "，" + near : near,
                                       LabelQuality.Nearby, "row");
            if (row.Length > 0) return new LabelResult(row, LabelQuality.Nearby, "alias");

            return RawOrSemantic(inf);
        }

        /// <summary>
        /// 这段文字像不像「本地化 key / 内部 ID」。
        ///
        /// 本作大量文本走 LocalizeStringEvent / 本地化表（见 probe 类型清单）。
        /// 本地化组件的 m_Key 形如 `UI.Setting.Name` / `Common.Button.OK`：
        /// 没有空格、只用点/下划线分段、全 ASCII。念出来对玩家毫无意义，
        /// 比「未命名按钮」还糟。这里只做**保守**判断（宁可漏判也不误伤真标签）：
        /// 必须同时满足「含 . 或 _ 分隔」「全 ASCII」「没有空格」「至少两段」。
        /// 真正的英文标签（`Start`）不含分隔符，不会被挡。
        /// </summary>
        private static bool LooksLikeLocKey(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf(' ') >= 0) return false;
            bool hasSep = false;
            int segments = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '.' || c == '_') { hasSep = true; segments++; continue; }
                if (c >= 0x2E80) return false;              // 含中日韩字符 → 是给人看的文字
                if (c < 0x20 || c > 0x7E) return false;     // 非可见 ASCII → 不判
            }
            return hasSep && segments >= 2;
        }

        /// <summary>滑条当前值的说法。0-1 范围的条按百分比念，否则念 N / M。</summary>
        private static string SliderValueText(Slider sl)
        {
            if (sl.minValue >= -0.001f && sl.maxValue <= 1.001f)
                return Mathf.RoundToInt(Mathf.Clamp01(sl.value) * 100f) + "%";
            return Mathf.RoundToInt(sl.value) + " / " + Mathf.RoundToInt(sl.maxValue);
        }

        private static string Describe(Selectable s)
        {
            var sb = new StringBuilder();
            if (s == null) return "";

            // IL2CPP：必须 GetComponent<T>()，不能 as/is（见文件头「IL2CPP 纪律」）。
            // 写成 `s as Toggle` 的话类型判定恒 null → 所有开关/滑条/输入框
            // 都被当成普通控件，"已进入输入框，直接打字" 永不出现。
            Toggle t = s.GetComponent<Toggle>();
            Slider sl = s.GetComponent<Slider>();
            TMP_InputField inf = s.GetComponent<TMP_InputField>();

            // 标签：可信的照念；推断出来的（Semantic、或 Raw 但没允许念原名）
            // 换成结构化语义描述「未命名按钮，第 N 项，共 M 项，面板…」。
            LabelResult label = ResolveLabel(s);
            bool semantic = label.Quality == LabelQuality.Semantic
                            || (label.Quality == LabelQuality.Raw && !SpeakRawName());
            sb.Append(LabelText(s, label));

            if (inf != null)
            {
                // 语义描述里已经含「输入框」，不要重复
                if (!semantic) sb.Append("，输入框");
                string cur = Norm(inf.text);
                if (cur.Length > 0)
                {
                    if (LooksLikeLocKey(cur))
                        Diag("输入框当前内容像本地化 key，已跳过：" + cur);
                    else
                        sb.Append("，当前内容 ").Append(cur);
                }
                else sb.Append("，当前为空");
                if (inf.characterLimit > 0 && inf.characterLimit <= 8)
                    sb.Append("，最多 ").Append(inf.characterLimit).Append(" 个字");
            }
            else if (t != null)
            {
                if (semantic) sb.Append("，").Append(t.isOn ? "开" : "关");
                else sb.Append("，开关，").Append(t.isOn ? "开" : "关");
            }
            else if (sl != null)
            {
                if (semantic) sb.Append("，").Append(SliderValueText(sl));
                else sb.Append("，滑条，").Append(SliderValueText(sl));
            }
            else if (!semantic)
            {
                // 类型后缀。**不能**写 s.GetType().Name：IL2CPP 代理的 GetType()
                // 只返回声明类型（Selectable），念出来等于没信息。
                sb.Append("，").Append(ControlTypeName(s));
            }

            if (!s.interactable) sb.Append("，不可用");
            return sb.ToString();
        }

        private const string GroupFallbackName = "面板";

        /// <summary>
        /// 面板组的中文名。
        ///
        /// 顺序：逐作钩子 → 名字本身含中文（照念）→ CfgSpeakRawName 打开时念原名 →
        /// 中性兜底「面板」。
        ///
        /// 为什么不再无脑念根对象名（0.2.2）：Canvas 下的顶层祖先名都是英文
        /// （`Right` / `UpRight` / `Canvas`），每次进组都念一遍既难听又不提供信息；
        /// 但直接丢掉也不行 —— 多组时全叫「面板」没法区分，所以兜底名带组号
        /// （见 <see cref="GroupLabel"/>）：「面板1」「面板2」。
        /// </summary>
        private static string GroupName(Transform root)
        {
            try
            {
                var f = A11yHost.GameGroupName;
                if (root != null && f != null)
                {
                    string n = f(root);
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }

            if (root == null) return GroupFallbackName;
            string name = root.name ?? "";
            if (HasCjk(name)) return name;          // 中文对象名本身就是给人看的
            if (SpeakRawName()) return name;        // 逐作明确要求念原名（排查用）
            Diag("组名没有中文来源，用中性兜底「" + GroupFallbackName + "」（原始名=" + name + "）");
            return GroupFallbackName;
        }

        /// <summary>字符串里有没有中日韩字符（判断对象名是不是给人看的文字）。</summary>
        private static bool HasCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 0x2E80) return true;
            }
            return false;
        }

        /// <summary>第 gi 组在朗读里的名字：兜底名带组号，避免四组全叫「面板」。</summary>
        private static string GroupLabel(int gi)
        {
            if (gi < 0 || gi >= Groups.Count) return GroupFallbackName;
            string n = GroupName(Groups[gi].Root);
            if (string.Equals(n, GroupFallbackName, StringComparison.Ordinal)) return n + (gi + 1);
            return n;
        }

        /// <summary>
        /// 某个控件所在组在**语义描述**里的说法：有名字时补「面板」前缀
        /// （「面板设置」），兜底名已经带「面板」和组号（「面板2」）。
        /// 返回空串表示找不到组。
        /// </summary>
        private static string GroupLabelOf(Selectable s)
        {
            try
            {
                if (s == null) return "";
                for (int g = 0; g < Groups.Count; g++)
                {
                    List<Selectable> list = Groups[g].Items;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] == s)
                        {
                            string n = GroupName(Groups[g].Root);
                            if (string.Equals(n, GroupFallbackName, StringComparison.Ordinal))
                                return GroupFallbackName + (g + 1);   // 「面板2」
                            return "面板" + n;                       // 「面板设置」
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        /// <summary>组的排序权重，没填钩子时恒为同一个值（排序行为不变）。</summary>
        private static int GroupPriority(Transform root)
        {
            try
            {
                var f = A11yHost.GameGroupPriority;
                if (root != null && f != null) return f(root);
            }
            catch { }
            return 100;
        }

        /// <summary>
        /// 进组播报。**这一段是本作最关键的可操作性修复**：
        ///
        /// 平台层原来只说「导航模式，第 N 组，共 M 项」—— 玩家既不知道总共有几组，
        /// 也不知道别的组是什么，于是「找不到想去的地方」。本作实测默认状态有 4 组、
        /// 而最常用的功能条在**第 2 组**，不换组就永远够不着。
        ///
        /// 现在：进导航时报一次**全组总览**，之后每次换组报「第 N / 共几组：组名，几项」。
        /// </summary>
        private static void AnnounceGroup(bool withCount)
        {
            if (Groups.Count == 0 || Items.Count == 0) return;
            string prefix = withCount ? GroupPrefix(true) : "";
            Announce(prefix);
        }

        private static string GroupPrefix(bool verbose)
        {
            string gname = GroupLabel(_groupIndex);
            var sb = new StringBuilder();
            if (Groups.Count <= 1)
            {
                if (verbose) sb.Append("导航模式，").Append(gname).Append("，共 ").Append(Items.Count).Append(" 项。");
                return sb.ToString();
            }
            if (verbose) sb.Append("导航模式。");
            sb.Append("第 ").Append(_groupIndex + 1).Append(" / ").Append(Groups.Count).Append(" 组：")
              .Append(gname).Append("，").Append(Items.Count).Append(" 项。");
            if (verbose)
            {
                sb.Append("全部 ").Append(Groups.Count).Append(" 组：");
                int shown = 0;
                for (int i = 0; i < Groups.Count && shown < 6; i++)
                {
                    if (Groups[i].Items.Count == 0) continue;
                    if (shown > 0) sb.Append('；');
                    sb.Append(i == _groupIndex ? "【" : "").Append(GroupLabel(i))
                      .Append(' ').Append(Groups[i].Items.Count).Append(" 项").Append(i == _groupIndex ? "】" : "");
                    shown++;
                }
                sb.Append("。用 PageUp / PageDown 换组。");
            }
            return sb.ToString();
        }

        private static void Announce(string prefix)
        {
            if (!_active || Items.Count == 0) return;
            _index = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[_index];
            if (s == null) { ExitInternal(false); return; }

            _announcedItem = s;
            SelectByUs(s.gameObject);

            // 光标移到别处 = 放弃刚才那次退出确认 / 降级确认
            ClearQuitConfirm();
            ClearDegradedConfirm();

            Repeat.Say(prefix + Describe(s) + "。" + (_index + 1) + " / " + Items.Count, true);
        }

        private static void SwitchGroup(int dir)
        {
            if (Groups.Count <= 1)
            {
                Repeat.Say("只有一个面板组。", true);
                return;
            }
            _groupIndex = (_groupIndex + dir + Groups.Count) % Groups.Count;
            _index = 0;
            AnnounceGroup(true);
        }

        // ================= 操作 =================

        /// <summary>当前选中项；导航模式未生效或该项已失效时返回 null。</summary>
        private static Selectable CurrentItem()
        {
            if (!_active) return null;
            if (_groupIndex < 0 || _groupIndex >= Groups.Count) return null;
            if (Items.Count == 0) return null;
            int i = Mathf.Clamp(_index, 0, Items.Count - 1);
            Selectable s = Items[i];
            return s == null ? null : s;   // Unity 伪空：已销毁对象在此拦下
        }

        /// <summary>
        /// 本帧游戏自身那次「推进剧情」是否该被拦掉。
        ///
        /// DialogueSceneManager / Ending2DialogueManager 的 Update 都是在
        /// Input.GetKeyDown(空格/回车) 成立后紧接着调 DialogueButtonClicked()。
        /// 只要那次按键已经归我们（或即将归我们），这次调用就该拦掉，
        /// 剧情才只动一次。
        ///
        /// 两种判据都要有，因为两个 Update 谁先执行是不确定的：
        ///   - 我们已经跑过：本帧接管过提交键 → 拦。
        ///   - 我们还没跑：键正处于按下状态、且我们有可激活的选中项 → 拦。
        ///
        /// 我们自己激活控件时引发的推进要放行 —— 那个按钮本来就是干这个的
        /// （例如全屏热区按钮）。
        /// </summary>
        internal static bool BlockGameAdvance
        {
            get
            {
                if (_inOurActivation) return false;

                try { if (_submitHandledFrame == Time.frameCount) return true; }
                catch { return false; }

                try
                {
                    if (!Input.GetKeyDown(KeyCode.Return)
                        && !Input.GetKeyDown(KeyCode.KeypadEnter)
                        && !Input.GetKeyDown(KeyCode.Space)) return false;
                }
                catch { return false; }

                return CurrentItem() != null;
            }
        }

        // ================= 退出确认 =================
        //
        // 只针对「按一下就把游戏关掉、而游戏自己不给确认框」的那一个控件：
        // 标题画面角落里的 EXIT。
        //
        // 标题画面有两个同类按钮，美术字分别是 START 和 EXIT，只差一个单词，
        // 而区分它们真正靠的是「哪一个是整块大面板、哪一个是角落小图标」——
        // 这种视觉信息读屏拿不到。按错一次整个会话直接没了。
        //
        // 游戏只在标题画面这一处不给确认框：剧情中的「返回标题」、手机菜单的
        // 「退出游戏」游戏自己都会弹原生确认框，我们不能重复问。
        //
        // 靠**对象名精确匹配**，名字是从游戏资源里实查的，不是猜的：
        //   probe 的运行时场景树转储（probe/evidence/scene_tree.txt）里，
        //   标题场景 Canvas/Panel/Background/Menu/Buttons 下就是这五个：
        //     Gallery / Load / Start / Options / Exit
        //   其中只有 Exit 会直接关掉整个游戏（ClockwiseMenuController.Awake 里
        //   SetButtonAction(0, handler.QuitGame)）。
        //
        // 踩过的坑（上一作 v0.5.4）：用正则 \b(exit|quit)\b 匹配，结果反了 ——
        //   ExitButton / QuitGame 是驼峰拼接，单词后面紧跟字母，根本没有 \b 词边界。
        //   教训：这种判定别用词边界，也别用「包含」，直接拿实查到的名字比。

        private static Selectable _pendingQuit;
        private static float _pendingQuitAt;
        private const float QuitConfirmSeconds = 8f;

        private static bool NeedsQuitConfirm(Selectable s)
        {
            if (A11yHost.CfgQuitConfirm == null || !A11yHost.CfgQuitConfirm.Value) return false;
            try
            {
                string cfg = A11yHost.CfgQuitNames != null ? A11yHost.CfgQuitNames.Value : "Exit";
                if (string.IsNullOrEmpty(cfg)) return false;

                string name = s.gameObject.name ?? "";
                string[] wants = cfg.Split(new char[] { ',', '，' });
                for (int i = 0; i < wants.Length; i++)
                {
                    string want = wants[i].Trim();
                    if (want.Length == 0) continue;
                    if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static bool QuitConfirmArmed(Selectable s)
        {
            if (_pendingQuit == null) return false;
            if (Time.realtimeSinceStartup - _pendingQuitAt > QuitConfirmSeconds)
            {
                ClearQuitConfirm();
                return false;
            }
            return _pendingQuit == s;
        }

        private static void ArmQuitConfirm(Selectable s)
        {
            _pendingQuit = s;
            _pendingQuitAt = Time.realtimeSinceStartup;
            // 留痕：万一配错了名字，日志里能看出到底拦的是哪个控件。
            try
            {
                A11yHost.Log.LogInfo("[UiNav] 退出确认：「" + s.gameObject.name + "」标签「"
                    + LabelText(s, ResolveLabel(s)) + "」场景 " + SceneManager.GetActiveScene().name);
            }
            catch { }
            Repeat.Say("这是退出游戏。再按一次回车或空格确认退出，按别的键取消。", true);
        }

        private static void ClearQuitConfirm()
        {
            _pendingQuit = null;
        }

        // ================= 降级确认（0.2.2）=================
        //
        // 用户反馈的问题 1 的另一半：读不到可读文字时，旧实现**没有任何降级行为
        // 就执行激活** —— 玩家听到「Exit」「Button (6)」或者干脆只有类型名，
        // 按回车就把动作做掉了。对象名看起来像人话这一点尤其危险
        // （`Load` / `Exit` / `Close` 真的就是对象名），玩家无法判断自己听到的
        // 到底是控件标签还是内部标识。
        //
        // 现在：标签是**推断**出来的（Semantic，或 Raw）时，第一次按激活键只
        // 重复朗读 + 提示再按一次；第二次才真正 Activate。状态机与退出确认同构
        // （同样的 8 秒窗口、同样的“换项/退出即清除”），避免两套语义。
        //
        // 顺序：退出确认**优先**（Update 里先处理 NeedsQuitConfirm）。退出本来就是
        // 危险动作，若同时再叠一层降级确认，玩家要按三次回车才能退出 —— 所以
        // 已武装退出确认时直接跳过降级确认。

        private static Selectable _degradedTarget;
        private static float _degradedUntil;

        private const float DegradedConfirmSeconds = QuitConfirmSeconds;   // 与退出确认同窗口

        /// <summary>降级确认开关。为 null 按 true（安全默认值）。</summary>
        private static bool UnknownConfirmEnabled()
        {
            try { return A11yHost.CfgUnknownConfirm == null || A11yHost.CfgUnknownConfirm.Value; }
            catch { return true; }
        }

        /// <summary>这个控件的标签是不是推断出来的（需要二次确认）。</summary>
        private static bool NeedsDegradedConfirm(Selectable s)
        {
            if (s == null || !UnknownConfirmEnabled()) return false;
            try
            {
                LabelResult r = ResolveLabel(s);
                return r.Quality >= LabelQuality.Semantic;   // Bound/Nearby 可信，Semantic/Raw 是推断
            }
            catch { return false; }
        }

        /// <summary>
        /// 标签是不是「只能落到未命名语义描述」这一级：没有任何可读来源
        /// （无 Text、无逐作 GameLabelOf、无别名、无同行文字），只剩
        /// 「未命名&lt;类型&gt;，第 N 项」。给激活前的硬阻止用（0.2.4）。
        /// </summary>
        private static bool IsUnnamedLabel(Selectable s)
        {
            if (s == null) return false;
            try
            {
                LabelResult r = ResolveLabel(s);
                return r.Quality == LabelQuality.Semantic
                       || (r.Quality == LabelQuality.Raw && !SpeakRawName());
            }
            catch { return false; }
        }

        private static bool DegradedConfirmArmed(Selectable s)
        {
            if (_degradedTarget == null) return false;
            if (Time.realtimeSinceStartup - _degradedUntil > 0f)
            {
                ClearDegradedConfirm();
                return false;
            }
            return _degradedTarget == s;   // Unity ==：伪空对象也认
        }

        private static void ArmDegradedConfirm(Selectable s)
        {
            _degradedTarget = s;
            _degradedUntil = Time.realtimeSinceStartup + DegradedConfirmSeconds;

            // 原始对象名只进日志（CfgDiagLog / DiagOn 打开时才写），不念。
            Diag("降级确认：路径=" + PathOf(s.transform));

            // 先念能念的（语义描述 + 类型/状态），再要求确认。
            // 必须 interrupt=true：这条提示要是被上一条盖掉，用户就不知道自己在确认什么。
            Repeat.Say(Describe(s) + "。未命名控件，已阻止操作。再按一次确认；按别的键取消。", true);
        }

        private static void ClearDegradedConfirm()
        {
            _degradedTarget = null;
            _degradedUntil = 0f;
        }

        /// <summary>
        /// 每帧校验：目标失效 / 切组 / 退出导航 / 场景切换时清掉。
        /// 这是「清理点一个都不能漏」的兜底 —— 即使某条路径忘了显式调用，
        /// 也不会在 8 秒内按回车确认到一个已经不存在的旧控件。
        /// </summary>
        private static void ValidateDegradedConfirm()
        {
            if (_degradedTarget == null) return;
            try
            {
                if (!_active
                    || !_degradedTarget.isActiveAndEnabled
                    || _degradedTarget != CurrentItem()
                    || Time.realtimeSinceStartup - _degradedUntil > 0f)
                {
                    ClearDegradedConfirm();
                }
            }
            catch { ClearDegradedConfirm(); }
        }

        private static void Activate(Selectable s)
        {
            if (s == null) { ExitInternal(false); return; }

            // 留痕：崩溃排查用。原生崩溃不会在日志里留下任何异常，
            // 只有我们自己事前写下的这一行能指明最后碰的是哪个控件。
            // 类型用 ControlTypeName（真实组件类型），不能用 GetType().Name
            // （IL2CPP 代理只会给出声明类型 Selectable，等于没信息）。
            try
            {
                LabelResult lr = ResolveLabel(s);
                A11yHost.Log.LogInfo("[UiNav] 激活 " + ControlTypeName(s) + "「" + LabelText(s, lr) + "」场景 "
                    + SceneManager.GetActiveScene().name);
                Diag("激活原始对象：路径=" + PathOf(s.transform)
                     + " 来源=" + lr.Source + " 质量=" + lr.Quality);
            }
            catch { }

            if (!s.interactable)
            {
                Repeat.Say(LabelText(s, ResolveLabel(s)) + "，该项当前不可用。", true);
                return;
            }

            try
            {
                // IL2CPP：必须 GetComponent<T>()，不能 `s as T`（恒 null）。
                TMP_InputField inf = s.GetComponent<TMP_InputField>();
                if (inf != null)
                {
                    // 先退出导航（ReleaseSelection 会清掉上一个高亮），
                    // 再把焦点交给输入框，否则刚设的焦点会被自己清掉。
                    ExitInternal(false);
                    SelectByUs(inf.gameObject);
                    inf.ActivateInputField();
                    // 开关键是可配的（逐作 CfgNavKey，默认 Ctrl+Alt+G / 备用 Tab），
                    // 平台层不知道具体键位，只能说「导航开关」。
                    Repeat.Say("已进入输入框，直接打字即可。再次按导航开关返回导航。", true);
                    return;
                }

                Toggle t = s.GetComponent<Toggle>();
                if (t != null)
                {
                    t.isOn = !t.isOn;
                    Repeat.Say(t.isOn ? "开" : "关", false);
                    RequestRescanNextFrame();
                    return;
                }

                Slider sl = s.GetComponent<Slider>();
                if (sl != null)
                {
                    Repeat.Say("滑条请用左右方向键调整。", true);
                    return;
                }

                Button b = s.GetComponent<Button>();
                if (b != null)
                {
                    string label = LabelText(s, ResolveLabel(s));
                    Repeat.Say("已激活 " + label, false);
                    _inOurActivation = true;
                    try { b.onClick.Invoke(); }
                    finally { _inOurActivation = false; }
                    // 下一帧重扫：此刻旧场景仍完整存活；
                    // 若该按钮触发场景切换，离真正卸载还有几十帧
                    RequestRescanNextFrame();
                    return;
                }

                _inOurActivation = true;
                try
                {
                    ExecuteEvents.Execute(s.gameObject, new BaseEventData(EventSystem.current),
                        ExecuteEvents.submitHandler);
                }
                finally { _inOurActivation = false; }
                RequestRescanNextFrame();
            }
            catch (Exception e)
            {
                A11yHost.Log.LogError("激活控件失败: " + e.Message);
            }
        }

        private static void RequestRescanNextFrame()
        {
            _pendingRescanFrame = Time.frameCount + 1;
        }

        /// <summary>再排一次稍晚的重扫：面板滑入/淡入时，下一帧新控件往往还没激活。</summary>
        private static void RequestRescanDelayed()
        {
            _pendingRescanFrame2 = Time.frameCount + 14;
        }

        /// <summary>两份控件清单是不是一模一样（顺序也要一样）。</summary>
        private static bool SameList(List<Selectable> a, List<Selectable> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (!ReferenceEquals(a[i], b[i])) return false;
            }
            return true;
        }

        private static void Adjust(float dir)
        {
            if (Items.Count == 0) return;
            // 方向键是「别的键」：按了就说明玩家不再确认刚才那一项，清掉降级确认。
            ClearDegradedConfirm();
            // IL2CPP：`Items[_index] as Slider` 恒 null → 左右方向键永远调不了滑条。
            // 用 GetComponent<Slider>()（见文件头「IL2CPP 纪律」）。
            Slider sl = _index >= 0 && _index < Items.Count && Items[_index] != null
                        ? Items[_index].GetComponent<Slider>() : null;
            if (sl == null) return;
            try
            {
                float step = (sl.maxValue - sl.minValue) / 20f;
                if (sl.wholeNumbers) step = Mathf.Max(1f, Mathf.Round(step));
                sl.value = Mathf.Clamp(sl.value + dir * step, sl.minValue, sl.maxValue);
                Repeat.Say(SliderValueText(sl), false);
            }
            catch (Exception e) { A11yHost.Log.LogError("调整滑条失败: " + e.Message); }
        }

        // ================= 每帧 =================
        //
        // ⚠ 遗留入口：这就是「导航模式」的每帧驱动（Tab 开关 + 扫屏 + 自维护游标 + 接管 submit）。
        //   新作默认**不调用**它（见文件头警告与 docs/交互形态判定.md 的 A/B/C 判定）：
        //   A 类连这个调用都不该有；B / C 类应把实现放到游戏自己的输入 / 焦点层。
        //   下面一行代码/逻辑属于 0.2.x 遗留行为，改它就是改老作，禁止。
        public static void Update()
        {
            // ---- 场景切换防护：必须放在最前面 ----
            try
            {
                Scene sc = SceneManager.GetActiveScene();
                if (sc.handle != _lastSceneHandle)
                {
                    _lastSceneHandle = sc.handle;
                    _sceneChangedAt = Time.realtimeSinceStartup;
                    KeyEdge.Reset();     // 别把上一个场景的闩锁带过来
                    if (_active || Groups.Count > 0) ExitInternal(false);
                    return;
                }
            }
            catch { }

            // 降级确认的兜底清理：目标失效 / 换项 / 退出导航 / 窗口超时都在这里收口。
            // 放在场景切换之后、重扫之前 —— 每帧都跑一次，但不做任何扫描。
            ValidateDegradedConfirm();

            // ---- 激活后的下一帧重扫（唯一允许的自动扫描）----
            bool rescanDue =
                (_pendingRescanFrame >= 0 && Time.frameCount >= _pendingRescanFrame) ||
                (_pendingRescanFrame2 >= 0 && Time.frameCount >= _pendingRescanFrame2);
            if (rescanDue)
            {
                if (_pendingRescanFrame >= 0 && Time.frameCount >= _pendingRescanFrame) _pendingRescanFrame = -1;
                if (_pendingRescanFrame2 >= 0 && Time.frameCount >= _pendingRescanFrame2) _pendingRescanFrame2 = -1;
                if (_active)
                {
                    if (!SceneStable()) { ExitInternal(false); return; }
                    Selectable before = _announcedItem;
                    var beforeList = new List<Selectable>(Items);
                    Scan();
                    if (Groups.Count == 0 || Items.Count == 0) { ExitInternal(false); return; }
                    _index = Mathf.Clamp(_index, 0, Items.Count - 1);

                    // 列表重建后 _index 还停在原来的序号上，但那个位置上可能已经换了
                    // 别的控件（弹窗、二级菜单、翻页都会这样）。这时必须重新播报，
                    // 否则玩家按下去的是他从没听过的东西 —— 主菜单 START/EXIT 那类
                    // 只差一个单词的按钮，听错一次就出事。
                    //
                    // 注意还要比**整份清单**：打开子菜单时，当前这一项往往还是原来
                    // 那个按钮（序号也没变），但列表里多了新控件。只比当前项的话
                    // 就一声不吭，玩家以为界面没变。实测就是这样：按了 1/7 没有提示，
                    // 直到按 Esc 才听见「界面已更新」。
                    Selectable now = CurrentItem();
                    if (now != before || !SameList(beforeList, Items)) Announce("界面已更新。");
                }
            }

            // ---- 导航开关：优先问逐作钩子，钩子没填时保持原来的 Tab ----
            //
            // 为什么开关由逐作决定（0.2.2）：本作要求导航快捷键是组合键（默认
            // Ctrl+Alt+G），且不能和游戏自己的 HotkeyService 撞键。组合键解析
            // （Hotkey.cs 语义：写了哪个修饰键就要求哪个、必须有主键）、边沿闩锁、
            // 输入框聚焦时不抢键 —— 这些都属于逐作热键系统。平台层只收一个
            // 「这一帧要不要切换导航模式」的布尔值（A11yHost.NavTogglePressed）。
            //
            // 钩子留空 = 前三作行为逐字节不变（Tab）。平台层自己读 Tab 时走
            // KeyEdge.Pressed，内部已 try/catch，不会把 Input 异常抛进主循环。
            bool navToggle;
            try
            {
                Func<bool> navHook = A11yHost.NavTogglePressed;
                navToggle = navHook != null ? navHook() : KeyEdge.Pressed(KeyCode.Tab);
            }
            catch (Exception e)
            {
                // 逐作钩子抛异常不能把平台主循环带走（与 KeyEdge / Hotkey 同一条纪律）。
                Diag("NavTogglePressed 抛异常，本帧按未按下处理：" + e.Message);
                navToggle = false;
            }
            if (navToggle) { Diag("导航开关 检测到 active=" + _active + " frame=" + Time.frameCount); Toggle(); return; }

            // 游戏**自己**换面板时（例如姓名输入按回车 →「<确认文案>」
            // 确认框：promptUI 与 confirmationUI 直接 SetActive 互换）不会通知我们，
            // 列表会一直停在旧控件上，新面板的按钮就「定位不到」。
            // 当前项一旦失效就重扫一次；每个失效对象只请求一次，避免反复重扫。
            if (_active && _pendingRescanFrame < 0)
            {
                Selectable cur = CurrentItem();
                if (cur != null && !cur.isActiveAndEnabled && !ReferenceEquals(cur, _rescanRequestedFor))
                {
                    _rescanRequestedFor = cur;
                    RequestRescanNextFrame();
                }
            }

            // 全部控件失效时先退出导航，把按键还给游戏
            if (_active && (Groups.Count == 0 || Items.Count == 0)) ExitInternal(false);

            // ---- 回车 / 空格：整块状态机唯一的判定点 ----
            //
            //   非导航模式            CurrentItem() 为 null → 什么都不做，游戏自己推进剧情
            //   导航模式 + 没有可用项  CurrentItem() 为 null → 同上
            //   导航模式 + 有可用项    → 我们接管，激活它，并拦住游戏同一帧的推进
            // 用 KeyEdge 而不是裸 Input.GetKeyDown：合成输入下 GetKeyDown 可能
            // 在一次按下里报两次，一次回车就会把控件点两下（存档槽尤其致命）。
            // 详见 KeyEdge 的注释。
            if (KeyEdge.Pressed(KeyCode.Return) || KeyEdge.Pressed(KeyCode.KeypadEnter)
                || KeyEdge.Pressed(KeyCode.Space))
            {
                Selectable target = CurrentItem();
                if (target != null)
                {
                    _submitHandledFrame = Time.frameCount;

                    // 标题画面的 EXIT 一按就关游戏，而游戏自己不给确认框。
                    // 读屏玩家分不清它和旁边的 START（美术字，只差一个单词），
                    // 所以这里补一道二次确认。详见 NeedsQuitConfirm。
                    if (NeedsQuitConfirm(target) && !QuitConfirmArmed(target))
                    {
                        ArmQuitConfirm(target);
                        return;
                    }

                    // 退出确认**优先**：已经武装过退出确认的这一下就是要真退出，
                    // 不再叠降级确认（否则退出要按三次回车，而且容易误以为没生效）。
                    bool quitArmed = QuitConfirmArmed(target);
                    ClearQuitConfirm();

                    // === 0.2.4【阻断级】未命名控件默认不激活 ===
                    //
                    // 用户原话：「中途没有降级行为就执行了这个操作」。占位按钮常常
                    // 还能捡到一段占位文字（quality=Bound），旧实现只对 Semantic/Raw
                    // 做二次确认，于是第一次回车就直接点了。
                    // 现在：标签只能落到「未命名<类型>，第 N 项」时 ——
                    //   · CfgUnknownConfirm 开（默认）→ 第一次只念「已阻止操作 + 再按一次确认」，
                    //     第二次才放行（复用降级确认状态机，不新增配置）；
                    //   · CfgUnknownConfirm 关 → 硬阻止，永不激活。
                    // 退出确认已武装时不叠这一层（否则退出要按三次回车）。
                    if (!quitArmed)
                    {
                        if (IsUnnamedLabel(target))
                        {
                            if (!UnknownConfirmEnabled())
                            {
                                Diag("未命名控件已阻止激活（CfgUnknownConfirm=false）：路径="
                                     + PathOf(target.transform));
                                Repeat.Say("未命名控件，已阻止操作。", true);
                                return;
                            }
                            if (!DegradedConfirmArmed(target))
                            {
                                Diag("未命名控件待复核：路径=" + PathOf(target.transform));
                                ArmDegradedConfirm(target);
                                return;
                            }
                            ClearDegradedConfirm();   // 第二次按下：放行
                        }
                        else if (NeedsDegradedConfirm(target))
                        {
                            // 降级确认：标签是推断出来的（Raw）时，第一次只重复
                            // 朗读 + 提示再按一次，第二次才真正执行。
                            // 必须放在 Activate 之前 —— 旧实现的问题就是「没有任何
                            // 降级行为就执行激活」。
                            if (!DegradedConfirmArmed(target))
                            {
                                ArmDegradedConfirm(target);
                                return;
                            }
                            ClearDegradedConfirm();   // 第二次按下：放行
                        }
                        else
                        {
                            ClearDegradedConfirm();
                        }
                    }
                    else
                    {
                        ClearDegradedConfirm();
                    }

                    Activate(target);

                    // 激活之后界面很可能已经变了（打开子面板、弹出确认框、切页……），
                    // 而游戏自己换面板**不会**通知我们。所以无条件排两次重扫：
                    // 下一帧一次，稍晚再一次（面板有动画时用得上）。
                    // 清单没变就不会播报，多扫一次没有副作用。
                    RequestRescanNextFrame();
                    RequestRescanDelayed();
                }
                return;
            }

            if (!_active) return;

            if (KeyEdge.Pressed(KeyCode.UpArrow))
            {
                _index = (_index - 1 + Items.Count) % Items.Count;
                Announce(null);
            }
            else if (KeyEdge.Pressed(KeyCode.DownArrow))
            {
                _index = (_index + 1) % Items.Count;
                Announce(null);
            }
            else if (KeyEdge.Pressed(KeyCode.PageDown)) SwitchGroup(1);
            else if (KeyEdge.Pressed(KeyCode.PageUp)) SwitchGroup(-1);
            else if (KeyEdge.Pressed(KeyCode.LeftArrow)) Adjust(-1f);
            else if (KeyEdge.Pressed(KeyCode.RightArrow)) Adjust(1f);
            else if (KeyEdge.Pressed(KeyCode.Home)) { _index = 0; Announce(null); }
            else if (KeyEdge.Pressed(KeyCode.End)) { _index = Items.Count - 1; Announce(null); }
        }
    }
}
