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
    /// </summary>
    internal static class KeyEdge
    {
        private static readonly HashSet<KeyCode> Latched = new HashSet<KeyCode>();

        /// <summary>这一下按键是否算「新的一次按下」。</summary>
        public static bool Pressed(KeyCode k)
        {
            if (k == KeyCode.None) return false;

            // 抬起就解锁。放在最前面：即使中途 GetKeyDown 报了多次，
            // 只要真实的抬起被观测到，下一次按下照样能认。
            if (!Input.GetKey(k))
            {
                Latched.Remove(k);
                return false;
            }

            if (!Input.GetKeyDown(k)) return false;
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

        /// <summary>场景切换时清干净，避免跨场景残留闩锁。</summary>
        public static void Reset()
        {
            Latched.Clear();
        }
    }
}
