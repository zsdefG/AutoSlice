using System.Globalization;
using System.IO;
using Python.Runtime;

namespace AutoSlice;

/// <summary>
/// 下载后钩子：若 %LOCALAPPDATA%\AutoSlice\post_download.py 存在，在每次下载完成（含失败）后
/// 用嵌入式 Python 执行它，argv = [脚本路径, url, 本地路径, "1"/"0"（成功/失败）, 耗时秒]。
/// 钩子存在只是用户自定义能力，任何时候失败都只记日志，绝不中断主流程。
/// </summary>
public static class PostScript
{
    /// <summary>日志出口（UI 注册；回调里禁止再进入 Python）。</summary>
    public static Action<string>? Log;

    public static string HookPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSlice");
        return Path.Combine(dir, "post_download.py");
    }

    public static bool HookExists() => File.Exists(HookPath());

    /// <summary>启动后台执行（初始化 + 运行都在后台线程，不阻塞 UI）。</summary>
    public static void RunAsync(string url, string localPath, bool ok, double seconds)
    {
        if (!HookExists()) return;

        // 复制参数到局部变量，避免竞态（DownLoadingsEngine 任务结束后字段可能变化）
        Task.Run(() =>
        {
            try
            {
                if (!PythonRuntime.EnsureInitialized(out var reason))
                {
                    Log?.Invoke("[脚本] " + reason);
                    return;
                }

                var argv = new[]
                {
                    HookPath(),
                    url,
                    localPath,
                    ok ? "1" : "0",
                    seconds.ToString("0.###", CultureInfo.InvariantCulture),
                };

                using (Py.GIL())
                {
                    // 注入 sys.argv，与 C 插件/命令行传参语义一致（显式 PyList 保证转换可靠）
                    using (var sys = Py.Import("sys"))
                    {
                        var list = new PyList();
                        foreach (var a in argv) list.Append(new PyString(a));
                        dyn(sys).argv = list;
                    }

                    var code = File.ReadAllText(HookPath());
                    using var scope = Py.CreateScope();
                    scope.Exec(code);
                }
                Log?.Invoke($"[脚本] post_download.py 已执行 ({(ok ? "成功" : "失败")}, {argv[1]})");
            }
            catch (Exception ex)
            {
                // 钩子脚本或引擎异常均只记录，不影响主流程
                var inner = ex.InnerException?.Message ?? ex.Message;
                Log?.Invoke("[脚本] 执行异常: " + inner);
            }
        });
    }

    private static dynamic dyn(PyObject o) => o;
}