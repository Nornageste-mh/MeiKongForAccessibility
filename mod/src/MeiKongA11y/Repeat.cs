using System;
using System.Collections.Generic;

namespace MeiKongA11y
{
    /// <summary>
    /// 重读缓冲区 —— L1 平台层里**唯一**允许更新「重读内容」的地方。
    ///
    /// === 为什么要把这件事提到平台层：同一个坑踩了两次 ===
    ///
    /// 《钟塔》0.1.0.1 的复盘原文：
    ///
    /// &gt; 根因是「重读缓冲区」的覆盖面不全 —— 上一作把所有朗读都收进唯一出口，
    /// &gt; 这一作有地方绕过了它……**选项播报绕过了缓冲区**，
    /// &gt; 所以停在选项上按退格什么也念不出来。
    ///
    /// 《妹控计划》0.1.0.0 又踩了**同一个坑**（发布当天被玩家抓出来、当天撤回重发）。
    ///
    /// 两次的根因是同一个：**缓冲区是逐作各写一份的**。
    /// 而「重读」的规则 —— 哪些内容该进、什么时候失效、重读路径能不能被去重吃掉 ——
    /// **与具体游戏无关**，本来就该是平台层的事。
    /// 所以 0.1.1.0 把它提到 L1：逐作层**只能往里塞，绕不过去**，
    /// 由 `tools/lint_repeat.ps1` 的静态断言机械保证（逐作层不许直接出现 `Speech.Speak(`）。
    ///
    /// === 三条规则（照抄两次复盘）===
    ///
    ///   1. **凡是玩家可能想重听的东西，都要进缓冲区** —— 包括**有配音的行**。
    ///      补丁只是不自动念它，玩家主动按重读键仍然要能听到（这是它唯一的朗读通路）。
    ///   2. **临时内容要能失效** —— 选项被清掉之后不能再念那几个已经不存在的选项。
    ///      所以用 `Push` / `Drop` 成对出现，而不是覆盖主缓冲区。
    ///   3. **重读是玩家的主动请求** —— 直接走 Speech，
    ///      **不经过任何去重与排队**，否则「刚念完马上再听一遍」这个正当操作会被吃掉。
    /// </summary>
    internal static class Repeat
    {
        private const int MaxHistory = 4;

        private static readonly List<string> _history = new List<string>();
        private static string _priority = "";
        private static string _priorityTag = "";

        /// <summary>当前按重读键会念出来的内容（诊断与测试用）。</summary>
        internal static string Current
        {
            get
            {
                if (_priority.Length > 0) return _priority;
                return _history.Count > 0 ? _history[_history.Count - 1] : "";
            }
        }

        /// <summary>缓冲区里有没有临时优先项（选项那类）。</summary>
        internal static bool HasPriority { get { return _priority.Length > 0; } }

        /// <summary>
        /// 播报 + 进缓冲区。**逐作层的朗读都该走这里** ——
        /// 直接调 `Speech.Speak` 就是那个踩了两次的坑。
        /// </summary>
        internal static void Say(string text, bool interrupt = true)
        {
            Note(text);
            Speech.Speak(text, interrupt);
        }

        /// <summary>
        /// 只进缓冲区、不播报（「只记不念」）：
        /// 有配音的行、快进经过的行 —— 它们不自动朗读，但必须能被重读键翻回来。
        /// </summary>
        internal static void Note(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_history.Count > 0 && _history[_history.Count - 1] == text) return;
            _history.Add(text);
            while (_history.Count > MaxHistory) _history.RemoveAt(0);
        }

        /// <summary>
        /// 临时优先项（选项列表这类）：**进缓冲区、且在重读时优先于台词**（不播报）。
        /// **必须与 <see cref="Drop"/> 成对出现。**
        /// </summary>
        internal static void Push(string text, string tag)
        {
            if (string.IsNullOrEmpty(text)) return;
            _priority = text;
            _priorityTag = tag ?? "";
            Note(text);
            // **不播报** —— 播报由调用方决定（要不要走播报队列、要不要等配音放完，
            // 那是逐作的策略，缓冲区不替它决定）。
        }

        /// <summary>
        /// 撤销临时优先项（选完了 / 段落结束了），缓冲区**退回台词**。
        /// tag 不匹配就不动 —— 防止晚到的 Drop 把新推上来的那一项顶掉。
        /// </summary>
        internal static void Drop(string tag)
        {
            if (_priority.Length == 0) return;
            if (!string.IsNullOrEmpty(tag) && _priorityTag != tag) return;
            _priority = "";
            _priorityTag = "";
        }

        /// <summary>重读。空缓冲区时**如实说明**，不静默吞掉。</summary>
        internal static bool Again()
        {
            string text = Current;
            if (string.IsNullOrEmpty(text))
            {
                Speech.Speak("还没有可以重读的内容。", true);
                return false;
            }
            Speech.Speak(text, true);
            return true;
        }

        internal static void Clear()
        {
            _history.Clear();
            _priority = "";
            _priorityTag = "";
        }
    }
}
