using System;
using System.Text.RegularExpressions;

namespace MeiKongA11y
{
    /// <summary>
    /// 原始文本 → 可朗读文本。**每一个游戏都必须重写这个文件。**
    ///
    /// 这是「同一作内所有花括号都在这里」的地方 —— 平台层不碰文本，
    /// 逐作的脏活全部收敛到这个类。
    ///
    /// === 三作各要处理什么（用来提醒你别漏了本作的那一类）===
    ///
    ///   类别          《钟塔》            《不/存在的你，和我》     《透明的她》
    ///   ----------    ----------------    ----------------------    ----------
    ///   说话人包装    【 韩冬 】 → 剥掉   —                        —
    ///   遮蔽名        【 ?  ? 】 → ？？？  —                        —
    ///   多人同框      【 许堇/韩冬 】→顿号 —                        —
    ///   富文本 ruby   —                   <ruby="注">正文</ruby>    —
    ///   抹除符号      —                   █ ■ □ → 「方块」          —
    ///   行内注释      —                   ;; @wait 2 之后不朗读     —
    ///
    /// ★ **ruby 那条是硬要求**：《不/存在的你，和我》的设计是「正文=表象、旁注=真相」，
    ///   只读正文会让盲人玩家错过题眼。这不是优化，是无障碍完整性问题。
    ///   本作如果有同类设计（视觉上并置、但读屏只能线性读的），同样必须两边都读。
    ///
    /// === 怎么做 ===
    ///
    /// 先用 P0 导出的剧本表**离线**跑这些规则，不进游戏就能验证（流水线 §6 P3 第 1 步）。
    /// 规则要写成纯函数：输入原始行，输出可朗读行，没有副作用、不读游戏状态。
    ///
    /// === 纪律 ===
    ///
    /// - 正则**只处理本作实查到的形态**，不要写「通用」的富文本剥离
    ///   （容易把正文也吃掉，而且下一作一定不适用）
    /// - 每一条规则都要在注释里写明**依据**：来自哪张表、哪个字段、哪次导出
    /// - 拿不准的形态**先输出原文**，宁可多念也不要漏念
    /// </summary>
    internal static class TextProc
    {
        // ==================== 本作的正则 ====================
        // 命名照抄三作的风格：<用途>Re

        /// <summary>TODO 说明这条规则处理什么、依据是什么。</summary>
        private static readonly Regex ExampleRe = new Regex(@"TODO", RegexOptions.Compiled);

        // ==================== 对外入口 ====================

        /// <summary>
        /// 说话人：把原始名字转成要念的名字。没有名字就返回空串。
        /// </summary>
        public static string Speaker(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();

            // TODO 本作的说话人包装（例：【 韩冬 】→ 韩冬）
            // TODO 遮蔽名（例：【 ?  ? 】→ ？？？）
            // TODO 多人同框（例：【 许堇/韩冬 】→ 许堇、韩冬）

            return s;
        }

        /// <summary>
        /// 台词正文：把原始文本转成可朗读文本。
        /// </summary>
        public static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw;

            // TODO 逐条规则。例：
            // s = RubyRe.Replace(s, m => m.Groups[1].Value + "，" + m.Groups[2].Value);  // 正文与旁注都读
            // s = BlockRe.Replace(s, "方块");
            // s = CommentRe.Replace(s, "");        // ;; @wait 2 之后不朗读

            s = s.Replace("\r", "").Replace("\n", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        /// <summary>
        /// 这一行**要不要朗读**。
        ///
        /// 和 Clean 分开的理由：有些行的处理是「整行丢掉」而不是「清洗后朗读」
        /// （行内注释行、空行、纯控制指令行）。
        /// </summary>
        public static bool ShouldRead(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            // TODO 例：if (CommentOnlyRe.IsMatch(raw)) return false;
            return true;
        }
    }
}
