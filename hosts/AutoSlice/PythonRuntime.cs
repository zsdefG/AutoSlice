using System.IO;
using Microsoft.Win32;
using Python.Runtime;

namespace AutoSlice;

/// <summary>
/// pythonnet 引擎单例（精简版）：定位 python DLL → 初始化引擎 → 提供在 GIL 内执行脚本的能力。
/// 供下载后钩子（PostScript）使用；找不到 Python 时静默跳过，不阻塞下载主流程。
/// </summary>
public static class PythonRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static IntPtr _threadState;

    public static bool IsReady
    {
        get { lock (Gate) return _initialized; }
    }

    /// <summary>
    /// 随应用发布的嵌入式 Python 运行时目录：{BaseDir}\runtime\python\（含 python3xx.dll、Lib\、DLLs\）。
    /// 打包脚本 pack-runtime.ps1 在构建时从构建机 Python 复制而来，使目标机器无需安装 Python。
    /// </summary>
    private static (string Dll, string Home)? TryLocateBundledRuntime()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "runtime", "python");
        if (!Directory.Exists(root)) return null;

        // python3.dll 是稳定 ABI 桥，不是解释器本体；只认 python3xx.dll（如 python312.dll）
        string? dll = null;
        try
        {
            foreach (var f in Directory.GetFiles(root, "python3*.dll"))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^python3\d+\.dll$"))
                { dll = f; break; }
            }
        }
        catch (Exception) { return null; }

        if (dll == null || !Directory.Exists(Path.Combine(root, "Lib"))) return null;
        return (dll, root);
    }

    /// <summary>
    /// 禁止 CPython 在源文件旁生成 __pycache__\*.pyc：安装目录往往可写，保持安装目录干净。
    /// 必须在 PythonEngine.Initialize() 之前调用。
    /// </summary>
    private static void DisableBytecodeCache()
    {
        Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");
    }

    /// <summary>尝试定位 Python DLL（pythonnet 的 Runtime.PythonDLL 必须是完整路径）。</summary>
    private static string? LocatePythonDll()
    {
        // 1) 环境变量显式指定
        var env = Environment.GetEnvironmentVariable("PYTHONNET_PYDLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return env;

        // 2) 注册表 InstallPath（pythonnet 3.x 支持 3.10-3.14）
        foreach (var ver in new[] { "3.12", "3.11", "3.13", "3.14", "3.10" })
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var k = hive.OpenSubKey($@"SOFTWARE\Python\PythonCore\{ver}\InstallPath");
                    if (k?.GetValue("") is string dir && !string.IsNullOrEmpty(dir))
                    {
                        var dll = Path.Combine(dir, $"python{ver.Replace(".", "")}.dll");
                        if (File.Exists(dll))
                            return dll;
                    }
                }
                catch (Exception)
                {
                    // 该注册表项不存在或无权限，继续下一个
                }
            }
        }
        return null;
    }

    /// <summary>初始化引擎（幂等）。失败返回 false 并给出原因（调用方自行决定是否报告）。</summary>
    public static bool EnsureInitialized(out string? failReason)
    {
        lock (Gate)
        {
            failReason = null;
            if (_initialized) return true;

            try
            {
                // 优先使用随应用发布的嵌入式运行时（目标机器无需安装 Python）
                var bundled = TryLocateBundledRuntime();
                if (bundled != null)
                {
                    Runtime.PythonDLL = bundled.Value.Dll;
                    // 嵌入式运行时的标准库 Lib\ 与扩展 DLLs\ 都在 Home 下，必须显式指定 PYTHONHOME，
                    // 否则 CPython 会去注册表/默认路径找标准库而失败。
                    Environment.SetEnvironmentVariable("PYTHONHOME", bundled.Value.Home);
                    DisableBytecodeCache();
                    PythonEngine.Initialize();
                    _threadState = PythonEngine.BeginAllowThreads();
                    _initialized = true;
                    return true;
                }

                var dll = LocatePythonDll();
                if (dll == null)
                {
                    failReason = "未找到 Python 3.10-3.14 运行库（python*.dll），下载后脚本不可用";
                    return false;
                }

                Runtime.PythonDLL = dll;
                DisableBytecodeCache();
                PythonEngine.Initialize();
                _threadState = PythonEngine.BeginAllowThreads();
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                failReason = "Python 引擎初始化失败: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>退出时清理引擎（幂等、尽力而为；不清除也不阻塞程序退出）。</summary>
    public static void ShutdownIfNeeded()
    {
        lock (Gate)
        {
            if (!_initialized) return;
            try
            {
                if (_threadState != IntPtr.Zero)
                {
                    try { PythonEngine.EndAllowThreads(_threadState); } catch (Exception) { }
                }
                PythonEngine.Shutdown();
            }
            catch (Exception)
            {
                // 引擎关闭失败不阻塞程序退出
            }
            _initialized = false;
        }
    }
}