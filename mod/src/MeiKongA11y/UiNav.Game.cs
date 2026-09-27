        /// <summary>
        /// 设置面板里那些「美术字标签」的对象名 → 中文。
        ///
        /// 《钟塔》里一批控件的文字是**画在图片上**的，TMP 里一个字都没有，
        /// 所以只能退回对象名 —— 而对象名是 Gallery / Options 这种。
        /// 这里按对象名给出中文。每一条都要有依据，不要凭感觉往里加：
        ///
        ///   · 标题旋转菜单的五个按钮，名字与动作的对应是从
        ///     ClockwiseMenuController.Awake 里实查的：
        ///       SetButtonAction(0, handler.QuitGame)
        ///       SetButtonAction(1, handler.OpenSettings)
        ///       SetButtonAction(2, handler.StartGame)
        ///       SetButtonAction(3, handler.LoadSave)
        ///       SetButtonAction(4, handler.OpenGallery)
        ///     对象名则是 probe 的场景树转储里实查的：
        ///       Canvas/Panel/Background/Menu/Buttons 下依次是
        ///       Gallery / Load / Start / Options / Exit。
        ///
        ///   · 画廊页签名（CG / 视频 / 音乐 / 语音）本身是 TMP 文字，
        ///     不需要别名 —— 别名只在控件子树里**一个字都没有**时才会被用到
        ///     （见 TextOf），所以不会盖掉正常按钮的朗读。
        /// </summary>
        private static readonly Dictionary<string, string> NameAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ---- 标题旋转菜单（纯图片按钮）----
            { "Start",   "开始游戏" },
            { "Load",    "读取存档" },
            { "Gallery", "画廊" },
            { "Options", "系统设置" },
            { "Exit",    "退出游戏" },

            // ---- 通用确认框（文字画在图片上）----
            { "Yes",     "确定" },
            { "No",      "取消" },
            { "Confirm", "确定" },
            { "Cancel",  "取消" },
        };

        // ================= 姓名输入框 =================
        //
        // level2（开局输入姓名）的两个输入框在场景里**没有任何标签文字**：
        // 行名画在图片上，TMP 里读不到；对象名一个叫 FirstText（InputField）、
        // 另一个叫 Third —— 后者尤其看不出是「名」。
        //
        // 唯一可靠的依据是游戏自己的 NameInputManagerTMP（反编译）：
        //     public TMP_InputField surnameField1;   ← 姓
        //     public TMP_InputField nameField1;      ← 名
        // Unity 按声明顺序序列化，解析 level2 的组件字节确认这两个字段
        // 分别指向场景里 FirstText（InputField）和 Third（恰好就是仅有的
        // 两个激活输入框，Second / Fourth 未激活）。
        //
        // 这里用反射读，读不到就一路回退（占位提示 → 同行标签 → 对象名），
        // 不让插件因为游戏改版而失效。

        private static Type _nameMgrType;
        private static bool _nameMgrProbed;
        private static FieldInfo _surnameField;
        private static FieldInfo _nameField;

        private static string InputFieldRoleName(TMP_InputField inf)
        {
            try
            {
                if (!_nameMgrProbed)
                {
                    _nameMgrProbed = true;
                    _nameMgrType = Type.GetType("NameInputManagerTMP, Assembly-CSharp");
                    if (_nameMgrType == null)
                    {
                        // 游戏程序集名万一不是 Assembly-CSharp，就遍历已加载程序集
                        Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                        for (int i = 0; i < all.Length && _nameMgrType == null; i++)
                        {
                            try { _nameMgrType = all[i].GetType("NameInputManagerTMP", false); }
                            catch { }
                        }
                    }
                    if (_nameMgrType != null)
                    {
                        _surnameField = _nameMgrType.GetField("surnameField1");
                        _nameField = _nameMgrType.GetField("nameField1");
                    }
                }
                if (_nameMgrType == null || _surnameField == null || _nameField == null) return "";

                int id = inf.GetInstanceID();
                string cached;
                if (_inputRoles.TryGetValue(id, out cached)) return cached;

                string role = "";
                UnityEngine.Object[] found = Resources.FindObjectsOfTypeAll(_nameMgrType);
                for (int i = 0; i < found.Length && role.Length == 0; i++)
                {
                    Component c = found[i] as Component;
                    if (c == null) continue;
                    if (ReferenceEquals(_surnameField.GetValue(c), inf)) role = "姓氏";
                    else if (ReferenceEquals(_nameField.GetValue(c), inf)) role = "名字";
                }
                _inputRoles[id] = role;
                return role;
            }
            catch { return ""; }
        }