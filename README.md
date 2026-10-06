# AutoSlice 下载器

基于 C++17/WinHTTP 多线程分片下载引擎的全内嵌桌面下载工具：WPF 图形界面驱动 C++ 引擎，随包内嵌 .NET 运行时与嵌入式 Python，单 MSI 安装包免依赖运行。

## 特性

- **多线程分片 + 动态加线程**：单文件 Range 分片并行下载，带宽饱和自动收敛线程数（默认自适应，硬上限 32）
- **多源镜像自动切换**：一次可提供多个 URL，源故障自动轮换/重试
- **Range 守卫**：请求分片却收到 HTTP 200（服务器忽略 Range）时强制降级单线程，杜绝分片数据错位导致文件损坏
- **断点续传**：中断/失败后清理残留分片（`.part*`），重启可续传
- **全局限速**：不限 / 自动（按实测带宽 80%）/ 手动指定
- **三种使用形态**：
  - CLI（`downloader.exe`，带进度条与 `--json` 机器可读协议）
  - Win32 原生 GUI（`downloadergui.exe`）
  - WebView2 界面（`downloadergui2.exe`，需自行提供 WebView2 SDK 头文件）
- **WPF 桌面端（AutoSlice）**：历史记录（`%LOCALAPPDATA%\AutoSlice\history.json`）、下载后 Python 钩子、打开文件所在文件夹
- **单文件 MSI 安装包**：WiX7 全中文向导，内嵌自包含 .NET 运行时 + 嵌入式 Python（目标机器无需安装任何依赖）

## 目录结构

```
hosts/AutoSlice/            WPF 桌面端（GUI + 引擎宿主 + 安装包工程）
  EngineRunner.cs           进程宿主：downloader.exe --json 协议解析
  MainWindow.xaml(.cs)      主窗口（历史记录 / 线程 / 限速 / 下载）
  PostScript.cs             下载后 Python 钩子
  PythonRuntime.cs          嵌入式 Python 引擎（懒加载）
  pack-runtime.ps1          构建时打包嵌入式 Python 运行时
  Installer/                WiX7 单 MSI 安装包
cpp_downloader/             C++17 下载引擎
  src/downloader.h/.cpp     核心引擎（分片/多源/限速/重试）
  src/history.h/.cpp        历史记录模块（JSON 原子写）
  src/main.cpp              CLI 入口（--json / --history / 多参数）
  src/gui.cpp               Win32 GUI
  src/gui_webview.cpp       WebView2 界面
  test/                     Python 测试服务器与回归脚本
```

## 构建

### 引擎（C++17，MinGW + CMake）

```bash
cmake -S cpp_downloader -B cpp_downloader/build -G Ninja \
  -DCMAKE_CXX_COMPILER=g++ -DCMAKE_MAKE_PROGRAM=ninja
ninja -C cpp_downloader/build
```

产物：`downloader.exe`、`downloadergui.exe`、`downloadergui2.exe`。
注意：`downloadergui2.exe`（WebView2 版）需要第三方 WebView2 SDK 头文件（`third_party/`，约 11 MB，未随仓库分发）。

### WPF 桌面端（.NET 8 SDK）

```bash
dotnet publish hosts/AutoSlice/AutoSlice.csproj -c Release -r win-x64 \
  --self-contained true -o <发布目录>
```

发布时自动复制引擎并打包嵌入式 Python 运行时（`pack-runtime.ps1`）。

### MSI 安装包（WiX7 + .NET SDK）

需先安装 `wix` 全局工具并注册 UI/Util 扩展：

```bash
dotnet tool install --global wix
wix extension add WixToolset.UI.wixext
wix extension add WixToolset.Util.wixext
powershell -File hosts/AutoSlice/Installer/build-msi.ps1
```

产出单文件 `AutoSlice-<版本>-x64.msi`（内嵌自包含 .NET 运行时 + 引擎 + 嵌入式 Python）。

## 用法

### CLI

```bash
# 多源下载（8 线程）
downloader.exe https://a.com/file.bin https://mirror.com/file.bin out.bin --threads 8

# 不限速 / 手动限速（KB/s）
downloader.exe https://a.com/f.bin out.bin --speed-cap 0
downloader.exe https://a.com/f.bin out.bin --speed-cap 2048

# 历史记录
downloader.exe --history
```

### --json 机器可读协议（供 GUI/脚本宿主）

启动参数附加 `--json` 后，stdout 每行一条 JSON（诊断文本走 stderr，不污染流）：

```
{"t":"progress","done":N,"total":N,"speed":N}     // total=-1 表示未知大小（chunked）
{"t":"error","msg":"..."}
{"t":"done","ok":true|false,"size":N,"seconds":S,"path":"...","error":"..."}
```

### WPF 桌面端

启动后粘贴下载地址（多个镜像用空格或 `|` 分隔），选择线程数与保存目录即可。下载完成后自动落历史记录；若 `%LOCALAPPDATA%\AutoSlice\post_download.py` 存在，下载结束后会以 `argv = [脚本, url, 本地路径, "1"|"0", 耗时秒]` 调用它（用于解压/校验/通知等，异常只记日志不影响主流程）。

## 测试

```bash
# Range 支持（分片路径）与 忽略 Range（200 降级路径）两个测试服务器
python test/range_server.py 8123 test
python -m http.server 8124 --directory test

# 下载并对拍 SHA256
downloader.exe http://127.0.0.1:8123/test_src.bin out.bin --threads 8
certutil -hashfile out.bin SHA256

# 回归脚本（进程生命周期 + 分片清理）
python test/lifecycle_test.py
```

（`test/test_src.bin` 为 12 MB 随机源数据，由 `downloader.exe --self-test <目录>` 生成，未随仓库分发。）

## 平台

Windows 10 1809+ / Windows 11（WinHTTP 原生，终端 UTF-8 输出）。MSI 为 perUser 安装，无需管理员权限。