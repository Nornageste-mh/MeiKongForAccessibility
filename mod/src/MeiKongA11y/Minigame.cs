using System;
using System.Collections.Generic;
using System.Text;
using ShiMeng.Minigames.MemoryMatch;
using UnityEngine;
using UnityEngine.UI;

namespace MeiKongA11y
{
    /// <summary>
    /// 记忆翻牌（本作唯一的小游戏）的无障碍适配。
    ///
    /// === 这个游戏对盲人难在哪 ===
    ///
    /// 4×4 共 16 张牌，**牌面是纯图片**、8 种图案、没有任何文字；
    /// 核心玩法就是「记住翻开过什么」——这恰恰是读屏最帮不上忙的地方。
    /// 所以本层的立场是**只做等价呈现，不做记忆外挂**（玩家明确要求「不做笔记键」）：
    ///
    ///   给：牌阵行列、当前光标位置、**此刻屏幕上看得见的状态**（背面 / 已翻开 / 已配对）、
    ///       已翻开牌面的图案名、比分、轮到谁
    ///   不给：已经盖回去的牌是什么 —— **这就是游戏本身**
    ///
    /// === 8 种图案的名字是怎么来的 ===
    ///
    /// 牌面的 sprite 名是 **Photoshop 图层名**（`图层 1617` / `图层 1617-1` … / `图层 1618`），
    /// 一个字的有用信息都没有。所以我把 8 张牌面从运行时导出成 PNG，**逐张看过**之后再起的名。
    /// 对应关系用 `pairId`（= `pairCardPrefabs` 的下标），由
    /// `MemoryMatchBoardModel.GetPairId(cardIndex)` 这个**游戏公开 API** 取到，不靠猜。
    ///
    /// 命名原则：**短、互相区分得开、和「正常人用来分辨两张牌是不是同一张」的依据一致**。
    /// 不做文学性描述 —— 名字的作用是让玩家判断「这两张是不是一对」。
    /// </summary>
    internal static class Minigame
    {
        /// <summary>
        /// pairId → 中文名。下标即 pairId（= `pairCardPrefabs` 的下标），
        /// 依据：把 8 张牌面导出 PNG 后逐张查看（见 probe 的 `ExportCardFaces`）。
        /// </summary>
        private static readonly string[] PairNames =
        {
            "浴巾",        // 0  图层 1617    粉发、黄色浴巾、脸红
            "麦克风",      // 1  图层 1617-1  粉发校服、手持麦克风
            "黑发麦克风",  // 2  图层 1617-2  黑发紫瞳、手持麦克风
            "相拥",        // 3  图层 1617-3  粉发校服、被人从旁抱住
            "女仆装",      // 4  图层 1617-4  棕发紫瞳、黑裙白围裙
            "两人合影",    // 5  图层 1617-5  黑发与粉发两人同框
            "金色舞台",    // 6  图层 1617-6  金发蓝瞳、舞台灯光
            "抱猫",        // 7  图层 1618    黑发、怀抱猫、木架暖光
        };

        private static string PairName(int pairId)
        {
            if (pairId >= 0 && pairId < PairNames.Length) return PairNames[pairId];
            return "图案 " + (pairId + 1);
        }

        // ==================================================================
        // 状态
        // ==================================================================
        private static MemoryMatchGameController _ctl;
        private static readonly List<MemoryMatchCardView> _cards = new List<MemoryMatchCardView>();
        private static int _row, _col, _rows, _cols;
        private static bool _announcedStart;
        private static int _lastPlayerScore = -1, _lastCpuScore = -1;
        private static MemoryMatchSide _lastTurn;
        private static string _lastStateKey = "";
        private static float _nextPoll;

        /// <summary>小游戏面板开着、而且牌已经铺好了，才算「在玩」。</summary>
        internal static bool IsActive()
        {
            try
            {
                if (_ctl == null) _ctl = UnityEngine.Object.FindObjectOfType<MemoryMatchGameController>(true);
                if (_ctl == null) return false;
                if (!_ctl.gameObject.activeInHierarchy) return false;
                var cg = _ctl.GetComponent<CanvasGroup>();
                if (cg != null && cg.alpha < 0.5f) return false;
                return true;
            }
            catch { return false; }
        }

