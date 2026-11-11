using System;
using System.Collections.Generic;

namespace MeiKongA11y
{
    /// <summary>
    /// 逐面板的「结构表」：标题、依赖提示、分组名。
    ///
    /// === 这张表是怎么来的（不是猜的） ===
    /// 全部来自 2026-11-11 的**运行时 dump**（`UiDump.cs` 临时取证工具，dump 出每个面板
    /// 真实存在的 Selectable 路径、屏幕坐标、互斥状态、以及游戏画面上**可见的 TMP 文字**），
    /// 再用 Steam 商店页截图 / 实机演示视频核对「玩家看到的是什么」。
    /// 证据留在仓外：`D:\mk-test\game\BepInEx\ui-dump.txt`（100KB+）、`D:\mk-test\shots\shot1..7.jpg`。
    ///
    /// === 为什么需要分组名 ===
    /// 盲人玩家听到的不是「27 个可操作项」，而是「音量设置，播放音量，滑条，50%，第 2 项，共 4 项」。
    /// 分组名优先取游戏自己画在界面上的分组标题（如「显示设置」「音量设置」「环境音效」），
    /// 没有可见标题时才用 dump 结构里的分组（这些只用来分层次，不当玩家语义）。
    ///
    /// === 图片按钮的标签 ===
    /// 本作大量按钮是纯图片（TMP 无字、也没有本地化资源表，已实测）。它们的中文名只有一个来源：
    /// 游戏自己的对象名 / 游戏自己的入口函数名（如 <c>PetUiIntegrationHub.OpenSchedule()</c>），
    /// 再与商店页截图、实机演示逐一对上。这类标签在 `docs/交互形态判定.md` §5 里登记为
    /// 「标签来源=游戏对象名+公开资料核对」，不是 OCR 猜的。
    /// </summary>
    internal static class PanelMap
    {
        /// <summary>面板根对象名 → 面板 key（dump 出来的根名与 id 不完全一致）。</summary>
        internal static string KeyOf(string rootName)
        {
            string n = (rootName ?? "").Trim();
            if (n.Length == 0) return "";
            if (n == "settingPanel") return "setting";
            if (n == "backgroundSetting") return "background";
            return n.ToLowerInvariant();
        }

        // ==================== 标题（玩家看到的叫法） ====================
        // 来源：PetUiIntegrationHub 各 OpenXxx() 的日志上下文 + 商店页「玩法」清单。

        internal static string TitleOf(string key)
        {
            switch (key)
            {
                case "scene": return "主界面";
                case "setting": return "系统设置";
                case "schedule": return "日程表";
                case "pomodoro": return "番茄钟";
                case "meal_today": return "今天吃啥";
                case "music_player": return "音乐列表";
                case "anime_assistant": return "追番助手";
                case "costume": return "换装";
                case "background": return "背景设置";
                case "favorites": return "收藏内容";
                case "minigame": return "小游戏";
                case "meme_maker": return "鬼畜制造机";
                case "collab_shelf": return "联动展架";
                case "voice_collect": return "语音收藏";
                // 提示/确认框：自己身上那段话是**正文**，不是面板名；名字用短的，正文在播报里单独念
                case "tips1": return "提示";
                case "tips2": return "确认删除";
                default: return "";
            }
        }

        /// <summary>
        /// 提示/确认框：面板自己身上那段话是正文（不是控件标签），进入时要把正文念出来。
        /// 依据：Tips1 只有「好的 / 不好也得好」两个按钮，Tips2 是「犹豫一下 / 当断则断」，
        /// 玩家要听的是那句提示/确认语，否则只听到两个按钮名不知道在确认什么。
        /// </summary>
        internal static bool HasBody(string key)
        {
            return key == "tips1" || key == "tips2";
        }

        // ==================== 依赖提示（说明「先做什么、后做什么」） ====================

