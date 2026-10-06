using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AutoSlice;

/// <summary>
/// C++ 引擎宿主：以 downloader.exe <url>... <out> [--threads N] --json 启动子进程，
/// 异步按行解析 stdout 的 JSON 协议（progress / error / done）并转成 C# 事件。
/// 引擎 stdout 仅输出 JSON（诊断走 stderr，收集后由本类提供）。
/// </summary>
public sealed class EngineRunner : IDisposable
{
    /// <summary>进度回调 (done, total, speedBps)；total&lt;0 表示未知大小。</summary>
    public event Action<long, long, long>? Progress;
    /// <summary>引擎错误（探测/下载/合并失败），可能多次触发。</summary>
    public event Action<string>? ErrorOccurred;
    /// <summary>完成回调 (ok, size, seconds, error)；取消时 ok=false。</summary>
    public event Action<bool, long, double, string>? Completed;

    private Process? _proc;
    private readonly object _gate = new();
    private readonly StringBuilder _stderr = new();
    private string? _cleanupBase;      // 取消/异常退出时按此清理 .part* 残留
    private bool _completedEmitted;

    public bool IsRunning
    {
        get { lock (_gate) return _proc != null && !_proc.HasExited; }
    }

    /// <summary>引擎可执行文件：优先取应用根目录 downloader.exe（发布目录），否则回退工程构建目录。</summary>
    public static string EnginePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "downloader.exe");
        if (File.Exists(local)) return local;
        // 开发/Publish 前未复制引擎时兜底：向工程构建目录探测（cwd 未知，尽量多试两个相对位置）
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "cpp_downloader", "build", "downloader.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "cpp_downloader", "build", "downloader.exe"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException("未找到 downloader.exe 引擎，请先构建 cpp_downloader 或使用发布目录。");
    }

    public void Start(IReadOnlyList<string> urls, string output, int threads)
    {
        lock (_gate)
        {
            if (_proc != null && !_proc.HasExited)
                throw new InvalidOperationException("上一任务仍在进行中。");

            _cleanupBase = output;
            _completedEmitted = false;
            _stderr.Clear();

            var psi = new ProcessStartInfo
            {
                FileName = EnginePath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            };
            foreach (var u in urls) psi.ArgumentList.Add(u);
            psi.ArgumentList.Add(output);                 // 输出路径参数化，不写死
            if (threads > 0)
            {
                psi.ArgumentList.Add("--threads");
                psi.ArgumentList.Add(threads.ToString());
            }
            psi.ArgumentList.Add("--json");

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (_stderr) _stderr.AppendLine(e.Data); } };
            _proc.Exited += (_, _) =>
            {
                // 进程正常退出但没收到 done 行（异常/被杀）：补偿一条失败记录
                if (!_completedEmitted)
                {
                    _completedEmitted = true;
                    var err = CollectStderr();
                    Completed?.Invoke(false, 0, 0, string.IsNullOrWhiteSpace(err) ? "引擎异常退出" : err);
                }
                lock (_gate) { _proc?.Dispose(); _proc = null; }
            };

            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
    }

    /// <summary>取消：终止引擎进程并清理 .part* 残留分片（引擎被强杀不会触发其自身 atexit 清理）。</summary>
    public void Cancel()
    {
        Process? p;
        lock (_gate) p = _proc;
        if (p == null || p.HasExited) return;
        try { p.Kill(entireProcessTree: true); } catch (Exception) { }
        CleanupPartFiles(_cleanupBase);
    }

    /// <summary>取 stderr 完整内容（引擎诊断文本，供界面显示）。</summary>
    public string CollectStderr()
    {
        lock (_stderr) return _stderr.ToString();
    }

    /// <summary>删除 &lt;base&gt;.part* 分片残留（引擎约定的残留命名，见 CleanupPartFiles）。</summary>
    public static void CleanupPartFiles(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath)) return;
        try
        {
            var dir = Path.GetDirectoryName(basePath);
            var name = Path.GetFileName(basePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
            foreach (var f in Directory.GetFiles(dir, name + ".part*"))
            {
                try { File.Delete(f); } catch (Exception) { }
            }
        }
        catch (Exception) { /* 清理尽力而为 */ }
    }

    private void OnLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }   // 非 JSON 行（如用户脚本残留输出）忽略

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("t", out var t)) return;
            switch (t.GetString())
            {
                case "progress":
                    Progress?.Invoke(
                        GetI64(doc, "done"), GetI64(doc, "total"),
                        GetI64(doc, "speed"));
                    break;
                case "error":
                    ErrorOccurred?.Invoke(GetStr(doc, "msg"));
                    break;
                case "done":
                    _completedEmitted = true;
                    var ok = doc.RootElement.GetProperty("ok").GetBoolean();
                    var err = GetStr(doc, "error");
                    if (!ok && string.IsNullOrWhiteSpace(err)) err = CollectStderr();
                    Completed?.Invoke(ok,
                        GetI64(doc, "size"),
                        doc.RootElement.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number
                            ? s.GetDouble() : 0,
                        err);
                    break;
            }
        }
    }

    private static long GetI64(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt64() : -1;

    private static string GetStr(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? "" : "";

    public void Dispose()
    {
        Process? p;
        lock (_gate) p = _proc;
        if (p != null && !p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { }
        }
        p?.Dispose();
        lock (_gate) { _proc = null; }
    }
}