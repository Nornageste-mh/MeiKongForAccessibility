using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// UiNav 的**逐作区**（L1 平台层的 partial 另一半）。逐作只允许改这一个文件。
    ///
    /// === 这个文件为什么长这样（P1 实机取证）===
    ///
    /// 实测：全屏 173 个「当前激活且可交互」的控件里，**131 个读不到任何文字**，
    /// 只能退化成对象名。其中又分三类：
    ///
    ///   1) **英文对象名**：CommonCloseButton / Button (6) / CCPlus / VolumeButton /
    ///      PlayMode / Pre / Next / DirectionButtonL …
    ///      —— 玩家原话：「如果是 setting、next 这种绝大部分人都知道是什么意思，
    ///      但其它的就不一定了」。
    ///   2) **预制体占位文字**：`New Text` / `Button` / `音效名称`
    ///      —— 比对象名更糟：玩家会以为那个东西真的叫「New Text」。
    ///   3) **同名不同义**：`CommonButton` 在番茄钟里分别是「停止 / 暂停 / 下一个」，
    ///      在互动区里是「和诗萌聊聊」—— 只看对象名的平表**永远区分不开**，
    ///      必须看**路径**。
    ///
    /// 所以本文件做四件事：噪声过滤、路径感知的标签解析、组的中文名、组的排序权重。
    /// 四件事都通过 A11yHost 的钩子接进平台层，**平台层逻辑一行没改**。
    /// </summary>
    internal static partial class UiNav
    {
        // ==================================================================
        // 1) 预制体占位文字：念出来比念对象名还糟
        //    依据：probe_scene_tree.txt 里实际出现的 TMP 文本
        //          （minigame 面板的 "New Text" / 通用按钮的 "Button" /
        //           环境音效槽位的 "音效名称"）。
        //    ⚠ 只放**实查到的**占位串。宁可漏过滤，也不要把真标签吃掉。
        // ==================================================================
        private static readonly HashSet<string> PlaceholderTexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "New Text", "Button", "Text", "Label", "Item", "Slider", "Toggle",
            "音效名称", "000", "00", "0",
        };

        internal static bool GameTextIsNoise(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            return PlaceholderTexts.Contains(s.Trim());
        }

        // ==================================================================
        // 2) 路径感知的标签解析
        //    调用时机：平台层走完「子树文字 → 同行兄弟文字 → 别名表」都没结果时。
        //    所以这里不需要重复那些判断，专攻「只能靠路径区分」的部分。
        // ==================================================================
        internal static string GameLabelOf(Selectable s)
        {
            if (s == null) return "";
            try
            {
                // 记忆翻牌的牌：对象名 Card_N 没有语义、牌面是纯图片
                // → 交给 Minigame 报「第几行第几列 + 此刻状态」
                var card = s.GetComponent<ShiMeng.Minigames.MemoryMatch.MemoryMatchCardView>();
                if (card != null) return Minigame.CardLabel(card);

                string name = s.gameObject.name ?? "";
                string path = PathOfLabel(s.transform);

                // ---- 2a. 换装：7 个快捷搭配（对象名 Button / Button (1..6)）----
                if (path.IndexOf("/QuickButtons/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "快捷搭配 " + (s.transform.GetSiblingIndex() + 1);

                // ---- 2b. 环境音效：3 个槽位（对象名 SFXSlot / SFXSlot (1) / (2)）----
                if (path.IndexOf("/SFXSlot", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "环境音效 " + (s.transform.GetSiblingIndex() + 1);

                // ---- 2c. 音乐播放器（英文名最集中的地方）----
                bool music = path.IndexOf("PetMusicPlayer", StringComparison.OrdinalIgnoreCase) >= 0
                          || path.IndexOf("/music_player/", StringComparison.OrdinalIgnoreCase) >= 0;
                if (music)
                {
                    if (s is Slider) return "播放进度";     // 同行文字是当前曲名，不是这个滑条的名字
                    switch (name)
                    {
                        case "Pre": return "上一首";
                        case "Next": return "下一首";
                        case "Play/Pause": return "播放或暂停";
                        case "VolumeButton": return "音量";
                        case "PlayMode": return "播放模式";
                        case "随机": return "随机播放";
                        case "播放": return "播放";
                    }
                }

                // ---- 2d. 加减按钮：**不猜**它是加什么的，用同一行里游戏自己写的标签 ----
                //     实测番茄钟里有三组同名按钮（CCPlus/CCMinus × 3），
                //     分别属于「循环次数 / 专注时长 / 休息时长」—— 只有同行标签能区分。
                if (name == "CCPlus" || name == "CCMinus"
                    || name.EndsWith("Up", StringComparison.Ordinal)
                    || name.EndsWith("Down", StringComparison.Ordinal)
                    || name.EndsWith("Plus", StringComparison.Ordinal)
                    || name.EndsWith("Minus", StringComparison.Ordinal))
                {
                    // 1) 首选：游戏自己的字段（最可靠）
                    string role = PomodoroRoleOf(s);
                    if (!string.IsNullOrEmpty(role)) return role;
                    // 2) 退一步：同行标签（`<行名>加 / 减`）
                    string what = RowTextOf(s);
                    if (string.IsNullOrEmpty(what)) return "";
                    bool plus = name == "CCPlus"
                             || name.EndsWith("Up", StringComparison.Ordinal)
                             || name.EndsWith("Plus", StringComparison.Ordinal);
                    return what + (plus ? "加" : "减");
                }

                if (path.IndexOf("/pomodoro/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // 数字框：同行标签（循环次数 / 专注时长 / 休息时长）就是它的名字
                    if (s is TMP_InputField)
                    {
                        string what = RowTextOf(s);
                        if (!string.IsNullOrEmpty(what)) return what;
                    }
                    // Running/Stop|Pause|Next/CommonButton —— 三个同名 CommonButton，
                    // 只能靠父节点区分（这就是「平表做不到」的典型）
                    string parent = s.transform.parent != null ? s.transform.parent.gameObject.name : "";
                    switch (parent)
                    {
                        case "Stop": return "停止";
                        case "Pause": return "暂停";
                        case "Next": return "下一个";
                    }
                }

                // ---- 2e. 换装面板的左右翻页（对象名 Pre / Next）----
                if (path.IndexOf("/costume/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (name == "Pre") return "上一个";
                    if (name == "Next") return "下一个";
                }

                // ---- 2f. 互动按钮：5 个同名 CommonButton，标签由游戏自己的 Label 属性给 ----
                if (path.IndexOf("/DialogueButtons/", StringComparison.OrdinalIgnoreCase) >= 0)
                    return StoryButtonLabel(s);

                // ---- 2g. 桌宠悬停条 ----
                if (path.IndexOf("DeskPetHoverUi", StringComparison.OrdinalIgnoreCase) >= 0 && name == "Pet")
                    return "切换到全屏";

                // ---- 2h. 通用关闭 ----
                if (name == "CommonCloseButton") return "关闭";

                // ---- 2i. 方向键（剧情回想/翻页）----
                if (name.StartsWith("DirectionButton", StringComparison.OrdinalIgnoreCase))
                    return name.EndsWith("L") || name.Contains("(1)") ? "上一个" : "下一个";

                // ---- 2j. 社交链接：本来就会被排除名单摘掉，兜底给个不至于误操作的名字 ----
                if (name == "Steam" || name == "Heihe" || name == "Bilibili") return "社交链接";

                return "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 「和诗萌聊聊」按钮：对象名是 CommonButton (1..4)，**没有语义**。
        /// 唯一可靠的来源是游戏自己的 `MainStoryTriggerButtonVariant.Label` 属性 ——
        /// 它优先返回序列化的 variantLabel，为空才回退到对象名。这里反射读它。
        /// </summary>
        private static Type _variantType;
        private static bool _variantProbed;

        private static string StoryButtonLabel(Selectable s)
        {
            try
            {
                if (!_variantProbed)
                {
                    _variantProbed = true;
                    var asm = s.GetType().Assembly;
                    _variantType = asm.GetType("ShiMeng.DialogueV2.MainStoryTriggerButtonVariant");
                }
                if (_variantType != null)
                {
                    var comp = s.GetComponent(_variantType);
                    if (comp != null)
                    {
                        var pi = _variantType.GetProperty("Label");
                        if (pi != null)
                        {
                            string v = Convert.ToString(pi.GetValue(comp));
                            if (!string.IsNullOrEmpty(v) && !v.StartsWith("CommonButton")) return v;
                        }
                    }
                }
            }
            catch { }
            return "和诗萌聊聊";
        }

        /// <summary>只用于判定的短路径（最多 6 层，够区分面板了）。</summary>
        private static string PathOfLabel(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            int guard = 0;
            while (t != null && guard++ < 8)
            {
                sb.Insert(0, "/" + t.gameObject.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        // ==================================================================
        // 3) 面板组的中文名与排序权重
        //
        //    实测默认状态是 4 组，而**最常用的功能条在第 2 组**：
        //      [组 1] UpRight                 → 设置/装饰/故事收藏/换装/隐藏UI/退出
        //      [组 2] Right                   → 联动内容/鬼畜制造机/音乐/番茄钟/日程表/小游戏
        //      [组 3] PetMusicPlayer_Window   → Pre/Play-Pause/Next/VolumeButton/PlayMode/曲目滑条
        //      [组 4] ScaleRoot               → 和诗萌聊聊
        //    玩家按 Tab 落在组 1，而想去的地方在组 2 —— 这就是「找不到想去的地方」。
        // ==================================================================
        internal static string GameGroupName(Transform root)
        {
            if (root == null) return "";
            switch (root.gameObject.name)
            {
                case "Right": return "功能条";
                case "UpRight": return "快捷栏";
                case "PetMusicPlayer_Window": return "音乐播放器";
                case "ScaleRoot":
                {
                    // 面板都挂在 ScaleRoot 下：打开了哪个面板，组就叫那个面板的名字
                    // （实测：打开番茄钟后这一组从「和诗萌聊聊 1 项」变成 11 项，
                    //   仍然叫「互动」会让人以为还在原地）
                    string panel = Surfaces.CurrentPanelNameCn();
                    return string.IsNullOrEmpty(panel) ? "互动" : panel;
                }
                default: return "";
            }
        }

        internal static int GameGroupPriority(Transform root)
        {
            if (root == null) return 100;
            switch (root.gameObject.name)
            {
                case "Right": return 0;                     // 功能条：最常用，排第一
                case "ScaleRoot": return 10;                // 互动（和诗萌聊聊）
                case "UpRight": return 20;                  // 快捷栏
                case "PetMusicPlayer_Window": return 30;    // 音乐播放器
                default: return 100;
            }
        }

        // ==================================================================
        // 4) 别名表（平台层在「同行兄弟文字」之后、逐作钩子之前查它）
        //    这里只放**平表能表达**的那些；需要看路径的一律走上面的 GameLabelOf。
        // ==================================================================
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "CommonCloseButton", "关闭" },
            { "DeskPetHoverUi",    "桌宠交互条" },
        };

        /// <summary>
        /// 输入框的「角色名」。
        ///
        /// === 为什么这里必须靠游戏自己的字段，而不是靠同行标签 ===
        ///
        /// 番茄钟那三个数字框在运行时会被换成**另一套布局**（`pomodoroSlot` 槽位），
        /// 换完之后整行**一个字都没有**，同行标签链断掉，只能念出对象名
        /// `InputField (TMP)` —— 盲人根本不知道哪个是循环次数、哪个是专注时长。
        ///
        /// 而游戏自己知道：`ShiMeng.Pomodoro.PomodoroUIBridge` 上有
        /// `focusMinutesInput` / `restMinutesInput` / `cycleCountInput` 三个序列化字段
        /// （反编译实查）。**按引用比对**就能把角色认出来 ——
        /// 这比「槽位 (2) 是专注时长」这种靠数值猜的写法可靠得多，
        /// 也不怕游戏改布局。
        /// </summary>
        private static string InputFieldRoleName(TMP_InputField inf)
        {
            return PomodoroRoleOf(inf);
        }

        // ---- 番茄钟：字段名 → 中文角色（字段名来自反编译实查）----
        private static readonly string[][] PomodoroFields =
        {
            new[] { "focusMinutesInput", "专注时长" },
            new[] { "focusMinutesPlus",  "专注时长加" },
            new[] { "focusMinutesMinus", "专注时长减" },
            new[] { "restMinutesInput",  "休息时长" },
            new[] { "restMinutesPlus",   "休息时长加" },
            new[] { "restMinutesMinus",  "休息时长减" },
            new[] { "cycleCountInput",   "循环次数" },
            new[] { "cycleCountPlus",    "循环次数加" },
            new[] { "cycleCountMinus",   "循环次数减" },
        };

        private static Type _pomodoroBridgeType;
        private static bool _pomodoroProbed;

        private static void ProbePomodoro()
        {
            if (_pomodoroProbed) return;
            _pomodoroProbed = true;
            try
            {
                var asm = typeof(ShiMeng.DialogueV2.DialogueLine).Assembly;
                _pomodoroBridgeType = asm.GetType("ShiMeng.Pomodoro.PomodoroUIBridge");
            }
            catch { }
        }

        /// <summary>按「游戏自己的字段指向哪个对象」反查角色；认不出来返回空串。</summary>
        private static string PomodoroRoleOf(Component c)
        {
            try
            {
                if (c == null) return "";
                ProbePomodoro();
                if (_pomodoroBridgeType == null) return "";
                var objs = Resources.FindObjectsOfTypeAll(_pomodoroBridgeType);
                if (objs == null || objs.Length == 0) return "";
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                foreach (var o in objs)
                {
                    foreach (var pair in PomodoroFields)
                    {
                        var fi = _pomodoroBridgeType.GetField(pair[0], F);
                        if (fi == null) continue;
                        var v = fi.GetValue(o) as Component;
                        if (v != null && v.gameObject == c.gameObject) return pair[1];
                    }
                }
            }
            catch { }
            return "";
        }
    }
}