        internal static string HintOf(string key)
        {
            switch (key)
            {
                case "setting":
                    // 反编译核对（WindowModeSettings.cs：OnWindowModeToggled / OnFullscreenModeToggled /
                    // OnResolutionToggled 内部直接 ApplyWindowMode / ApplyFullscreenMode / ApplyResolution；
                    // 音量走 PetSettingsApplier.Apply*Volume）——**改完立刻生效**。
                    // prefab 里那两个「应用」「还原」按钮运行时 active=0，从未出现，属未启用残留。
                    return "改完立刻生效。桌宠大小要先切桌宠模式。";
                case "costume":
                    // 反编译核对（CostumePanelController.cs:337 SelectOutfitAtListIndex → ApplySelection →
                    // TryPersistAppliedCostume）：选卡**立即**换上主模型并写存档，游戏里没有「确认」这一步
                    // （确认/还原/正面/侧面那几个 Prefab 按钮在本作运行时始终 active=0，属未启用残留）。
                    return "选中即换上并保存；下面是差分开关。";
                case "pomodoro":
                    return "先设时长，再开始专注。";
                case "background":
                    return "";
                case "favorites":
                    return "先选页签，再选章节。";
                case "music_player":
                    return "";
                case "meme_maker":
                    return "";
                case "schedule":
                    return "回车勾选；Esc 结束输入。";
                case "collab_shelf":
                    return "回车看简介与链接；外链会切出画面。";
                case "minigame":
                    return "";
                case "voice_collect":
                    return "";
                default:
                    return "";
            }
        }

        // ==================== 分组 ====================

        // ==================== 首焦点落在哪一组 ====================
        /// <summary>
        /// 面板打开后第一下方向键/第一句朗读应该落在的**分组**（不是具体控件）。
        /// 依据是「玩家打开这个面板想干什么」：音乐播放器先给曲目、换装先给服装卡、
        /// 设置先给显示设置、鬼畜制造机先给工程列表。分组名必须与 SectionOf 的返回值一致，
        /// 否则会被当成「没找到」而回落到「第一个可用项」。
        /// </summary>
        internal static string PrimarySectionOf(string key)
        {
            switch (key)
            {
                case "setting": return "显示设置";
                case "schedule": return "任务";
                case "pomodoro": return "时长设置";
                case "costume": return "服装";
                case "background": return "环境音效";
                case "favorites": return "页签";
                case "music_player": return "曲目";
                case "meme_maker": return "工程列表";
                case "collab_shelf": return "作品";
                case "voice_collect": return "语音条目";
                default: return "";
            }
        }

