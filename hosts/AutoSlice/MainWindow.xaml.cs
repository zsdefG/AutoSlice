using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AutoSlice;

public partial class MainWindow : Window
{
    private readonly EngineRunner _engine = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        PostScript.Log = s => Dispatcher.Invoke(() => Status("脚本: " + s, false));

        ThreadsBox.Items.Add(new ThreadItem { Label = "自动", Threads = 0 });
        foreach (var n in new[] { 1, 2, 4, 8, 16, 32 })
            ThreadsBox.Items.Add(new ThreadItem { Label = n + " 线程", Threads = n });
        ThreadsBox.SelectedIndex = 0;

        _engine.Progress += (done, total, speed) => Dispatcher.Invoke(() => OnProgress(done, total, speed));
        _engine.ErrorOccurred += msg => Dispatcher.Invoke(() => OnEngineError(msg));
        _engine.Completed += (ok, size, seconds, err) => Dispatcher.Invoke(() => OnCompleted(ok, size, seconds, err));

        LoadSettings();
        RefreshHistory();
        Closed += (_, _) => { _engine.Dispose(); PythonRuntime.ShutdownIfNeeded(); };
    }

    // ===== 设置（%LOCALAPPDATA%\AutoSlice\settings.json）=====
    private static string SettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSlice", "settings.json");

    private void LoadSettings()
    {
        try
        {
            var p = SettingsPath();
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("saveDir", out var d) && d.ValueKind == JsonValueKind.String
                    && Directory.Exists(d.GetString())) DirBox.Text = d.GetString()!;
                if (doc.RootElement.TryGetProperty("threads", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    var idx = ThreadsBox.Items.Cast<ThreadItem>().ToList()
                        .FindIndex(x => x.Threads == t.GetInt32()) + 1; // +1: 0=自动已在索引0
                    if (idx > 0) ThreadsBox.SelectedIndex = idx - 1;
                }
            }
        }
        catch (Exception) { /* 设置损坏不影响启动 */ }

        if (string.IsNullOrWhiteSpace(DirBox.Text))
            DirBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Status("就绪" + (PostScript.HookExists() ? "（检测到 post_download.py 钩子）" : ""), false);
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath())!);
            var th = (ThreadsBox.SelectedItem as ThreadItem)?.Threads ?? 0;
            File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(new
            {
                saveDir = DirBox.Text,
                threads = th,
                name = NameBox.Text,
                lastUrl = UrlBox.Text,
            }));
        }
        catch (Exception) { /* 设置保存失败不影响使用 */ }
    }

    // ===== 下载控制 =====
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(UrlBox.Text)) { Status("请输入下载地址", true); return; }

        var urls = ParseUrls(UrlBox.Text);
        if (urls.Count == 0) { Status("下载地址无法解析", true); return; }

        var dir = DirBox.Text.Trim();
        if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch (Exception) { Status("保存目录不可用", true); return; } }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = FilenameFromUrl(urls[0]);
        if (string.IsNullOrEmpty(name)) { Status("无法从 URL 推断文件名，请手动填写", true); return; }

        var output = Path.Combine(dir, SanitizeFilename(name));
        var threads = (ThreadsBox.SelectedItem as ThreadItem)?.Threads ?? 0;

        _busy = true;
        StartBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        SaveSettings();
        Status("下载中…", false);
        try
        {
            _engine.Start(urls, output, threads);
        }
        catch (Exception ex)
        {
            _busy = false;
            StartBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;
            Status("启动失败: " + ex.Message, true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _engine.Cancel();
        Status("已取消", false);
    }

    private void OnProgress(long done, long total, long speed)
    {
        if (total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100, done * 100.0 / total);
            Status($"{FormatBytes(done)} / {FormatBytes(total)} ({done * 100.0 / total:F1}%)", false);
        }
        else
        {
            Progress.IsIndeterminate = true;
            Status($"{FormatBytes(done)}（未知大小）", false);
        }
        SpeedText.Text = $"实时速度: {FormatSpeed(speed)}   线程: {ThreadsBox.Text}";
    }

    private void OnEngineError(string msg)
    {
        Status("错误: " + msg, true);
        SpeedText.Text = msg;
    }

    private void OnCompleted(bool ok, long size, double seconds, string error)
    {
        _busy = false;
        StartBtn.IsEnabled = true;
        CancelBtn.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = ok ? 100 : 0;
        SpeedText.Text = "";

        if (ok)
        {
            Status($"完成: {FormatBytes(size)}，耗时 {seconds:F1}s", false);
        }
        else
        {
            var stderr = _engine.CollectStderr();
            Status("失败: " + (string.IsNullOrWhiteSpace(error) ? stderr : error), true);
        }

        // 下载后钩子：用户自定义 post_download.py
        PostScript.RunAsync(UrlBox.Text.Trim(), Path.Combine(DirBox.Text.Trim(), NameBox.Text.Trim()), ok, seconds);

        RefreshHistory();
    }

    // ===== 历史记录（与引擎共用同一 history.json）=====
    private static string HistoryPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSlice", "history.json");

    private void RefreshHistory()
    {
        try
        {
            List<HistoryItem> items = new();
            var p = HistoryPath();
            if (File.Exists(p))
            {
                var recs = JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(p)) ?? new();
                foreach (var r in recs)
                {
                    items.Add(new HistoryItem
                    {
                        Url = r.url,
                        Filename = r.filename,
                        SizeText = FormatBytes(r.size),
                        ResultText = r.success ? "成功" : "失败",
                        SecondsText = r.seconds.ToString("0.0s", CultureInfo.InvariantCulture),
                        TimeText = UnixToLocal(r.time),
                    });
                }
                items.Reverse();   // 最新的显示在最上
            }
            HistoryList.ItemsSource = items;
        }
        catch (Exception) { /* 历史文件损坏时显示空列表 */ }
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryItem it && !string.IsNullOrEmpty(it.Url))
        {
            if (string.IsNullOrEmpty(DirBox.Text) || !System.IO.Directory.Exists(DirBox.Text)) return;
            var full = Path.Combine(DirBox.Text, it.Filename);
            ExplorerSelect(full);
        }
        else
        {
            OpenSaveDir_Click(sender, e);
        }
    }

    private void OpenSaveDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim();
        if (System.IO.Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ===== 辅助 =====
    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择保存目录", Multiselect = false };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
            DirBox.Text = dlg.FolderName;
    }

    private static List<string> ParseUrls(string text)
    {
        return text.Split(new[] { '\r', '\n', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
    }

    private static string FilenameFromUrl(string url)
    {
        var u = url;
        var q = u.IndexOfAny(new[] { '?', '#' });
        if (q >= 0) u = u[..q];
        var slash = u.LastIndexOf('/');
        if (slash >= 0) u = u[(slash + 1)..];
        return Uri.UnescapeDataString(u);
    }

    private static string SanitizeFilename(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "download.bin" : name;
    }

    private void Status(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33));
    }

    private static void ExplorerSelect(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
    }

    private static string FormatBytes(long b) =>
        b >= 1 << 30 ? $"{b / (double)(1 << 30):F2} GB"
        : b >= 1 << 20 ? $"{b / (double)(1 << 20):F1} MB"
        : b >= 1 << 10 ? $"{b / (double)(1 << 10):F1} KB"
        : $"{b} B";

    private static string FormatSpeed(long bps) => FormatBytes(bps) + "/s";

    private static string UnixToLocal(long ts)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception) { return ts.ToString(); }
    }

    private sealed class ThreadItem
    {
        public string Label { get; init; } = "";
        public int Threads { get; init; }
        public override string ToString() => Label;
    }

    private sealed class HistoryItem
    {
        public string Url { get; init; } = "";
        public string Filename { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string ResultText { get; init; } = "";
        public string SecondsText { get; init; } = "";
        public string TimeText { get; init; } = "";
    }

    private sealed class HistoryRecord
    {
        public string url { get; set; } = "";
        public string filename { get; set; } = "";
        public string path { get; set; } = "";
        public long size { get; set; }
        public bool success { get; set; }
        public string error { get; set; } = "";
        public double seconds { get; set; }
        public long speed_bps { get; set; }
        public long time { get; set; }
    }
}