using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace MeiKongA11y
{
    /// <summary>
    /// UiNav 的**逐作区**（L1 平台层的 partial 另一半）。
    ///
    /// 平台层（UiNav.cs）是冻结层，逐作只允许改这一个文件。
    /// 这里只有两样东西是真正逐作的：**对象名别名表** 与 **输入框角色识别**。
    ///
    /// === 本作（《妹控计划》）的实查结论（P1 探针 · D:\mk-test\game · 场景树 1689 行）===
    ///
    /// 1) **别名表故意接近空**。本作开发者把对象名写成了中文：
    ///      · 功能条：小游戏 / 番茄钟 / 日程表 / 设置 / 故事·收藏 / 换装
    ///      · 面板内：新任务 / 确认 / 取消 / 正面 / 侧面 / 随机 / 播放 / 导入音乐 / 关闭 …
    ///    而 UiNav 的取值顺序是「子树 TMP 文本 → 对象名别名」，**子树里有字就不会看别名**。
    ///    所以别名只在极少数「纯图片且名字是英文」的控件上才有用。
    ///
    /// 2) **需要别名的只有纯图片控件**（探针实测：整行没有 text= 的 selectable）：
    ///      · Scrollbar Horizontal / Vertical / Common_VScrollbar Variant —— 滚动条，
    ///        方向键本来就是滑条语义，不需要别名；
    ///      · 一批挂在面板上的 CommonCloseButton —— 名字一样，统一给「关闭」；
    ///      · DialogueOverlayCanvas/ScaleRoot/DeskPetHoverUi/Window/Pet —— 桌宠悬停条本体。
    ///    **每一条都要有依据，不要凭感觉往里加**（流水线 §7 铁律 1）。
    /// </summary>
    internal static partial class UiNav
    {
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 面板通用关闭按钮（纯图片，各面板同名）----
            { "CommonCloseButton", "关闭" },

            // ---- 桌宠悬停交互条（纯图片按钮，文字画在图上）----
            { "DeskPetHoverUi", "桌宠交互条" },
            { "Pet",            "诗萌" },

            // ---- 面板关闭/完成类按钮（对象名是中文但语义不完整）----
            { "已完成界面按钮",     "已完成的任务" },
            { "已完成界面关闭按钮", "关闭已完成列表" },
        };

        /// <summary>
        /// 输入框的「角色名」（姓名框 / 搜索框 / 数字框…）。
        ///
        /// 《钟塔》要用它区分「姓」「名」两个无标签输入框；本作**没有姓名输入**，
        /// 但有四类输入框（探针实测）：
        ///   · 番茄钟的循环次数 / 专注时长 / 休息时长（TMP_InputField，值是 5 / 25 / 4）
        ///   · 音乐播放器的搜索框、表情包工坊的工程名与搜索框
        /// 这些输入框的**同行标签是 TMP 文字**（如「循环次数」「专 注 时 长」），
        /// 平台层的「占位提示 → 同行标签 → 对象名」回退链已经能读到，
        /// 所以这里不需要特殊处理 —— 返回空串表示「没有特殊角色」。
        ///
        /// 保留方法是接口要求（UiNav.cs:996 会调用）。
        /// </summary>
        private static string InputFieldRoleName(TMP_InputField inf)
        {
            return "";
        }
    }
}
