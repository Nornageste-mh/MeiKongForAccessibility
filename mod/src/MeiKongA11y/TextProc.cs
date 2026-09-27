using System;
using System.Text.RegularExpressions;

namespace MeiKongA11y
{
    /// <summary>
    /// 原始文本 → 可朗读文本。**每一作都必须重写这个文件。**
    ///
    /// === 本作（《妹控计划》）的实查结论（P0 §3.5，12 张表 1392 行台词全量扫描）===
    ///
    ///   富文本标签    <color=> / &lt;b&gt; / &lt;i&gt; / &lt;size=> / &lt;sprite> / [..] / {..} / \n
    ///                 —— **各 0 行**。这一作**没有**行内富文本。
    ///   说话人包装    —— **不存在说话人字段**（DialogueLine 只有 10 个字段，没有 CharacterName）。
    ///   全角括号      —— 全 1392 行里 【…】 出现 **0 次**，所以 belfry 那套剥括号规则在这里用不上。
    ///
    /// 结论：**TextProc 在本作近乎空实现**。belfry 的 TextProc 有一半代码在剥富文本与说话人包装，
    /// 这里一行都用不上。唯一的脏活是标点与空白规整：
    ///   · ……  384 行、——  25 行、～ 23 行 —— 这些是**读屏怎么念**的问题，不是清洗问题，
    ///     交给平台层的语音后端（NVDA/Tolk/SAPI 都能把「……」念成停顿），**不在这里改字**。
    ///   · 真正的清洗只有：去首尾空白、把连续空白压成一个、去掉换行。
    ///
    /// ★ 纪律：**不在这里改剧情文字**。任何「看起来更顺口」的改写都可能改变语气，
    ///   而这一作 97.2% 的行有配音 —— 玩家会拿听到的配音跟读屏念的做比对，改字等于制造不一致。
    /// </summary>
    internal static class TextProc
    {
        private static readonly Regex WhitespaceRe = new Regex(@"[ \t\u3000]{2,}", RegexOptions.Compiled);

        /// <summary>
        /// 说话人：本作**没有**说话人数据（DialogueLine 里没有姓名字段），
        /// 所以这里恒返回空串 —— 补丁不会、也不应该播报「谁在说」。
        ///
        /// 保留这个方法是为了平台层接口一致（UiNav / Reader 都会问一次）。
        /// 依据：P0 §3.4（12 张表的 10 个字段并集里没有姓名字段）。
        /// </summary>
        public static string Speaker(string raw)
        {
            return "";
        }

        /// <summary>
        /// 台词正文 → 可朗读文本。只做空白规整，不改一个字（理由见类注释）。
        /// </summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;
            s = s.Replace("\r", "").Replace("\n", " ");
            s = WhitespaceRe.Replace(s, " ");
            return s.Trim();
        }

        /// <summary>
        /// 这一行要不要朗读。
        ///
        /// 与 Clean 分开的理由：有些行是「整行丢掉」而不是「清洗后朗读」。
        /// 本作的判据直接来自运行时枚举（P0 §3.1）：
        ///   · Type != Normal(0) 的行没有台词（Choice 的文本在 ExtraParams 里、由 Choices 单独处理）
        ///   · 空文本行不念
        /// </summary>
        public static bool ShouldRead(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            return raw.Trim().Length > 0;
        }
    }
}
