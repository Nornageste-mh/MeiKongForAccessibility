using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;

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
    /// 语音输出抽象层。按可用性依次挑选后端：
    ///
    ///   1. Tolk          —— 若 Tolk.dll 存在。一套接口覆盖
    ///                       NVDA / JAWS / SuperNova / ZoomText / System Access，
    ///                       并可用 SAPI 兜底，同时支持语音与盲文输出。
    ///   2. NVDA          —— nvdaControllerClient.dll，本项目实测可用。
    ///   3. SAPI          —— Windows 内置语音，无需任何读屏软件。
    ///
    /// 之所以自带 NVDA / SAPI 两级而不强依赖 Tolk：Tolk 官方不提供预编译
    /// 二进制，只有 MSVC 工程；而在只装了 NVDA 的机器上，JAWS / SuperNova /
    /// ZoomText 等分支根本无法验证。与其塞一份没测过的互操作代码，不如让
    /// 每一级都可独立工作，Tolk 作为「有则更好」的增强。
    ///
    /// 把 Tolk.dll 及 Tolk 的依赖 DLL（nvdaControllerClient64.dll、SAAPI64.dll）
    /// 放进 BepInEx\plugins\ 即可自动启用。
    /// </summary>
    internal static class Speech
    {
        public enum Backend { None, Tolk, Nvda, Sapi }

        private static Backend _backend = Backend.None;
        private static bool _inited;
        public static string LastError = "";

        public static Backend Current { get { return _backend; } }

        public static string BackendName
        {
            get
            {
                switch (_backend)
                {
                    case Backend.Tolk: return _tolkReader != null ? ("Tolk (" + _tolkReader + ")") : "Tolk";
                    case Backend.Nvda: return "NVDA";
                    case Backend.Sapi: return "SAPI";
                    default: return "无";
                }
            }
        }

        // ================= 初始化 =================

        /// <summary>默认优先级：Tolk → NVDA → SAPI。</summary>
        private static readonly Backend[] Default =
            { Backend.Tolk, Backend.Nvda, Backend.Sapi };

        /// <summary>
        /// 后端优先级。配置「语音后端」填了具体值时只试那一个（排查用）。
        /// log 为 null 时不打印配置写错的告警（重试路径上会刷屏）。
        /// </summary>
        private static Backend[] Order(ManualLogSource log)
        {
            string cfg = A11yHost.CfgSpeechBackend != null ? A11yHost.CfgSpeechBackend.Value : "";
            string t = cfg != null ? cfg.Trim() : "";
            if (t.Length == 0) return Default;

            string u = t.ToUpperInvariant();
            if (u == "自动" || u == "AUTO") return Default;

            Backend forced;
            if (ParseBackend(u, out forced)) return new[] { forced };

            if (log != null)
                log.LogWarning("配置「语音后端」的值认不出来：" + t
                    + "（可用：自动 / Tolk / NVDA / SAPI），已按「自动」处理。");
            return Default;
        }

        private static bool ParseBackend(string upper, out Backend b)
        {
            b = Backend.None;
            if (upper == null) return false;
            if (upper.Contains("TOLK")) { b = Backend.Tolk; return true; }
            if (upper.Contains("NVDA")) { b = Backend.Nvda; return true; }
            if (upper.Contains("SAPI")) { b = Backend.Sapi; return true; }
            return false;
        }

        private static bool TryBackend(Backend b, ManualLogSource log)
        {
            switch (b)
            {
                case Backend.Tolk: return TryTolk(log);
                case Backend.Nvda: return TryNvda(log);
                case Backend.Sapi: return TrySapi(log);
            }
            return false;
        }

        /// <summary>把每个后端的结论串起来，日志里一眼能看出「为什么全灭」。</summary>
        private static string Verdict(Backend b, string why)
        {
            string name;
            switch (b)
            {
                case Backend.Tolk: name = "Tolk"; break;
                case Backend.Nvda: name = "NVDA"; break;
                case Backend.Sapi: name = "SAPI"; break;
                default: name = "?"; break;
            }
            return name + "：" + (string.IsNullOrEmpty(why) ? "不可用" : why) + "；";
        }

        public static void Init(ManualLogSource log)
        {
            if (_inited) return;
            _inited = true;

            var tried = new System.Text.StringBuilder();
            foreach (Backend b in Order(log))
            {
                LastError = "";
                if (!TryBackend(b, log)) { tried.Append(Verdict(b, LastError)); continue; }
                _backend = b;
                return;
            }

            _backend = Backend.None;
            log.LogWarning("没有可用的语音后端。逐个结论：" + tried);
        }

        /// <summary>定期重试：NVDA 可能后启动，Tolk 也可能中途可用。</summary>
        public static bool Ready()
        {
            if (_backend != Backend.None) return true;
            if (UnityEngine.Time.realtimeSinceStartup < _nextRetry) return false;
            _nextRetry = UnityEngine.Time.realtimeSinceStartup + RetryInterval;

            _inited = false;
            Init(A11yHost.Log);
            return _backend != Backend.None;
        }

        private static float _nextRetry;
        private const float RetryInterval = 10f;

        // ================= Tolk =================

        private const string TolkDll = "Tolk.dll";

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Load();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_IsLoaded();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Unload();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySAPI);

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern IntPtr Tolk_DetectScreenReader();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_HasSpeech();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_HasBraille();

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_Output(string str, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport(TolkDll, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool Tolk_Silence();

        private static bool _tolkLoaded;
        private static string _tolkReader;
        private static bool _tolkHasBraille;

        public static bool TolkHasBraille { get { return _tolkHasBraille; } }

        private static bool TryTolk(ManualLogSource log)
        {
            try
            {
                Tolk_TrySAPI(true);   // 无读屏时用 SAPI 兜底
                Tolk_Load();
                if (!Tolk_IsLoaded()) return false;

                _tolkLoaded = true;

                IntPtr p = Tolk_DetectScreenReader();
                _tolkReader = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                _tolkHasBraille = Tolk_HasBraille();

                log.LogInfo("语音后端: Tolk，读屏=" + (_tolkReader ?? "(SAPI 兜底)")
                    + (_tolkHasBraille ? "，支持盲文" : ""));
                return true;
            }
            catch (Exception e)
            {
                LastError = "Tolk 不可用: " + e.Message;
                return false;
            }
        }

        // ================= NVDA =================

        private static bool TryNvda(ManualLogSource log)
        {
            try
            {
                if (!Nvda.DllOk) Nvda.Probe();
                if (!Nvda.DllOk)
                {
                    LastError = Nvda.LastError;
                    return false;
                }
                if (!Nvda.TestRunning())
                {
                    LastError = Nvda.LastError;
                    return false;
                }
                log.LogInfo("语音后端: NVDA");
                return true;
            }
            catch (Exception e)
            {
                LastError = "NVDA 不可用: " + e.Message;
                return false;
            }
        }

        // ================= SAPI =================

        /// <summary>
        /// 交给 Sapi.cs（纯 P/Invoke + vtable）。
        ///
        /// v0.5.x 这里走的是 `Type.GetTypeFromProgID` + `InvokeMember` 的 COM 后期
        /// 绑定，而 Unity 的 Mono **没有实现**它 —— 玩家机器上的日志：
        ///     SAPI 不可用: NotImplementedException: The method or operation is not implemented.
        /// 也就是说「SAPI 兜底」从来没出过声：只装了争渡（或什么读屏都没装）的
        /// 机器上后端全灭，表现就是整局游戏一片安静。v0.5.8a 修复。
        /// </summary>
        private static bool TrySapi(ManualLogSource log)
        {
            try
            {
                if (!Sapi.TryInit(log)) { LastError = Sapi.LastError; return false; }
                return true;
            }
            catch (Exception e)
            {
                LastError = "SAPI 不可用: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        // ================= 对外接口 =================

        public static void Speak(string text, bool interrupt)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 后端不可用时定期重试（NVDA 可能后启动，或读屏中途被打开）
            if (_backend == Backend.None && !Ready()) return;

            // 诊断：把「本该念出去的东西」也写进日志。
            // 实机验证时读屏到底念了什么，靠耳朵复核成本太高，
            // 打开「界面诊断日志」就能在 LogOutput.log 里逐条对照。
            try
            {
                if (A11yHost.CfgDiagLog != null && A11yHost.CfgDiagLog.Value && A11yHost.Log != null)
                    A11yHost.Log.LogInfo("[朗读] " + text);
            }
            catch { }

            try
            {
                switch (_backend)
                {
                    case Backend.Tolk:
                        // Tolk_Output 同时走语音与盲文
                        Tolk_Output(text, interrupt);
                        break;

                    case Backend.Nvda:
                        Nvda.Speak(text, interrupt);
                        break;

                    case Backend.Sapi:
                        Sapi.Speak(text, interrupt);
                        break;
                }
            }
            catch (Exception e)
            {
                LastError = "朗读失败: " + e.Message;
                _backend = Backend.None;
                _inited = false;
            }
        }

        public static void Stop()
        {
            if (_backend == Backend.None) return;
            try
            {
                switch (_backend)
                {
                    case Backend.Tolk:
                        Tolk_Silence();
                        break;
                    case Backend.Nvda:
                        Nvda.Stop();
                        break;
                    case Backend.Sapi:
                        Sapi.Stop();
                        break;
                }
            }
            catch
            {
                _backend = Backend.None;
                _inited = false;
            }
        }

        public static void Shutdown()
        {
            try { if (_tolkLoaded) Tolk_Unload(); } catch { }
            _tolkLoaded = false;
            try { Sapi.Shutdown(); } catch { }
        }
    }
}
