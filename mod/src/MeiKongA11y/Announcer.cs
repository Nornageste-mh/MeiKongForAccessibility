using System;
using System.Collections.Generic;
using ShiMeng.DialogueV2;

namespace MeiKongA11y
{
    /// <summary>
    /// 播报调度：**别跟诗萌抢话**。
    ///
    /// === 这条纪律是被玩家当场抓出来的 ===
    ///
    /// 补丁的既定策略是「有配音的台词不自动念」，理由就是**不能和配音叠读**。
    /// 但我自己的系统播报（小游戏开局、面板打开、切模式）却是无条件念的 ——
    /// 小游戏开局时游戏正在播 `game_记忆翻牌_开局前` 的配音，
    /// 结果 NVDA 压着诗萌的声音说「记忆翻牌开始。4 行 4 列……」。玩家原话：「有一点小瑕疵」。
    ///
    /// 所以补丁自己也得守同一条规矩：**配音在放的时候，自动播报排队等它放完。**
    ///
    /// === 两类播报，两套规则 ===
    ///
    ///   `Now()`  —— **玩家按键触发**的反馈（F2 状态、选项念读、光标移动、滑条值）。
    ///               他在等答案，排队等于没反应，所以立刻说。
    ///   `Auto()` —— **系统自己触发**的播报（面板开关、进入桌宠模式、小游戏开局、选项出现）。
    ///               没人催，等配音放完再说，信息一点不少。
    ///
    /// 队列有上限（默认 3 条），满了丢**最旧**的：过期的状态播报比没有更糟 ——
    /// 「已打开：番茄钟」等到三个面板之后才念出来，只会让人更迷糊。
    /// </summary>
    internal static class Announcer
    {
        private const int MaxPending = 3;

        private sealed class Item
        {
            internal string Text;
            internal float Deadline;   // <=0 表示不设上限（可以一直等）
        }

        private static readonly List<Item> _pending = new List<Item>();
        private static DialogueV2Runner _runner;
        private static float _nextTryAt;

        /// <summary>配音通道忙不忙。用**游戏自己的** `DialogueV2Runner.IsAnyVoicePlaying()`（public）。</summary>
        internal static bool VoiceBusy()
        {
            try
            {
                if (_runner == null) _runner = UnityEngine.Object.FindObjectOfType<DialogueV2Runner>(true);
                if (_runner != null && _runner.IsAnyVoicePlaying()) return true;
            }
            catch { }
            try
            {
                var p = AudiosPlayer.Instance;
                if (p != null && p.CVSource != null && p.CVSource.isPlaying) return true;
            }
            catch { }
            return false;
        }

        private static bool WaitEnabled
        {
            get { return Plugin.CfgQueueBehindVoice == null || Plugin.CfgQueueBehindVoice.Value; }
        }

        /// <summary>系统自动播报：配音在放就排队。</summary>
        internal static void Auto(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (!WaitEnabled || !VoiceBusy()) { Repeat.Say(text, true); return; }
                if (_pending.Count > 0 && _pending[_pending.Count - 1].Text == text) return;  // 相邻重复丢掉
                while (_pending.Count >= MaxPending) _pending.RemoveAt(0);                    // 满了丢最旧
                _pending.Add(new Item { Text = text, Deadline = 0f });
                A11yHost.Diag("[播报] 配音中，排队等它放完: " + Head(text));
            }
            catch (Exception e) { A11yHost.Diag("[播报] Auto 异常: " + e.Message); }
        }

        /// <summary>
        /// **时间敏感**的状态播报（小游戏的翻牌结果、诗萌翻了什么、配对成败）。
        ///
        /// 与 `Auto()` 的区别只有一个：**等配音最多等 maxWait 秒**。
        /// 理由：这些信息过几秒就没用了 —— 一句 7 秒的配音把「诗萌翻开第 2 行第 3 列：麦克风」
        /// 拖到放完才念，玩家早就在等下一步了。宁可轻微重叠，也不能迟到。
        /// </summary>
        internal static void Soon(string text, float maxWait = 2.5f)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                if (!WaitEnabled || !VoiceBusy()) { Repeat.Say(text, true); return; }
                if (_pending.Count > 0 && _pending[_pending.Count - 1].Text == text) return;
                while (_pending.Count >= MaxPending) _pending.RemoveAt(0);
                _pending.Add(new Item { Text = text, Deadline = UnityEngine.Time.realtimeSinceStartup + maxWait });
                A11yHost.Diag("[播报] 配音中，限时排队(" + maxWait + "s): " + Head(text));
            }
            catch (Exception e) { A11yHost.Diag("[播报] Soon 异常: " + e.Message); }
        }

        private static string Head(string s)
        {
            return s.Length <= 20 ? s : s.Substring(0, 20) + "…";
        }

        /// <summary>玩家按键触发的即时反馈：不排队。</summary>
        internal static void Now(string text, bool interrupt = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Repeat.Say(text, interrupt); }
            catch (Exception e) { A11yHost.Diag("[播报] Now 异常: " + e.Message); }
        }

        /// <summary>每帧调一次：配音一停就把排队的那条说出来。</summary>
        internal static void Tick()
        {
            if (_pending.Count == 0) return;
            try
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now < _nextTryAt) return;
                _nextTryAt = now + 0.2f;

                Item first = _pending[0];
                bool overdue = first.Deadline > 0f && now >= first.Deadline;   // 等太久了，不等了
                if (VoiceBusy() && !overdue) return;

                _pending.RemoveAt(0);
                A11yHost.Diag(overdue ? "[播报] 限时已到，抢报: " + Head(first.Text)
                                      : "[播报] 配音结束，补报: " + Head(first.Text));
                Repeat.Say(first.Text, true);
            }
            catch (Exception e) { A11yHost.Diag("[播报] Tick 异常: " + e.Message); }
        }

        internal static void Clear()
        {
            _pending.Clear();
        }
    }
}