        // ==================================================================
        // 每帧（小游戏期间由 Plugin 的 TickUiNav 调进来，UiNav 让位）
        // ==================================================================
        internal static void Update()
        {
            try
            {
                if (Time.realtimeSinceStartup < _nextPoll) { HandleKeys(); return; }
                _nextPoll = Time.realtimeSinceStartup + 0.25f;

                RefreshCards();
                if (_cards.Count == 0) { _announcedStart = false; return; }

                if (!_announcedStart)
                {
                    _announcedStart = true;
                    _row = _col = 0;
                    Announcer.Auto("记忆翻牌开始。" + _rows + " 行 " + _cols + " 列，共 " + _cards.Count + " 张牌。"
                                   + "方向键移动，回车翻牌。现在在第 1 行第 1 列，"
                                   + StateWord(CardAt(_row, _col)) + "。");
                    _lastTurn = Turn;
                    _lastPlayerScore = PlayerScore; _lastCpuScore = CpuScore;
                    return;
                }

                WatchBoard();        // 牌面状态迁移 —— 这是「及时」的核心
                AnnounceIfChanged();
                HandleKeys();
            }
            catch (Exception e) { A11yHost.Diag("[小游戏] Update 异常: " + e.Message); }
        }

        // ==================================================================
        // 牌面状态迁移播报 —— **这一层是「及时」的关键**
        //
        // 正常人玩这个游戏，靠的是「看到牌翻开的那一瞬间」：
        //   · 自己翻的牌是什么
        //   · 诗萌翻的两张是什么（她翻的时候他也在看）
        //   · 这一对成不成、盖回去没有
        // 这些全是**抬眼就看到的**，所以播报它们不算作弊；
        // 而不播报，盲人就等于闭着眼睛在玩。
        //
        // 做法：每帧比对 16 张牌的 `MemoryMatchCardState`（16 次枚举读取，开销可忽略），
        // 只对**发生变化**的那几张说话。用 `Announcer.Soon()` 而不是 `Auto()`：
        // 这些信息过几秒就作废，最多等配音 2.5 秒，宁可轻微重叠也不能迟到。
        // ==================================================================
        private static readonly MemoryMatchCardState[] _prevState = new MemoryMatchCardState[16];
        private static readonly bool[] _prevValid = new bool[16];

        private static void WatchBoard()
        {
            var b = Board;
            if (b == null) return;
            var who = Turn == MemoryMatchSide.Player ? "你" : "诗萌";
            var flipped = new List<string>();   // 「第 X 行第 Y 列：图案」
            var matched = new List<string>();
            int hidden = 0;                     // 翻回去的张数

            for (int i = 0; i < 16; i++)
            {
                MemoryMatchCardState now;
                try { now = b.GetState(i); } catch { continue; }
                if (!_prevValid[i]) { _prevValid[i] = true; _prevState[i] = now; continue; }
                var was = _prevState[i];
                if (now == was) continue;
                _prevState[i] = now;

                if (now == MemoryMatchCardState.FaceUp)
                    flipped.Add(PosText(i) + "：" + PairName(b.GetPairId(i)));
                else if (now == MemoryMatchCardState.Matched)
                    matched.Add(PairName(b.GetPairId(i)));
                else if (now == MemoryMatchCardState.FaceDown && was == MemoryMatchCardState.FaceUp)
                    hidden++;
            }

            // 同一拍里的迁移合成**一句话**：两张牌各报一次「不是一对」是纯噪音
            // （玩家刚刚才听过那两张是什么，他要的只是「成了没有」）。
            if (flipped.Count == 1) Announcer.Soon(who + "翻开" + flipped[0] + "。");
            else if (flipped.Count > 1) Announcer.Soon(who + "翻开 " + string.Join("、", flipped) + "。");

            if (matched.Count > 0) Announcer.Soon("配对成功，" + string.Join("、", matched) + "。");
            else if (hidden > 1) Announcer.Soon("没配上，两张都盖回去了。");
            else if (hidden == 1) Announcer.Soon("没配上，盖回去一张。");
        }

        /// <summary>牌索引 → 「第 X 行第 Y 列」。行列来自按屏幕位置算出的映射，不是假设。</summary>
        private static string PosText(int cardIndex)
        {
            foreach (var c in _cards)
            {
                if (c == null || c.Index != cardIndex) continue;
                int id = c.GetInstanceID();
                int r, col;
                if (_rowOf.TryGetValue(id, out r) && _colOf.TryGetValue(id, out col))
                    return "第 " + (r + 1) + " 行第 " + (col + 1) + " 列";
            }
            return "第 " + (cardIndex + 1) + " 张";
        }

