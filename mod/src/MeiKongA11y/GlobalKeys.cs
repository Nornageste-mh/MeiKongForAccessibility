using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MeiKongA11y
{
    // ========================================================================
    // A11yFramework · L1 平台层（0.2.3 只增不改）
    //
    // === 为什么需要这个文件 ===
    //
    // 《不/存在的莉莉丝》的 Unity 窗口**永远拿不到键盘焦点**：hwnd class =
    // UnityWndClass，exstyle = 0x080800B0 =
    //   WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST |
    //   WS_EX_TRANSPARENT
    // 「单击穿透」模式（默认）下它不能激活；实机连试三次
    // SetForegroundWindow / AttachThreadInput / SwitchToThisWindow 都拿不到焦点
    // （前台一直是 Program Manager / 浏览器）。于是 UnityEngine.Input 读不到任何键：
    // 导航开关、方向键、回车、数字键**全部静默失效** —— 不是绑定问题，是焦点依赖。
    //
    // 方案：Win32 `GetAsyncKeyState` **轮询**当前按键状态。
    //   · 不装全局键盘钩子（SetWindowsHookEx）、不做 DLL 注入、不碰游戏线程；
    //   · 不依赖窗口焦点，前台是任何程序都能读到；
    //   · 代价：只能问「现在是否按住」，没有「按下沿」——边沿必须由调用方
    //     用静态表自己算（见 Pressed / KeyEdge.Pressed 与 Hotkey.Down）。
    //
    // === 纪律（与其它 L1 文件一致） ===
    //
    //   · 只引用 UnityEngine（KeyCode）与 System.Runtime.InteropServices；
    //   · 不引用任何游戏类型 / Il2CppInterop / Il2CppSystem；
    //   · 非 Windows 平台 / 没有 user32.dll 时 Available 为 false，调用方自动退回
    //     UnityEngine.Input —— 不会有异常冒出主循环，也不写日志刷屏；
    //   · 由契约层 A11yHost.UseGlobalKeys 打开（默认 false），逐作自己填 true。
    //
    // === 安全边界 ===
    //
    //   GetAsyncKeyState 读的是**整机**按键状态，与我们是否前台无关。
    //   因此打开后，玩家在别的程序里按导航键也会触发导航开关 ——
    //   这正是用户要的「全程焦点也能用」，但它确实越过了「只看本游戏输入」的边界，
    //   所以这个开关必须逐作显式打开，不能做成平台默认。
    // ========================================================================
    internal static class GlobalKeys
    {
        /// <summary>
        /// Win32 全局按键状态。返回值的 bit 15 = 当前是否按住（其余位不要依赖：
        /// bit 0 =「上次调用以来被按过」，会被系统里任何调用者消费掉，不可靠）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = false)]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>VK_SHIFT，仅用于首次试探 user32 是否可用。</summary>
        private const int VkShift = 0x10;

        // ====================================================================
        // 可用性探测（一次性）
        // ====================================================================

        private static bool _probed;
        private static bool _available;

        /// <summary>
        /// 首次试探后固定：非 Windows（DllNotFoundException）或入口缺失
        /// （EntryPointNotFoundException）→ false，**且永不重试**（避免每帧一次异常）。
        /// </summary>
        internal static bool Available
        {
            get
            {
                if (_probed) return _available;
                _probed = true;
                try
                {
                    GetAsyncKeyState(VkShift);
                    _available = true;
                }
                catch (DllNotFoundException) { _available = false; }
                catch (EntryPointNotFoundException) { _available = false; }
                catch (Exception) { _available = false; }
                return _available;
            }
        }

        // ====================================================================
        // 总开关（唯一入口是契约层 A11yHost.UseGlobalKeys）
        // ====================================================================

        // 这里**不**由契约层 setter 写入：那是「契约层引用平台层」，会把分层倒过来
        // （README「架构」一节：platform 依赖 contract，不许反向）。
        // 改成平台层每帧懒同步一次：值没变直接返回，所以代价是一次 bool 比较。
        private static bool _enabled;
        private static bool _synced;

        /// <summary>现在是否真的在读全局键（开关打开 **且** 平台可用 **且** 焦点门允许）。</summary>
        internal static bool Reading
        {
            get
            {
                SyncWithHostSwitch();
                if (!(_enabled && Available)) return false;

                // === 0.2.5 焦点门 ===
                //
                // GetAsyncKeyState 是整机范围的：不关心前台是哪个窗口。第五作
                // 《不/存在的莉莉丝》必须靠全局读取才能用（窗口拿不到焦点），
                // 但副作用是「在别的程序里打字也会触发本补丁的热键」。
                // 逐作层已经有各自的 FocusGate / 输入框判定，五次实机后提成契约钩子
                // A11yHost.KeyInputAllowed：平台层只问「现在能不能读」，条件由逐作给。
                //
                // 门关掉时清掉边沿表：否则重新开门那一帧会把「关门期间的按住」
                // 当成一次新的按下沿（少了抬起事件），多触发一次动作。
                bool allowed = true;
                try
                {
                    Func<bool> gate = A11yHost.KeyInputAllowed;
                    if (gate != null) allowed = gate();
                }
                catch
                {
                    // 钩子是「减少误触」的优化，不是安全边界：异常时 fail-open，
                    // 宁可误触，也不要把整段热键静默关掉。
                    allowed = true;
                }

                if (!allowed)
                {
                    _prevHeld.Clear();
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// 把 <c>A11yHost.UseGlobalKeys</c> 同步到平台层，并接上/摘掉 Hotkey 的全局委托。
        ///
        /// 为什么要在这里做而不是在 A11yHost 的 setter 里：
        ///   1) 分层方向固定为 platform → contract，反向引用会在 review 里被打回；
        ///   2) Hotkey.cs 必须能**单文件**编译（tools/zdsr_selftest 只 Include
        ///      Zdsr.cs + Hotkey.cs），所以它只认委托，接线只能由平台层完成。
        /// </summary>
        private static void SyncWithHostSwitch()
        {
            bool want;
            try { want = A11yHost.UseGlobalKeys; }
            catch { want = false; }

            if (_synced && want == _enabled) return;
            _synced = true;
            _enabled = want;

            if (want)
            {
                Hotkey.GlobalHeld = Held;
                Hotkey.GlobalPressed = Pressed;
                try { A11yHost.Diag("[A11yHost] 全局按键读取已打开（user32=" + Available + "）"); }
                catch { }
            }
            else
            {
                Hotkey.GlobalHeld = null;
                Hotkey.GlobalPressed = null;
                _prevHeld.Clear();
            }
        }

        // ====================================================================
        // KeyCode → 虚拟键码（VK）
        // ====================================================================
        //
        // 只映射平台层/UiNav 实际会读的键。没映射的 KeyCode 一律返回 false
        // （宁可读不到，也不要猜错键码误触发别的动作）。
        //
        // 注意主键盘与符号键用的是美国布局的 OEM 虚拟键码：
        //   Equals = VK_OEM_PLUS(0xBB) —— 「主键盘加号」就是 Shift+Equals；
        //   Minus  = VK_OEM_MINUS(0xBD)；
        //   KeypadPlus = VK_ADD(0x6B) —— 小键盘加号，和主键盘加号是两个键。
        // 用户明确要求导航开关键是「仅限大键盘的加号」，所以这三个键都必须能读
        // （Equals / Minus / KeypadPlus）才有办法验证「小键盘加号不触发」。
        // ====================================================================

        private static Dictionary<KeyCode, int> _map;

        private static Dictionary<KeyCode, int> Map
        {
            get
            {
                if (_map == null) _map = BuildMap();
                return _map;
            }
        }

        private static Dictionary<KeyCode, int> BuildMap()
        {
            var m = new Dictionary<KeyCode, int>(160);

            // 控制键
            m[KeyCode.Tab] = 0x09;
            m[KeyCode.Return] = 0x0D;
            m[KeyCode.KeypadEnter] = 0x0D;
            m[KeyCode.Space] = 0x20;
            m[KeyCode.Escape] = 0x1B;
            m[KeyCode.Backspace] = 0x08;
            m[KeyCode.Insert] = 0x2D;
            m[KeyCode.Delete] = 0x2E;

            // 方向键 / 翻页 / 首尾
            m[KeyCode.LeftArrow] = 0x25;
            m[KeyCode.UpArrow] = 0x26;
            m[KeyCode.RightArrow] = 0x27;
            m[KeyCode.DownArrow] = 0x28;
            m[KeyCode.PageUp] = 0x21;
            m[KeyCode.PageDown] = 0x22;
            m[KeyCode.Home] = 0x24;
            m[KeyCode.End] = 0x23;

            // 主键盘字母 / 数字
            for (int i = 0; i < 26; i++) m[(KeyCode)((int)KeyCode.A + i)] = 0x41 + i;
            for (int i = 0; i <= 9; i++) m[(KeyCode)((int)KeyCode.Alpha0 + i)] = 0x30 + i;

            // 小键盘数字 / 符号
            for (int i = 0; i <= 9; i++) m[(KeyCode)((int)KeyCode.Keypad0 + i)] = 0x60 + i;
            m[KeyCode.KeypadMultiply] = 0x6A;
            m[KeyCode.KeypadPlus] = 0x6B;
            m[KeyCode.KeypadMinus] = 0x6D;
            m[KeyCode.KeypadPeriod] = 0x6E;
            m[KeyCode.KeypadDivide] = 0x6F;

            // 主键盘符号（美式布局 OEM 键码）
            m[KeyCode.Equals] = 0xBB;
            m[KeyCode.Minus] = 0xBD;
            m[KeyCode.Comma] = 0xBC;
            m[KeyCode.Period] = 0xBE;
            m[KeyCode.Slash] = 0xBF;
            m[KeyCode.Semicolon] = 0xBA;
            m[KeyCode.Quote] = 0xDE;
            m[KeyCode.BackQuote] = 0xC0;
            m[KeyCode.LeftBracket] = 0xDB;
            m[KeyCode.RightBracket] = 0xDD;
            m[KeyCode.Backslash] = 0xDC;

            // 修饰键（左右分开：Hotkey / chord 都要按「任意一侧」判断）
            m[KeyCode.LeftShift] = 0xA0;
            m[KeyCode.RightShift] = 0xA1;
            m[KeyCode.LeftControl] = 0xA2;
            m[KeyCode.RightControl] = 0xA3;
            m[KeyCode.LeftAlt] = 0xA4;
            m[KeyCode.RightAlt] = 0xA5;
            m[KeyCode.LeftWindows] = 0x5B;
            m[KeyCode.RightWindows] = 0x5C;

            // 功能键
            for (int i = 1; i <= 12; i++) m[(KeyCode)((int)KeyCode.F1 + i - 1)] = 0x6F + i;

            return m;
        }

        // ====================================================================
        // 读取
        // ====================================================================

        /// <summary>这个键当前是否按住（未开开关 / 平台不可用 / 未映射 → false）。</summary>
        internal static bool Held(KeyCode k)
        {
            if (k == KeyCode.None || !Reading) return false;
            int vk;
            if (!Map.TryGetValue(k, out vk)) return false;
            return HeldRaw(vk);
        }

        private static bool HeldRaw(int vk)
        {
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        // ====================================================================
        // 边沿：GetAsyncKeyState 只给「是否按住」，按下沿要自己记
        // ====================================================================
        //
        // 语义与 KeyEdge 的闩锁一致：按住期间只算一次，观测到抬起才解锁。
        // （Hotkey 原来用 Input.GetKeyDown；全局模式下改用这里的表，因为
        //   全局读取本来就没有 down 位，而且这样不会被合成输入连报两次。）
        private static readonly HashSet<KeyCode> _prevHeld = new HashSet<KeyCode>();

        /// <summary>一次「按下」沿：当前按住、且上一次没按住。</summary>
        internal static bool Pressed(KeyCode k)
        {
            if (k == KeyCode.None || !Reading) return false;
            bool held = Held(k);
            if (!held)
            {
                _prevHeld.Remove(k);
                return false;
            }
            if (_prevHeld.Contains(k)) return false;
            _prevHeld.Add(k);
            return true;
        }

        /// <summary>清掉边沿表（场景切换 / 失焦）。挂起中的按键不会被当成「刚按下」。</summary>
        internal static void ResetPressed()
        {
            _prevHeld.Clear();
        }

        // ====================================================================
        // 给 ModifierChordDown 用：「按住期间有没有按别的键」
        // ====================================================================
        //
        // 全局模式下 Input.anyKeyDown 也是焦点依赖的（窗口没焦点 = 永远 false），
        // 所以 chord 武装时先把「当时已经按住的非修饰键」记成快照，
        // 之后发现快照外的新按键才算「期间按了别的键」——
        // 语义仍是「按下」，不会因为玩家一直按着 W 走路就永远开不了导航。

        /// <summary>快照需要的 ulong 数（VK 0..255）。</summary>
        internal const int SnapshotWords = 4;

        /// <summary>把当前按住的非修饰键记进快照（buffer 长度不足 SnapshotWords 时安全空转）。</summary>
        internal static void FillOtherKeySnapshot(ulong[] buffer)
        {
            if (buffer == null) return;
            int words = buffer.Length < SnapshotWords ? buffer.Length : SnapshotWords;
            for (int i = 0; i < words; i++) buffer[i] = 0UL;
            if (!Reading) return;

            foreach (KeyValuePair<KeyCode, int> kv in Map)
            {
                if (IsModifierKey(kv.Key)) continue;
                if (!HeldRaw(kv.Value)) continue;
                SetBit(buffer, kv.Value);
            }
        }

        /// <summary>快照之后是否又按住了别的键（鼠标按钮不在本表里，天然不算）。</summary>
        internal static bool AnyNewOtherKeyHeld(ulong[] snapshot)
        {
            if (snapshot == null || !Reading) return false;

            foreach (KeyValuePair<KeyCode, int> kv in Map)
            {
                if (IsModifierKey(kv.Key)) continue;
                int vk = kv.Value;
                if (GetBit(snapshot, vk)) continue;   // 武装时就按着的不算
                if (!HeldRaw(vk)) continue;
                return true;
            }
            return false;
        }

        private static bool IsModifierKey(KeyCode k)
        {
            return k == KeyCode.LeftShift || k == KeyCode.RightShift
                || k == KeyCode.LeftControl || k == KeyCode.RightControl
                || k == KeyCode.LeftAlt || k == KeyCode.RightAlt
                || k == KeyCode.LeftWindows || k == KeyCode.RightWindows;
        }

        private static void SetBit(ulong[] buffer, int vk)
        {
            if (vk < 0 || vk >= 256) return;
            buffer[vk >> 6] |= 1UL << (vk & 63);
        }

        private static bool GetBit(ulong[] buffer, int vk)
        {
            if (vk < 0 || vk >= 256) return false;
            return (buffer[vk >> 6] & (1UL << (vk & 63))) != 0UL;
        }
    }
}
