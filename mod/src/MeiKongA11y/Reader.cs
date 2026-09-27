using System;
using ShiMeng.DialogueV2;

namespace MeiKongA11y
{
    /// <summary>
    /// 朗读中枢：决定「哪一行、念不念、念什么」。
    ///
    /// === 本作的朗读策略（与前三作**不同**，依据是 P0 §3.2 的实查数字）===
    ///
    /// 全作 1392 行台词里 **1353 行（97.2%）有配音**，而 belfry 只有 39.1%。
    /// 所以照抄「只朗读无配音行」在这里几乎等于「什么都不念」。
    /// 本作的分工是：
    ///
    ///   有配音的行  → **默认不自动念**（配音本身就是内容；叠读会让玩家两边都听不清）
    ///                 但**记下来**，玩家可以按「重读键」随时重念 —— 这是这类行唯一的朗读通路
    ///   无配音的行  → 自动念（默认开）
    ///
    /// 玩家如果确实想让读屏把每一句都念一遍，把配置项「有配音的行也自动朗读」打开即可。
    ///
    /// === 去重 ===
    /// 实机发现 `NotifyLineChanged` 对同一行会被调用**两次**（探针日志里 idx=4 / idx=10 各出现两遍），
    /// 所以必须按 (ScenarioId, Index) 去重，否则每一句都会被念两遍。
    /// </summary>
    internal static class Reader
    {
        internal static string LastText = "";
        internal static string LastScenario = "";
        internal static int LastIndex = -1;
        internal static bool LastHadVoice;
        internal static bool LastWasSpoken;

        // ==================================================================
        // 重读缓冲区
        //
        // === 这一条是被玩家当场抓出来的（前三作踩过同一个坑）===
        //
        // 《钟塔》0.1.0.1 的复盘原话：「**根因是「重读缓冲区」的覆盖面不全**
        // —— 选项播报绕过了缓冲区，所以停在选项上按退格什么也念不出来。」
        //
        // 本作第一版又犯了同一个错：`Choices.Ready()` 直接调播报、没进缓冲区，
        // 于是有选项时按退格念的是**上一句台词**（或者干脆没反应）。
        //
        // 规矩定死：**凡是玩家可能想重听的东西，都要进缓冲区。**
        //   · 台词（有配音的也进 —— 补丁只是不自动念它，玩家主动按退格仍然要响应）
        //   · 选项列表（只在选项还挂着的时候有效）
        // 选项被清掉之后（选完了 / 段落结束），缓冲区**退回台词**，
        // 免得之后每次按退格都去念那几个已经不存在的选项。
        // ==================================================================

        private static string _choicesText = "";
        private static bool _choicesPending;

        /// <summary>选项出现时把整段选项文本放进缓冲区（由 Choices.Ready 调用）。</summary>
        internal static void SetChoices(string text)
        {
            _choicesText = text ?? "";
            _choicesPending = _choicesText.Length > 0;
        }

        /// <summary>选项没了（选完了 / 段落结束）—— 缓冲区退回台词。</summary>
        internal static void ClearChoices()
        {
            _choicesText = "";
            _choicesPending = false;
        }

        internal static bool HasChoicesBuffered { get { return _choicesPending; } }

        private static float _lastAt = -100f;
        private static string _lastKey = "";

        /// <summary>收到一行（由 Harmony 挂钩 `DialoguePlaybackTracker.NotifyLineChanged` 调用）。</summary>
        internal static void OnLine(DialogueLine line)
        {
            try
            {
                if (line == null) return;
                // 只有 Normal(0) 有台词；Choice(1) 的文本在 ExtraParams 里，由 Choices 单独处理；
                // End(2)/Command(3) 没有台词（P0 §3.1）。
                if (line.Type != DialogueNodeType.Normal) return;

                string text = TextProc.Clean(line.Text);
                if (!TextProc.ShouldRead(text)) return;

                string key = (line.ScenarioId ?? "") + "#" + line.Index;
                if (key == _lastKey && UnityEngine.Time.realtimeSinceStartup - _lastAt < 5f)
                {
                    A11yHost.Diag("[朗读] 跳过重复行 " + key);
                    return;
                }

                _lastKey = key;
                _lastAt = UnityEngine.Time.realtimeSinceStartup;
                LastScenario = line.ScenarioId ?? "";
                LastIndex = line.Index;
                LastText = text;
                LastHadVoice = !string.IsNullOrEmpty(line.VoiceAddress);

                bool speak;
                if (LastHadVoice)
                    speak = Plugin.CfgReadVoiced != null && Plugin.CfgReadVoiced.Value;
                else
                    speak = Plugin.CfgReadUnvoiced == null || Plugin.CfgReadUnvoiced.Value;

                LastWasSpoken = speak;
                A11yHost.Diag("[朗读] " + key + " 配音=" + (LastHadVoice ? "有" : "无")
                              + " 决定=" + (speak ? "念" : "不念") + " 长度=" + text.Length);

                if (speak) Speech.Speak(text, true);
            }
            catch (Exception e) { A11yHost.Diag("[朗读] OnLine 异常: " + e.Message); }
        }

        /// <summary>
        /// 重读（重读键 / 有配音行的唯一朗读通路）。
        ///
        /// **选项还挂着的时候优先念选项** —— 那才是玩家此刻最可能需要重听的东西。
        /// 念的时候**直接走 Speech、不经过播报队列**：退格是玩家的主动请求，
        /// 让他等配音放完等于没反应（与 `Announcer.Now()` 同一条规矩）。
        /// </summary>
        internal static void Repeat()
        {
            string text = _choicesPending && _choicesText.Length > 0 ? _choicesText : LastText;
            if (string.IsNullOrEmpty(text))
            {
                Speech.Speak("还没有可以重读的内容。", true);
                return;
            }
            A11yHost.Diag("[重读] " + (_choicesPending ? "选项" : "台词") + " 长度=" + text.Length);
            Speech.Speak(text, true);
        }

        /// <summary>进入新的一段剧情时清掉「上一句」。</summary>
        internal static void OnScenarioStarted(string scenarioId)
        {
            LastScenario = scenarioId ?? "";
            LastIndex = -1;
            _lastKey = "";
            LastText = "";
            ClearChoices();
        }
    }
}
