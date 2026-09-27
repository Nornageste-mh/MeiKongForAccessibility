using System;
using System.Runtime.InteropServices;

namespace MeiKongA11y
{
    // ========================================================================
    // A11yFramework · L1 平台层（**冻结层**，逐作整份复用，不重写）
    //
    // 来源：三个已发布仓库里最完整的一份（《钟塔》BelfryForAccessibility），
    //       并已按《无障碍补丁流水线》§2.2 把对 Plugin.* 的引用改指 A11yHost.*。
    //
    // 逐作使用时**只允许改两处**：
    //   1) namespace —— 由 tools/new_game.py 自动替换，不要手改
    //   2) 本文件里所有 A11yHost.* 成员由契约层（contract/A11yHost.cs）提供，
    //      逐作不需要改这里的代码，只需要在 Plugin.Awake 里把值填进去
    //
    // 下文注释里出现的《钟塔》/《透明的她与真实的我》/《不/存在的你，和我》
    // 是**实测背景的记录**。保留原文是为了不丢掉「这条规则是被哪次事故逼出来的」
    // 这一证据（见流水线 §1.3、§5）。不要因为「看起来像别的项目」而删掉。
    //
    // 纪律：本层**绝不允许**引用任何游戏类型（Assembly-CSharp 里的任何东西）、
    //       任何逐作配置项、Il2CppInterop / Il2CppSystem。
    //       由 tools/compile_check.ps1 编译验证（不引用 Assembly-CSharp）。
    // ========================================================================
    /// <summary>
    /// NVDA Controller Client 封装（Speech 的 NVDA 后端）。
    /// DLL 名称固定为 nvdaControllerClient.dll（官方 x64 构建的文件名）。
    /// 所有函数返回 0 表示成功，非 0 为 Windows 错误码。
    /// </summary>
    internal static class Nvda
    {
        private const string Dll = "nvdaControllerClient.dll";

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        private static extern int nvdaController_testIfRunning();

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText(string text);

        [DllImport(Dll)]
        private static extern int nvdaController_cancelSpeech();

        private static bool _dllOk;
        private static bool _dllProbed;
        private static bool _speakingOk;
        private static float _nextProbe;
        private const float ProbeInterval = 5f;

        /// <summary>DLL 是否成功加载。</summary>
        public static bool DllOk { get { return _dllOk; } }

        /// <summary>把异常暴露出来，避免静默失败。</summary>
        public static string LastError = "";

        /// <summary>探测 DLL 能否加载（幂等）。</summary>
        public static void Probe()
        {
            if (_dllProbed) return;
            _dllProbed = true;
            try
            {
                nvdaController_testIfRunning();
                _dllOk = true;
                LastError = "";
            }
            catch (Exception e)
            {
                _dllOk = false;
                LastError = "DLL 加载失败: " + e.Message;
            }
        }

        /// <summary>NVDA 是否正在运行。供 Speech 挑选后端时调用。</summary>
        public static bool TestRunning()
        {
            Probe();
            if (!_dllOk) return false;
            try
            {
                int rc = nvdaController_testIfRunning();
                if (rc == 0) { _speakingOk = true; LastError = ""; return true; }
                LastError = "NVDA 未运行 (错误码 " + rc + ")";
                return false;
            }
            catch (Exception e)
            {
                _dllOk = false;
                LastError = "调用 NVDA 失败: " + e.Message;
                return false;
            }
        }

        /// <summary>内部就绪检查。NVDA 可能后启动，故 5 秒重试一次。</summary>
        private static bool Ready()
        {
            if (_speakingOk) return true;
            if (UnityEngine.Time.realtimeSinceStartup < _nextProbe) return false;
            _nextProbe = UnityEngine.Time.realtimeSinceStartup + ProbeInterval;
            if (TestRunning())
            {
                A11yHost.Log.LogInfo("已连接到 NVDA。");
                return true;
            }
            return false;
        }

        /// <summary>朗读一段文本。interrupt=true 时先打断上一句。</summary>
        public static void Speak(string text, bool interrupt)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!Ready()) return;

            try
            {
                if (interrupt) nvdaController_cancelSpeech();
                int rc = nvdaController_speakText(text);
                if (rc != 0)
                {
                    // 连接断了（例如 NVDA 退出），下次重新探测
                    _speakingOk = false;
                    _nextProbe = 0f;
                    LastError = "speakText 返回 " + rc;
                }
            }
            catch (Exception e)
            {
                _speakingOk = false;
                _dllOk = false;
                LastError = "speakText 异常: " + e.Message;
            }
        }

        /// <summary>打断当前朗读（例如切到有配音的台词时，避免和语音重叠）。</summary>
        public static void Stop()
        {
            if (!_speakingOk) return;
            try { nvdaController_cancelSpeech(); }
            catch { _speakingOk = false; }
        }
    }
}
