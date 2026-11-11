using System;
using System.Collections.Generic;
using UnityEngine;

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
    /// 带「松开闩锁」的单次按键检测。
    ///
    /// === 为什么不能直接用 Input.GetKeyDown ===
    ///
    /// 游戏里的每一次「按下」都会触发一个不可撤销的动作：切导航模式、
    /// 选中一个选项、退出游戏。而 Unity 的 legacy Input 在合成输入下
    /// （`keybd_event` / 自动化脚本 / 某些键盘宏和远程桌面）**有可能在
    /// 同一段按下状态里连着报两次 GetKeyDown**：动作于是被执行两遍。
    ///
    /// 开发期用脚本驱动游戏时就撞上过：发一次 Tab，日志里出现
    /// 「进入导航 → 退出导航 → 进入导航」三次迁移。
    ///
    /// === 闩锁的语义 ===
    ///
    ///   键处于抬起状态          → 清闩锁
    ///   GetKeyDown 且闩锁未置位  → 接受，并置位闩锁
    ///   GetKeyDown 且闩锁已置位  → 忽略
    ///
    /// 也就是说：**一次「按下 - 抬起」最多只算一次**。
    /// 真人连按两次一定伴随一次抬起，所以不会误伤；
    /// 而重复报的 GetKeyDown 中间没有抬起，正好被吃掉。
    ///
    /// === 组合键（0.2.2）===
    ///
    /// 本文件负责两类组合键：
    ///
    ///   1) 「修饰键 + 主键」（如 `Ctrl+Alt+G`）→ 走 <see cref="Hotkey"/>：它要求
    ///      「写了哪个修饰键就要求哪个按住、没写的必须没按住、且必须有一个主键」，
    ///      语义比单键严得多，不会在玩家按游戏自己的 `G` / `Alt+G` 时误触。
    ///   2) **纯修饰键组合**（如 `Ctrl+Alt`，没有主键）→ 走
    ///      <see cref="ModifierChordDown"/>：`Ctrl+Alt+G` 这类必须走第 1 类，
    ///      因为「Ctrl+Alt 同时按住」和「Ctrl+Alt+G」在看键状态时无法区分，
    ///      纯修饰键模式只能靠「按住时长 + 期间没有别的按键」来判定。
    ///
    /// 边沿闩锁：Hotkey.Down 只回答「这一帧是否按下」，调用方（逐作 Plugin）
    /// 自己维护闩锁；ModifierChordDown 自带闩锁（触发/作废后必须全部松开）。
    /// 平台层只通过 A11yHost.NavTogglePressed 这个只增不改的钩子接收
    /// 「这一帧要不要切换导航模式」，见 UiNav.Update。
    /// </summary>
    internal static class KeyEdge
    {
        private static readonly HashSet<KeyCode> Latched = new HashSet<KeyCode>();

        /// <summary>全局读取的「上一帧是否按住」表：GetAsyncKeyState 没有边沿，用它算按下沿。</summary>
        private static readonly HashSet<KeyCode> _prevGlobalHeld = new HashSet<KeyCode>();

        /// <summary>这一下按键是否算「新的一次按下」。</summary>
        public static bool Pressed(KeyCode k)
        {
            if (k == KeyCode.None) return false;

            // === 为什么整段 try/catch（0.2.2）===
            //
            // 工程如果把 Player Settings → Active Input Handling 设成
            // "Input System Package (New)" / "Both"，旧版 UnityEngine.Input
            // 的读取会在运行期抛 InvalidOperationException。
            // 平台层的按键读取全部经过这里（UiNav.Update 的 Tab / 回车 / 方向键），
            // 异常一旦冒出去就会一路冒到 Plugin.Update 的 catch —— 表现是
            // 「热键整段失效并每帧刷日志」。这里吞掉并当作「没按」，
            // 与 Hotkey.Down 的处理完全一致（见 Hotkey.cs 末尾的注释）。
            // === 本地读取（UnityEngine.Input）===
            bool held = false, down = false;
            try
            {
                held = Input.GetKey(k);
                down = Input.GetKeyDown(k);
            }
            catch
            {
                // Active Input Handling 设成 Input System Package 时旧 Input 会抛
                // InvalidOperationException。这里**不能直接 return** —— 逐作打开
                // 全局读取（A11yHost.UseGlobalKeys）时，全局那一路可能仍然可用。
                held = false;
                down = false;
            }

            // === 全局读取（0.2.3）：窗口拿不到焦点时 Input 恒为 false ===
            //
            // 并集语义：任一来源说「按住」就算按住，任一来源说「刚按下」就算按下。
            // 全局读取（GetAsyncKeyState）没有 down 位，按下沿用 _prevGlobalHeld 自己算
            // （held && !prev）。它和下面的「闩锁」是两层独立状态：
            // 前者防「全局读取没有边沿」，后者防「同一段按下被报多次」。
            if (GlobalKeys.Reading)
            {
                bool gHeld = GlobalKeys.Held(k);
                bool gDown = gHeld && !_prevGlobalHeld.Contains(k);

                if (gHeld) _prevGlobalHeld.Add(k);
                else _prevGlobalHeld.Remove(k);

                held |= gHeld;
                down |= gDown;
            }

            // 抬起就解锁。放在最前面：即使中途 GetKeyDown 报了多次，
            // 只要真实的抬起被观测到，下一次按下照样能认。
            if (!held)
            {
                Latched.Remove(k);
                return false;
            }

            if (!down) return false;
            if (Latched.Contains(k)) return false;

            Latched.Add(k);
            return true;
        }

        /// <summary>主键盘 / 小键盘上的数字键。</summary>
        /// <remarks>
        /// 注意坑：`Enum.TryParse("0")` 会**成功**并得到 KeyCode.None（数值 0），
        /// 而不是 Alpha0 —— 上一作 v0.5.7 踩过，所以这里显式拼名字。
        /// </remarks>
        public static bool DigitPressed(int n)
        {
            if (n < 0 || n > 9) return false;
            if (Pressed((KeyCode)System.Enum.Parse(typeof(KeyCode), "Alpha" + n))) return true;
            return Pressed((KeyCode)System.Enum.Parse(typeof(KeyCode), "Keypad" + n));
        }

        /// <summary>把配置里写的按键名解析成 KeyCode。认不出来返回 KeyCode.None。</summary>
        public static KeyCode Parse(string cfg)
        {
            if (string.IsNullOrEmpty(cfg)) return KeyCode.None;
            cfg = cfg.Trim();
            if (cfg.Length == 0) return KeyCode.None;

            // 单个数字必须自己映射，不能走 Enum.Parse("0") 那条路
            if (cfg.Length == 1 && cfg[0] >= '0' && cfg[0] <= '9')
            {
                try { return (KeyCode)System.Enum.Parse(typeof(KeyCode), "Alpha" + cfg); }
                catch { return KeyCode.None; }
            }

            try
            {
                KeyCode k = (KeyCode)System.Enum.Parse(typeof(KeyCode), cfg, true);
                return k;
            }
            catch { return KeyCode.None; }
        }

        // ====================================================================
        // 纯修饰键组合（0.2.2）：Ctrl+Alt / Ctrl+Shift / Alt / Win+Alt …
        // ====================================================================
        //
        // 为什么需要单独一条路：
        //   用户要求《不/存在的莉莉丝》的导航开关是**纯 Ctrl+Alt**（没有主键）。
        //   Hotkey.cs 的组合键语义要求「必须有主键」（"Ctrl+Alt" 会直接解析失败），
        //   所以纯修饰键组合只能另做一套判定：全部按住 ≥ minHoldSeconds，
        //   且按住期间没有按其它键，才算一次。
        //
        // 为什么不用 Input.anyKeyDown 直接判「其它键」：
        //   武装那一帧修饰键自己就是按下的，anyKeyDown 必然为 true。
        //   所以记录**武装帧**，只有之后（frameCount 更大）再次出现 anyKeyDown
        //   才当作「按了别的键」→ 本次作废（例如 Ctrl+Alt+X 不会切导航模式）。
        //   鼠标不算「其它键」：anyKeyDown 把鼠标按钮也算进来，这里要排除掉。
        //
        // 约束（与整个平台层一致）：
        //   · 只用 legacy UnityEngine.Input，整段 try/catch 吞异常
        //     （工程切到 Input System 时旧 Input 会抛 InvalidOperationException，
        //     绝不能让它冒进 Plugin.Update）；
        //   · 不引用 Input System / Il2CppInterop / 游戏类型；
        //   · 用 Time.unscaledTime（暂停 / 降速时仍然可靠）；
        //   · 每帧不分配：解析结果按 spec 缓存，状态是静态字段；
        //   · 同一时刻只支持一组 chord（平台层只需要导航开关这一个）。
        private const int ModGroupCtrl = 1;      // LeftControl / RightControl
        private const int ModGroupAlt = 2;       // LeftAlt / RightAlt
        private const int ModGroupShift = 4;     // LeftShift / RightShift
        private const int ModGroupWin = 8;       // LeftWindows / RightWindows

        private const int ChordIdle = 0;         // 没全按住，可以重新武装
        private const int ChordHolding = 1;      // 全按住，等 minHoldSeconds
        private const int ChordFired = 2;        // 已经触发过，等全部松开
        private const int ChordCancelled = 3;    // 本次作废，等全部松开

        private static int _chordPhase = ChordIdle;
        private static float _chordArmedAt;
        private static int _chordArmFrame = -1;
        private static int _chordMask;
        private static string _chordSpecCache;

        /// <summary>武装那一刻已经按住的非修饰键快照（全局模式下判「期间按了别的键」）。</summary>
        private static readonly ulong[] _chordOtherSnapshot = new ulong[GlobalKeys.SnapshotWords];

        /// <summary>
        /// 纯修饰键组合的「按下」沿：<paramref name="spec"/> 里列的组全部按住
        /// ≥ <paramref name="minHoldSeconds"/>、且按住期间没有按其它键，则返回 true
        /// **一次**；之后必须全部松开才能再次触发（自带闩锁）。
        /// </summary>
        /// <param name="spec">
        /// 只允许修饰键，`+` 或空格分隔，大小写不敏感。认
        /// Ctrl/Control、Alt、Shift、Win/Windows/Command/Cmd/Meta/Super。
        /// 组内左右键任意一个都算（Ctrl = LeftControl 或 RightControl）。
        /// 出现任何非修饰键 token → 返回 false（带主键的组合键请用
        /// <see cref="Hotkey.Down(string)"/>）。
        /// </param>
        /// <param name="minHoldSeconds">至少按住多久才算，默认 0.25 秒；负数/NaN 按 0 处理。</param>
        public static bool ModifierChordDown(string spec, float minHoldSeconds = 0.25f)
        {
            if (float.IsNaN(minHoldSeconds) || minHoldSeconds < 0f) minHoldSeconds = 0f;

            if (!TryParseModifierMask(spec))
            {
                ResetChordState();
                return false;
            }

            bool allHeld;
            try
            {
                allHeld = ChordHeld(_chordMask);
            }
            catch
            {
                // Input 不可用（Active Input Handling = Input System Package）：
                // 当作「没按」，并清状态，避免残留武装状态卡住。
                ResetChordState();
                return false;
            }

            if (_chordPhase == ChordFired || _chordPhase == ChordCancelled)
            {
                if (!allHeld) ResetChordState();
                return false;
            }

            if (!allHeld)
            {
                // 没全按住（还没武装 / 按住途中松了一个）：本次作废，回到空闲。
                ResetChordState();
                return false;
            }

            if (_chordPhase == ChordIdle)
            {
                // 刚刚全部按住：武装，从这一帧开始计时。
                _chordPhase = ChordHolding;
                _chordArmedAt = Time.unscaledTime;
                _chordArmFrame = Time.frameCount;
                // 全局模式下 Input.anyKeyDown 读不到（窗口没焦点），
                // 先把「武装时已经按着的其它键」记成快照，之后只认新按下的键。
                GlobalKeys.FillOtherKeySnapshot(_chordOtherSnapshot);
                return false;
            }

            // ---- ChordHolding ----
            // 「其它键」：跳过武装帧（修饰键自己那一帧 anyKeyDown 也为 true），
            // 之后任何新按键都算；鼠标不算。
            bool otherKey = false;
            if (Time.frameCount > _chordArmFrame)
            {
                try
                {
                    otherKey = Input.anyKeyDown
                               && !Input.GetMouseButtonDown(0)
                               && !Input.GetMouseButtonDown(1)
                               && !Input.GetMouseButtonDown(2);
                }
                catch { otherKey = false; }

                // 全局模式：Input.anyKeyDown 是焦点依赖的（本作窗口没焦点 = 永远 false），
                // 所以改成「和武装时的快照比，出现新按住的非修饰键」。
                if (!otherKey && GlobalKeys.Reading)
                {
                    otherKey = GlobalKeys.AnyNewOtherKeyHeld(_chordOtherSnapshot);
                }
            }

            if (otherKey)
            {
                _chordPhase = ChordCancelled;
                return false;
            }

            if (Time.unscaledTime - _chordArmedAt < minHoldSeconds) return false;

            _chordPhase = ChordFired;
            return true;
        }

        /// <summary>清掉 chord 状态机（场景切换 / 失焦 / 触发或作废之后）。</summary>
        private static void ResetChordState()
        {
            _chordPhase = ChordIdle;
            _chordArmedAt = 0f;
            _chordArmFrame = -1;
        }

        /// <summary>解析 spec 成修饰键组掩码；同一个 spec 走缓存（每帧不分配）。</summary>
        private static bool TryParseModifierMask(string spec)
        {
            if (string.IsNullOrEmpty(spec)) { _chordSpecCache = null; _chordMask = 0; return false; }

            if (string.Equals(spec, _chordSpecCache, StringComparison.Ordinal)) return _chordMask != 0;

            int mask = 0;
            int i = 0;
            int n = spec.Length;
            while (i < n)
            {
                while (i < n && (spec[i] == '+' || char.IsWhiteSpace(spec[i]))) i++;
                if (i >= n) break;
                int start = i;
                while (i < n && spec[i] != '+' && !char.IsWhiteSpace(spec[i])) i++;
                int len = i - start;

                if (TokenIs(spec, start, len, "ctrl") || TokenIs(spec, start, len, "control"))
                    mask |= ModGroupCtrl;
                else if (TokenIs(spec, start, len, "alt"))
                    mask |= ModGroupAlt;
                else if (TokenIs(spec, start, len, "shift"))
                    mask |= ModGroupShift;
                else if (TokenIs(spec, start, len, "win") || TokenIs(spec, start, len, "windows")
                      || TokenIs(spec, start, len, "command") || TokenIs(spec, start, len, "cmd")
                      || TokenIs(spec, start, len, "meta") || TokenIs(spec, start, len, "super"))
                    mask |= ModGroupWin;
                else
                {
                    // 非修饰键：不是本 API 的输入（带主键的组合键请用 Hotkey）。
                    _chordSpecCache = null;
                    _chordMask = 0;
                    return false;
                }
            }

            if (mask == 0) { _chordSpecCache = null; _chordMask = 0; return false; }

            _chordSpecCache = spec;
            _chordMask = mask;
            return true;
        }

        /// <summary>大小写不敏感地比较 spec 里 [start, start+length) 与 expected（expected 必须小写）。</summary>
        private static bool TokenIs(string spec, int start, int length, string expected)
        {
            if (length != expected.Length) return false;
            for (int i = 0; i < length; i++)
            {
                char c = spec[start + i];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (c != expected[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 掩码里的组是否全部按住（组内左右键任意一个都算）。
        /// 「按住」= 本地 Input 或全局读取（逐作打开 A11yHost.UseGlobalKeys 时）任一说按住。
        /// </summary>
        private static bool ChordHeld(int mask)
        {
            if ((mask & ModGroupCtrl) != 0
                && !HeldAny(KeyCode.LeftControl) && !HeldAny(KeyCode.RightControl)) return false;
            if ((mask & ModGroupAlt) != 0
                && !HeldAny(KeyCode.LeftAlt) && !HeldAny(KeyCode.RightAlt)) return false;
            if ((mask & ModGroupShift) != 0
                && !HeldAny(KeyCode.LeftShift) && !HeldAny(KeyCode.RightShift)) return false;
            if ((mask & ModGroupWin) != 0
                && !HeldAny(KeyCode.LeftWindows) && !HeldAny(KeyCode.RightWindows)) return false;
            return true;
        }

        /// <summary>
        /// 这个键现在是否按住：本地 Input 或全局读取任一为真。
        /// 全局那一路在窗口没焦点时是唯一有效的来源（0.2.3）。
        /// </summary>
        private static bool HeldAny(KeyCode k)
        {
            if (GlobalKeys.Reading && GlobalKeys.Held(k)) return true;
            try { return Input.GetKey(k); }
            catch { return false; }
        }

        /// <summary>场景切换时清干净，避免跨场景残留闩锁。</summary>
        public static void Reset()
        {
            Latched.Clear();
            _prevGlobalHeld.Clear();
            GlobalKeys.ResetPressed();
            ResetChordState();
            _chordSpecCache = null;
            _chordMask = 0;
        }
    }
}
