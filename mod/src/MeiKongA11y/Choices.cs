using System;
using System.Collections.Generic;
using UnityEngine;

namespace MeiKongA11y
{
    /// <summary>
    /// 选项：抓取 → 播报 → 数字键选择。
    ///
    /// === 本作的选项通路（P1 实机已验证，见 P0 §4.3 E4）===
    ///
    ///   NotifyChoiceWaiting(first, count)                 ← 只有数量，还没有文本
    ///   DialogueChoiceItemView.Bind(label, onChosen)      ← 每个选项调一次，拿到标签 + 回调
    ///   DialogueChoiceItemView.SetInteractable(true)      ← **每个选项各调一次**（全部滑入完成后）
    ///
    /// ★ 两个必须处理的细节（都是实机踩出来的）：
    ///   1) `SetInteractable(true)` 每个选项各调一次 → **必须去重**，否则选项会被播报 N 遍；
    ///   2) 数字键「直接调用 Bind 收到的 Action」**等价于点按钮** —— 这一条已经在实机上验证过
    ///      （探针自动选第 1 项后，剧情确实跳到了该选项的 ToNumber 目标）。
    ///
    /// 纪律（流水线 §4「不许扩大可达范围」）：
    ///   · 选项还没全部滑入（interactable=false）时**不接受**数字键 —— 那是游戏的节奏；
    ///   · 不预选、不跳过、不替玩家做决定；
    ///   · 选项文本只念一次，念完把编号留在那里，玩家按数字即可。
    /// </summary>
    internal static class Choices
    {
        private static readonly List<string> _labels = new List<string>();
        private static readonly List<Action> _callbacks = new List<Action>();
        private static bool _announced;
        private static bool _ready;
        private static int _announcedCount;

        /// <summary>选项块开始（NotifyChoiceWaiting）。</summary>
        internal static void Begin(int count)
        {
            _labels.Clear();
            _callbacks.Clear();
            _announced = false;
            _ready = false;
            _announcedCount = 0;
            A11yHost.Diag("[选项] 开始，预计 " + count + " 项");
        }

        /// <summary>收集一个选项（Bind）。</summary>
        internal static void Add(string label, Action onChosen)
        {
            string clean = TextProc.Clean(label);
            _labels.Add(clean);
            _callbacks.Add(onChosen);
            A11yHost.Diag("[选项] #" + _labels.Count + " 长度=" + clean.Length);
        }

        /// <summary>全部滑入完成（SetInteractable(true)，每个选项各一次 → 这里去重）。</summary>
        internal static void Ready()
        {
            _ready = true;
            if (_announced) return;
            if (_labels.Count == 0) return;
            _announced = true;
            _announcedCount = _labels.Count;

            if (Plugin.CfgReadChoices != null && !Plugin.CfgReadChoices.Value) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("有 ").Append(_labels.Count).Append(" 个选项：");
            for (int i = 0; i < _labels.Count; i++)
            {
                if (i > 0) sb.Append('；');
                sb.Append(i + 1).Append('、').Append(_labels[i]);
            }
            if (Plugin.CfgChoiceHotkeys == null || Plugin.CfgChoiceHotkeys.Value)
                sb.Append("。按数字键选择。");
            Speech.Speak(sb.ToString(), true);
        }

        /// <summary>选项被清掉（段落结束 / 已选择）。</summary>
        internal static void Clear()
        {
            _labels.Clear();
            _callbacks.Clear();
            _announced = false;
            _ready = false;
            _announcedCount = 0;
        }

        internal static bool HasPending
        {
            get { return _ready && _labels.Count > 0 && _announcedCount > 0; }
        }

        /// <summary>每帧调一次：处理数字键。</summary>
        internal static void Update()
        {
            if (Plugin.CfgChoiceHotkeys == null || !Plugin.CfgChoiceHotkeys.Value) return;
            if (!HasPending) return;
            int pick = ReadDigit();
            if (pick <= 0) return;
            Choose(pick);
        }

        /// <summary>按 1–9 选第 N 项。选项已按顺序收集，回调就是「把 pickedJumpIndex 赋值」的闭包。</summary>
        internal static void Choose(int oneBased)
        {
            int idx = oneBased - 1;
            if (idx < 0 || idx >= _callbacks.Count)
            {
                Speech.Speak("没有第 " + oneBased + " 个选项。", true);
                return;
            }
            string label = idx < _labels.Count ? _labels[idx] : "";
            Speech.Speak("已选择：" + label, true);
            Action cb = _callbacks[idx];
            Clear();                       // 先清，避免回调触发的下一段被当成重复
            try { if (cb != null) cb(); }
            catch (Exception e) { A11yHost.Diag("[选项] 回调异常: " + e.Message); }
        }

        /// <summary>
        /// 读一个数字键。**不能用 Enum.TryParse**（流水线 G3）：`Enum.TryParse("0")` 会成功
        /// 但得到 `KeyCode.None`（数值 0），不是 Alpha0。这里手工映射。
        /// </summary>
        internal static int ReadDigit()
        {
            for (int i = 1; i <= 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i)) return i;
                if (Input.GetKeyDown(KeyCode.Keypad0 + i)) return i;
            }
            return 0;
        }
    }
}
