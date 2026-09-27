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

        /// <summary>重读当前句（重读键 / 有配音行的唯一朗读通路）。</summary>
        internal static void Repeat()
        {
            if (string.IsNullOrEmpty(LastText))
            {
                Speech.Speak("还没有可以重读的台词。", true);
                return;
            }
            Speech.Speak(LastText, true);
        }

        /// <summary>进入新的一段剧情时清掉「上一句」。</summary>
        internal static void OnScenarioStarted(string scenarioId)
        {
            LastScenario = scenarioId ?? "";
            LastIndex = -1;
            _lastKey = "";
            LastText = "";
        }
    }
}