        /// <summary>回合 / 比分变化时主动播报（这些正常人抬眼就能看到）。</summary>
        private static void AnnounceIfChanged()
        {
            int ps = PlayerScore, cs = CpuScore;
            var turn = Turn;
            if (ps != _lastPlayerScore || cs != _lastCpuScore)
            {
                _lastPlayerScore = ps; _lastCpuScore = cs;
                Announcer.Soon("比分，哥哥 " + ps + " 分，诗萌 " + cs + " 分。", 3f);
            }
            if (turn != _lastTurn)
            {
                _lastTurn = turn;
                Announcer.Soon(turn == MemoryMatchSide.Player ? "轮到你了。" : "轮到诗萌了，等她翻。", 3f);
            }
        }

        private static void HandleKeys()
        {
            // 返回键先问 —— 它一旦吃下这一帧的输入，后面的方向键/回车就不该再当回事。
            // （本作运行时没有 UIManager 实例，游戏自己不看 ESC，所以这里是唯一的裁决点；
            //   万一将来有了，UIManager 的前置钩子会先问，帧内去重保证只处理一次。）
            InterceptBackInput();

            int dx = 0, dy = 0;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) dx = -1;
            else if (Input.GetKeyDown(KeyCode.RightArrow)) dx = 1;
            else if (Input.GetKeyDown(KeyCode.UpArrow)) dy = -1;
            else if (Input.GetKeyDown(KeyCode.DownArrow)) dy = 1;

            if (dx != 0 || dy != 0) { Move(dx, dy); return; }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                FlipCurrent();
        }

        /// <summary>方向键在网格上走 —— 这是玩家选的交互模型（与画面布局一一对应）。</summary>
        internal static void Move(int dx, int dy)
        {
            int r = Mathf.Clamp(_row + dy, 0, Mathf.Max(0, _rows - 1));
            int c = Mathf.Clamp(_col + dx, 0, Mathf.Max(0, _cols - 1));
            if (r == _row && c == _col) { Repeat.Say("到头了。", false); return; }
            _row = r; _col = c;
            var card = CardAt(_row, _col);
            Repeat.Say("第 " + (_row + 1) + " 行第 " + (_col + 1) + " 列，" + StateWord(card) + "。", true);
        }

        /// <summary>回车翻当前这张牌 —— 走游戏自己的 `OnCardClicked(index)`，等价于用鼠标点它。</summary>
        internal static void FlipCurrent()
        {
            try
            {
                var card = CardAt(_row, _col);
                if (card == null) { Repeat.Say("这里没有牌。", true); return; }
                int idx = card.Index;
                var state = BoardState(idx);
                if (state != MemoryMatchCardState.FaceDown)
                {
                    Repeat.Say(state == MemoryMatchCardState.Matched ? "这张已经配对过了。" : "这张已经翻开了。", true);
                    return;
                }
                if (_ctl != null) _ctl.OnCardClicked(idx);
                A11yHost.Diag("[小游戏] OnCardClicked(" + idx + ")");
            }
            catch (Exception e) { A11yHost.Diag("[小游戏] FlipCurrent 异常: " + e.Message); }
        }

// ==================================================================
        // 4) 返回键：按 ESC 关闭小游戏窗口（两段缓冲，防误触）
        //
        // 为什么这件事得我们自己干：小游戏面板由**桌宠面板系统**
        // （PetUiIntegrationHub → PetPanelRegistry）开关，**不在** UIManager 的返回栈上；
        // 而探针实查运行时场景里连 UIManager 实例都没有 —— 游戏自己对 ESC 毫无反应。
        // 正常人是用鼠标点窗口的关闭按钮，那按钮走的正是 hub.CloseMinigame()，我们叫同一个。
        //
        // 为什么要按两次：小游戏一开局就作废、没有存档点，误触一次就是白玩一局。
        // 所以第一次按只「举起来」并说清楚，再按一次才真关，中途按别的键就撤。
        // 这与 UiNav「退出前二次确认」是同一套交互模型（0.1.0.0 起就在用），
        // 读屏用户不必再学一套新的。
        // ==================================================================

        private static bool _escArmed;

        // 帧内去重：Plugin 的输入分发与 UIManager 的前置钩子都会问同一件事，
        // 但**帧序不可假设**，所以谁先问谁干活，第二个拿结论就好 —— 不能念两遍、更不能关两次。
        private static int _askedFrame = -1;
        private static bool _askedVerdict;