        /// <summary>
        /// 「主要分组」里优先落在哪种角色上。番茄钟里 10 个控件的第一项是减号按钮，
        /// 盲人打开面板想改的是「专注时长」这个输入框，所以优先输入框。
        /// 返回空表示按屏幕位置取第一个可用项。
        /// </summary>
        /// <summary>「主要分组」里优先落在标签含这个关键词的项上（番茄钟要先念「专注时长」而不是休息/循环）。</summary>
        internal static string PreferredLabelOf(string key)
        {
            switch (key)
            {
                case "pomodoro": return "专注";
                default: return "";
            }
        }
        /// <summary>
        /// 有些面板里的输入框内容不是「名字」而是大段材料（鬼畜制造机 52 个工程槽里装的是台词），
        /// 逐条念内容会把面板变成朗读机，所以这些分组只念名字和序号。
        /// </summary>
        internal static bool SuppressInputContent(string key, string section)
        {
            if (key == "meme_maker" && section == "工程列表") return true;
            return false;
        }
        /// <summary>这类分组里每一项的名字都一样（「未命名工程1」×52），用序号当名字更清楚。返回前缀，空串表示不替换。</summary>
        internal static string NumericLabelOf(string key, string section)
        {
            if (key == "meme_maker" && section == "工程列表") return "工程";
            return "";
        }
        /// <summary>
        /// 这个输入框只收数字吗？
        /// 番茄钟的「专注时长 / 休息时长 / 循环次数」三个框在 prefab 里是
        /// contentType=Standard + characterValidation=None（TMP 层什么都不挡），
        /// 但游戏读值走的是 <c>int.TryParse</c>（`PomodoroManager.SetFocusMinutesFromString` 等），
        /// 写进字母会被静默丢掉、玩家以为改成功了。所以这一类输入框由补丁按数字挡字符。
        /// </summary>
        internal static bool NumericInput(string key, string section)
        {
            if (key == "pomodoro" && section == "时长设置") return true;
            return false;
        }
        internal static string PreferredRoleOf(string key)
        {
            switch (key)
            {
                case "pomodoro": return "输入框";
                default: return "";
            }
        }
        /// <summary>按「相对面板根的路径」判断所属分组；path 用 / 分隔。</summary>
        internal static string SectionOf(string key, string path)
        {
            string p = path ?? "";
            switch (key)
            {
                case "scene":
                    if (p.IndexOf("/Right/", StringComparison.OrdinalIgnoreCase) >= 0) return "功能条";
                    if (p.IndexOf("/UpRight/", StringComparison.OrdinalIgnoreCase) >= 0) return "快捷栏";
                    if (p.IndexOf("PetMusicPlayer_Window", StringComparison.OrdinalIgnoreCase) >= 0) return "唱片机";
                    if (p.IndexOf("DialogueButtons", StringComparison.OrdinalIgnoreCase) >= 0) return "互动区";
                    return "主界面";

                case "setting":
                    if (p.IndexOf("/显示设置/", StringComparison.Ordinal) >= 0) return "显示设置";
                    if (p.IndexOf("/桌宠设置/", StringComparison.Ordinal) >= 0) return "桌宠设置";
                    if (p.IndexOf("/音效设置/", StringComparison.Ordinal) >= 0) return "音量设置";
                    return "系统设置";

                case "costume":
                    if (p.IndexOf("/Toggles/", StringComparison.OrdinalIgnoreCase) >= 0) return "筛选";
                    if (p.IndexOf("/QuickButtons/", StringComparison.OrdinalIgnoreCase) >= 0) return "快速搭配";
                    if (p.IndexOf("CostumeCard", StringComparison.OrdinalIgnoreCase) >= 0) return "服装";
                    if (p.IndexOf("/Pre", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("/Next", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("正面", StringComparison.Ordinal) >= 0
                        || p.IndexOf("侧面", StringComparison.Ordinal) >= 0
                        || p.IndexOf("确认", StringComparison.Ordinal) >= 0
                        || p.IndexOf("取消", StringComparison.Ordinal) >= 0) return "预览操作";
                    return "换装";

                case "pomodoro":
                    if (p.IndexOf("/Start/", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("/Image/", StringComparison.OrdinalIgnoreCase) >= 0) return "时长设置";
                    if (p.IndexOf("/Stop/", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("/Pause/", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("/Next/", StringComparison.OrdinalIgnoreCase) >= 0) return "计时控制";
                    return "番茄钟";

                case "background":
                    if (p.IndexOf("/SFXSlot", StringComparison.OrdinalIgnoreCase) >= 0) return "环境音效";
                    if (p.IndexOf("/BG/", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("BgSlot", StringComparison.OrdinalIgnoreCase) >= 0) return "背景";
                    return "换背景";

                case "favorites":
                    if (p.IndexOf("/Tab", StringComparison.OrdinalIgnoreCase) >= 0) return "页签";
                    if (p.IndexOf("MainSlot", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) >= 0) return "章节";
                    return "收藏内容";

                case "music_player":
                    if (p.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) >= 0) return "曲目";
                    if (p.IndexOf("musicButtons", StringComparison.OrdinalIgnoreCase) >= 0) return "播放控制";
                    if (p.IndexOf("/导入音乐/", StringComparison.Ordinal) >= 0
                        || p.IndexOf("/搜索/", StringComparison.Ordinal) >= 0) return "导入与搜索";
                    return "音乐播放器";

                case "meme_maker":
                    // 运行时 dump 实测结构：ProjectPanel（工程列表）/ SearchPanel（素材搜索）/
                    // EditorPanel（轨道编辑）/ Toolbar（顶部工具）。
                    if (p.IndexOf("Toolbar", StringComparison.OrdinalIgnoreCase) >= 0) return "工具栏";
                    if (p.IndexOf("SearchItem", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("SearchPanel/Scroll", StringComparison.OrdinalIgnoreCase) >= 0) return "搜索结果";
                    if (p.IndexOf("ProjectSlot", StringComparison.OrdinalIgnoreCase) >= 0
                        || p.IndexOf("ProjectPanel/Scroll", StringComparison.OrdinalIgnoreCase) >= 0) return "工程列表";
                    if (p.IndexOf("SearchPanel", StringComparison.OrdinalIgnoreCase) >= 0) return "素材搜索";
                    if (p.IndexOf("ProjectPanel", StringComparison.OrdinalIgnoreCase) >= 0) return "工程面板";
                    if (p.IndexOf("ProgressRow", StringComparison.OrdinalIgnoreCase) >= 0) return "播放进度";
                    if (p.IndexOf("EditorPanel", StringComparison.OrdinalIgnoreCase) >= 0) return "轨道编辑";
                    return "鬼畜制造机";

                case "schedule":
                    if (p.IndexOf("TaskPrefab", StringComparison.OrdinalIgnoreCase) >= 0) return "任务";
                    return "日程表";

                case "collab_shelf":
                    if (p.IndexOf("GameSlot", StringComparison.OrdinalIgnoreCase) >= 0) return "作品";
                    if (p.IndexOf("buttons", StringComparison.OrdinalIgnoreCase) >= 0) return "链接";
                    return "联动展架";

                case "voice_collect":
                    if (p.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase) >= 0) return "语音条目";
                    return "语音收藏";

                default:
                    return TitleOf(key);
            }
        }

        /// <summary>
        /// 场景里的纯图片图标：对象名 → 玩家听得懂的标签。
        /// 依据：对象名本身（`联动内容`/`番茄钟`…）+ `PetUiIntegrationHub.OpenXxx()` 的入口名
        /// + 商店页截图核对。**只用于场景图标**，面板内部一律优先读游戏自己画出来的 TMP 文字。
        /// </summary>
        /// <summary>
        /// 纯图片控件的约定标签：这类控件在游戏里没有 TMP 文本，也没有本地化条目，
        /// 旁边的文字属于别的控件（关掉面板的 X 会因此被念成面板标题）。
        /// 这里按游戏自己的对象名给出「这个动作叫什么」，并在判定文档 §5 登记为遗留缺陷。
        /// 只用于 UI 约定动作，绝不把对象名当玩家语义直接念。
        /// </summary>
        internal static string LabelOverride(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return "";
            switch (objectName)
            {
                case "CommonCloseButton":        return "关闭";
                case "已完成界面关闭按钮":        return "关闭已完成任务";
                case "已完成界面按钮":            return "已完成的任务";
                case "VolumeButton":             return "音量";
                case "Pre":                      return "上一首";
                case "Play/Pause":               return "播放或暂停";
                case "Next":                     return "下一首";
                case "PlayMode":                 return "播放模式";
                // 音乐列表底部三个工具按钮：游戏 prefab 把三处的 TMP 文本都写成了「导入音乐」（复制粘贴残留），
                // 但 onClick 分别接的是 OnListToolbarRandomClicked / OnListToolbarPlayPauseClicked /
                // UiImportMusicFromFolder（PetMusicScrollListController.cs:1086-1096）。按对象名给动作名。
                case "随机":                      return "随机播放";
                case "播放":                      return "播放或暂停";
                case "导入音乐":                  return "导入音乐";
                // 联动展架的社交链接：三个按钮的 TMP 文本都写成「社交链接」（游戏 prefab 复制粘贴残留），
                // 但分别跳 Steam 商店 / 小黑盒 / Bilibili（CollabExternalLinkButton.OpenLink →
                // CollabExternalLinkOpener.Open，见反编译 CollabExternalLinkButton.cs:53-61）。
                // 三个按钮念起来一模一样，盲人无法区分点哪个，必须按平台名分开。
                case "Steam":                     return "Steam 商店页";
                case "Heihe":                     return "小黑盒";
                case "Bilibili":                  return "Bilibili 视频";
            }
            return "";
        }

        internal static string SceneIconLabel(string objectName)
        {
            switch ((objectName ?? "").Trim())
            {
                case "联动内容": return "联动展架";
                case "鬼畜制造机": return "鬼畜制造机";
                case "小游戏": return "小游戏";
                case "音乐": return "音乐播放器";
                case "番茄钟": return "番茄钟";
                case "日程表": return "日程表";
                case "设置": return "系统设置";
                case "装饰": return "换背景";
                case "故事/收藏": return "收藏内容";
                case "换装": return "换装";
                case "隐藏UI": return "隐藏界面";
                case "退出": return "退出游戏";
                case "VolumeButton": return "音量";
                case "Pre": return "上一首";
                case "Play/Pause": return "播放或暂停";
                case "Next": return "下一首";
                case "PlayMode": return "播放模式";
                default: return "";
            }
        }
    }
}
