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

        /// <summary>
        /// 剧情是不是正在进行（0.2.1 起给 Focus 用）。
        ///
        /// 主界面的图标浏览只在「没有面板、没有剧情、没有小游戏」时才有意义：
        /// 剧情一开始必须立刻退出浏览，否则方向键/回车会被补丁抢走，
        /// 玩家按空格推进剧情时可能先激活主界面的某个图标（这正是「不符合盲人逻辑」的一类）。
        /// </summary>
        internal static bool InDialogue;

        // 真正的剧情（story_* / tool_* 等）。桌宠的 idle 台词（basic_待机_* / basic_启动_* 等）也会走
        // NotifyScenarioStarted，但它们不是「玩家在过剧情」，不能因此把主界面浏览踢掉。
        internal static bool InStory;

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

        // 缓冲区本体在 L1 的 Repeat.cs 里（0.1.1.0 起）。
        // 逐作层**只往里塞，绕不过去** —— 由 tools/lint_repeat.ps1 机械保证。
        // 这里只保留两个薄透传，免得调用方到处写 Repeat。

        private const string ChoicesTag = "meikong.choices";

        /// <summary>
        /// 选项出现时把整段选项文本设为重读优先项（由 Choices.Ready 调用）。
        ///
        /// 0.2.7.2 起用 <c>Repeat.Note</c> 而不是 <c>Push</c>：平台层的 Push 已经变成
        /// 「入缓冲区 **并且自己播报**」（G33，`platform/Repeat.cs` 注释写明是冻结语义），
        /// 而本作的播报点在 Choices.Ready 里紧接着调 <c>Announcer.Auto</c>
        /// —— 再 Push 就会把同一串选项念两遍。Note 只入缓冲区，播报仍由调用方负责。
        /// </summary>
        internal static void SetChoices(string text) { Repeat.Note(text); }

        /// <summary>选项没了（选完了 / 段落结束）—— 缓冲区退回台词。</summary>
        internal static void ClearChoices() { Repeat.Drop(ChoicesTag); }

        /// <summary>缓冲区里有没有东西（日志/诊断用；选项不再单独占优先级）。</summary>
        internal static bool HasChoicesBuffered { get { return Repeat.Current != null; } }

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

                // 念的走 Say（播报 + 进缓冲区），不念的也要走 Note（只记不念）——
                // 有配音的行之所以不自动念，只是不想和配音叠读；
                // 玩家主动按重读键时**必须**还能听到它，那是这类行唯一的朗读通路。
                if (speak) Repeat.Say(text);
                else Repeat.Note(text);
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
        internal static void RepeatLast()
        {
            // 缓冲区本体在 L1：0.2.7.2 起选项用 Note 入缓冲（不再占优先级队列），
            // 所以重读念的就是「最近进入缓冲区的那一串」—— 选项出现时它就是选项，选完之后是台词。
            A11yHost.Diag("[重读] 长度=" + (Repeat.Current != null ? Repeat.Current.Length : 0));
            Repeat.Again();
        }

        /// <summary>进入新的一段剧情时清掉「上一句」。</summary>
        internal static void OnScenarioStarted(string scenarioId)
        {
            LastScenario = scenarioId ?? "";
            LastIndex = -1;
            _lastKey = "";
            LastText = "";
            InDialogue = true;
            // 桌宠的 idle 台词（basic_待机_* / basic_启动_* / basic_点击_* 等）也会走这条事件，
            // 但它们不是「玩家正在过剧情」。只有非 basic_ 的场景（story_* / tool_* 等）
            // 才允许把主界面图标浏览踢掉，否则玩家一按方向键就会被待机台词打断。
            InStory = !LastScenario.StartsWith("basic_", System.StringComparison.OrdinalIgnoreCase);
            A11yHost.Diag("[剧情] 开始 " + LastScenario + " → InDialogue=true, InStory=" + InStory);
            // 只清选项优先项；**不清主缓冲区** ——
            // 「重读最近听到的内容」跨段落仍然成立，清掉反而让刚进新章时按重读键没反应。
            ClearChoices();
        }

        /// <summary>一段剧情结束：主界面图标浏览可以重新开始。</summary>
        internal static void OnScenarioEnded(string scenarioId)
        {
            A11yHost.Diag("[剧情] 结束 " + (scenarioId ?? "") + " → InDialogue=false");
            InDialogue = false;
            InStory = false;
        }
    }
}