        /// <summary>
        /// 裁决「这一帧的返回输入要不要交给游戏」。返回 true = 我们吃下了，游戏不要再看。
        /// 小游戏窗口没开时永远返回 false（不动游戏一丝一毫）。
        /// </summary>
        internal static bool InterceptBackInput()
        {
            if (_askedFrame == Time.frameCount) return _askedVerdict;
            _askedFrame = Time.frameCount;
            bool consumed = false;
            try
            {
                if (IsActive()) consumed = HandleBackInput();
                else _escArmed = false;          // 窗口已经关了，缓冲自然作废
            }
            catch (Exception e) { A11yHost.Diag("[小游戏] 返回键异常: " + e.Message); }
            _askedVerdict = consumed;
            return consumed;
        }

        private static bool HandleBackInput()
        {
            if (Plugin.KeyDown(Plugin.CfgClosePanelKey))
            {
                bool confirm = Plugin.CfgClosePanelConfirm == null || Plugin.CfgClosePanelConfirm.Value;
                if (!confirm)
                {
                    Repeat.Say("正在关闭小游戏窗口。", true);
                    CloseWindow();
                    return true;
                }
                if (!_escArmed)
                {
                    _escArmed = true;
                    Repeat.Say("再按一次" + CloseKeyName() + "关闭小游戏窗口，按下其它任意键取消缓冲。", true);
                    return true;
                }
                _escArmed = false;
                Repeat.Say("正在关闭小游戏窗口。", true);
                CloseWindow();
                return true;
            }

            // 「其它任意键」用 Input.anyKeyDown：它**包含鼠标左右中键**，
            // 所以缓冲举着的时候右键点一下也当取消 —— 不会绕过确认把关掉窗口，
            // 也不会出现「按了键但没反应」的沉默状态。
            if (_escArmed && Input.anyKeyDown)
            {
                _escArmed = false;
                Repeat.Say("已取消缓冲。", false);
                return true;
            }
            return false;
        }

        private static string CloseKeyName()
        {
            string s = Plugin.CfgClosePanelKey != null ? Plugin.CfgClosePanelKey.Value : "Escape";
            if (string.IsNullOrEmpty(s)) return "ESC";
            s = s.Trim();
            if (s.Equals("Escape", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Esc", StringComparison.OrdinalIgnoreCase)) return "ESC";
            return s;
        }

        /// <summary>
        /// 关窗：叫游戏自己的 <c>PetUiIntegrationHub.CloseMinigame()</c> ——
        /// 也就是窗口关闭按钮上挂的那个方法（它顺带做 StopAndReset，和鼠标点关闭完全等价）。
        /// 拿不到 Hub 才退化为直接关面板。
        /// </summary>
        private static void CloseWindow()
        {
            try
            {
                var hub = UnityEngine.Object.FindObjectOfType<PetUiIntegrationHub>(true);
                if (hub != null)
                {
                    hub.CloseMinigame();
                    A11yHost.Diag("[小游戏] 已调用 PetUiIntegrationHub.CloseMinigame()");
                    return;
                }
                A11yHost.Diag("[小游戏] 找不到 PetUiIntegrationHub，退化为直接关面板");
            }
            catch (Exception e) { A11yHost.Diag("[小游戏] CloseMinigame 异常: " + e.Message); }
            Surfaces.ClosePanel("minigame");
        }

        // ==================================================================
        // 牌阵几何：**按牌的真实屏幕位置算行列**，不假设布局
        // ==================================================================
        private static void RefreshCards()
        {
            _cards.Clear();
            var all = UnityEngine.Object.FindObjectsOfType<MemoryMatchCardView>(true);
            foreach (var c in all)
            {
                if (c == null || !c.gameObject.activeInHierarchy) continue;
                _cards.Add(c);
            }
            if (_cards.Count == 0) return;

            // 行：按屏幕 y 聚类；列：同一行内按 x 排序
            var sorted = new List<MemoryMatchCardView>(_cards);
            sorted.Sort((a, b) =>
            {
                float ay = ScreenPos(a).y, by = ScreenPos(b).y;
                int ai = Mathf.RoundToInt(ay / 8f), bi = Mathf.RoundToInt(by / 8f);
                if (ai != bi) return bi.CompareTo(ai);         // 先上后下
                return ScreenPos(a).x.CompareTo(ScreenPos(b).x); // 同行先左后右
            });

            var rowOfCard = new Dictionary<int, int>();
            var colOfCard = new Dictionary<int, int>();
            int rows = 0, cols = 0;
            int i = 0;
            while (i < sorted.Count)
            {
                float y0 = ScreenPos(sorted[i]).y;
                var line = new List<MemoryMatchCardView>();
                while (i < sorted.Count && Mathf.Abs(ScreenPos(sorted[i]).y - y0) <= 8f) line.Add(sorted[i++]);
                for (int c = 0; c < line.Count; c++)
                {
                    rowOfCard[line[c].GetInstanceID()] = rows;
                    colOfCard[line[c].GetInstanceID()] = c;
                }
                cols = Mathf.Max(cols, line.Count);
                rows++;
            }
            _rows = rows; _cols = cols;
            _row = Mathf.Clamp(_row, 0, Mathf.Max(0, _rows - 1));
            _col = Mathf.Clamp(_col, 0, Mathf.Max(0, _cols - 1));
            _rowOf = rowOfCard; _colOf = colOfCard;
        }

        private static Dictionary<int, int> _rowOf = new Dictionary<int, int>();
        private static Dictionary<int, int> _colOf = new Dictionary<int, int>();

        private static Vector3 ScreenPos(MemoryMatchCardView c)
        {
            try
            {
                var rt = c.transform as RectTransform;
                if (rt == null) return c.transform.position;
                var cam = (c.GetComponentInParent<Canvas>() is Canvas cv && cv.renderMode != RenderMode.ScreenSpaceOverlay)
                          ? cv.worldCamera : null;
                return cam != null ? cam.WorldToScreenPoint(rt.position) : rt.position;
            }
            catch { return c.transform.position; }
        }

        private static MemoryMatchCardView CardAt(int row, int col)
        {
            foreach (var c in _cards)
            {
                int id = c.GetInstanceID();
                if (_rowOf.TryGetValue(id, out var r) && _colOf.TryGetValue(id, out var cc) && r == row && cc == col)
                    return c;
            }
            return null;
        }

        // ==================================================================
        // 读状态：牌面 / 比分 / 回合 —— 全部来自游戏自己的公开 API
        // ==================================================================
        private static MemoryMatchBoardModel Board
        {
            get { try { return _ctl != null && _ctl.Flow != null ? _ctl.Flow.Board : null; } catch { return null; } }
        }

        private static MemoryMatchCardState BoardState(int cardIndex)
        {
            try { var b = Board; if (b != null) return b.GetState(cardIndex); } catch { }
            return MemoryMatchCardState.FaceDown;
        }

        private static int PlayerScore
        {
            get { try { return _ctl.Flow.Session.PlayerScore; } catch { return 0; } }
        }

        private static int CpuScore
        {
            get { try { return _ctl.Flow.Session.CpuScore; } catch { return 0; } }
        }

        private static MemoryMatchSide Turn
        {
            get { try { return _ctl.Flow.Session.CurrentTurn; } catch { return MemoryMatchSide.Player; } }
        }

        /// <summary>
        /// 一张牌此刻「看得见」的说法。**只报此刻的状态**：
        /// 背面就只说背面，绝不透露它是什么 —— 那正是这个游戏要玩家自己记的东西。
        /// </summary>
        internal static string StateWord(MemoryMatchCardView card)
        {
            if (card == null) return "没有牌";
            try
            {
                var st = BoardState(card.Index);
                if (st == MemoryMatchCardState.FaceDown) return "背面";
                int pairId = -1;
                var b = Board;
                if (b != null) pairId = b.GetPairId(card.Index);
                return (st == MemoryMatchCardState.Matched ? "已配对，" : "已翻开，") + PairName(pairId);
            }
            catch { return "状态未知"; }
        }

        /// <summary>给 UiNav 用的标签（万一玩家用 Tab 遍历而不是网格）。</summary>
        internal static string CardLabel(MemoryMatchCardView card)
        {
            try
            {
                int id = card.GetInstanceID();
                int r, c;
                if (!_rowOf.TryGetValue(id, out r) || !_colOf.TryGetValue(id, out c))
                { RefreshCards(); _rowOf.TryGetValue(id, out r); _colOf.TryGetValue(id, out c); }
                return "第 " + (r + 1) + " 行第 " + (c + 1) + " 列，" + StateWord(card);
            }
            catch { return StateWord(card); }
        }

        internal static void Reset()
        {
            _announcedStart = false;
            _escArmed = false;
            _cards.Clear();
            for (int i = 0; i < _prevValid.Length; i++) _prevValid[i] = false;
            _lastPlayerScore = _lastCpuScore = -1;
        }
    }
}
