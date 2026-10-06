# pack-runtime.ps1 —— 发布后打包：C++ 引擎 + 嵌入式 Python 运行时到输出目录。
# 使发布出的程序在目标机器上无需安装 Python 即可运行（引擎为独立 exe，随程序分发）。
# 用法（由 AutoSlice.csproj 的 AfterPublish target 调用）：
#   powershell -File pack-runtime.ps1 -TargetDir <输出目录> -EngineDir <downloader.exe 所在目录> [-PythonDir <python 安装目录>]
param(
    [Parameter(Mandatory = $true)][string]$TargetDir,
    # C++ 引擎 downloader.exe 所在目录（属性 $(EngineDir)，默认解析到 cpp_downloader\build）
    [Parameter(Mandatory = $false)][string]$EngineDir = '',
    # 显式指定 Python 安装目录（多架构打包时用，如 x86 打包指向 x86 Python）。
    # 为空时回退到 PATH 探测（单架构机器上的默认行为）。
    [string]$PythonDir = ''
)
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) { Write-Host "[pack-runtime] $msg" -ForegroundColor Cyan }

# 1) 复制 C++ 引擎 downloader.exe -> <TargetDir>\（与 AutoSlice.exe 同目录）
if ($EngineDir) {
    $engineSrc = Join-Path $EngineDir 'downloader.exe'
    if (Test-Path $engineSrc) {
        Copy-Item $engineSrc (Join-Path $TargetDir 'downloader.exe') -Force
        Write-Step "C++ 引擎已复制: $engineSrc"
    } else {
        Write-Warning "未找到引擎: $engineSrc（跳过，GUI 将无法下载）"
    }
} else {
    Write-Warning "未指定 -EngineDir，跳过引擎复制"
}

# 2) 定位要打包的 Python：-PythonDir 优先，否则 PATH 探测（路径动态获取，不写死）。
$pyDir = $null
if ($PythonDir) {
    $pyDir = $PythonDir
} else {
    $pyCmd = Get-Command python -ErrorAction SilentlyContinue
    if ($pyCmd) {
        try {
            $pyDir = (& python -c "import sys,os;print(os.path.dirname(sys.executable))" 2>$null).Trim()
        } catch { $pyDir = $null }
    }
}
if (-not $pyDir -or -not (Test-Path (Join-Path $pyDir 'python3*.dll'))) {
    Write-Warning "未找到可用的 Python 安装，跳过嵌入式运行时打包（下载后脚本将不可用）。"
    $pyDir = $null
}

# 3) 复制嵌入式运行时 -> <TargetDir>\runtime\python\
$pyDst = Join-Path $TargetDir 'runtime\python'
if ($pyDir) {
    $dst = $pyDst
    New-Item -ItemType Directory -Force -Path $dst | Out-Null

    $dll = Get-ChildItem $pyDir -Filter 'python3*.dll' |
        Where-Object { $_.Name -match '^python3\d+\.dll$' } | Select-Object -First 1
    if (-not $dll) {
        Write-Warning "Python 目录中未找到 python3xx.dll，跳过嵌入式运行时打包。"
    } else {
        Copy-Item $dll.FullName (Join-Path $dst $dll.Name) -Force
        # python3.dll（稳定 ABI 桥）可选，带上以防扩展模块需要
        $abi = Join-Path $pyDir 'python3.dll'
        if (Test-Path $abi) { Copy-Item $abi (Join-Path $dst 'python3.dll') -Force }
        Copy-Item (Join-Path $pyDir 'DLLs') (Join-Path $dst 'DLLs') -Recurse -Force

        # Lib 用逐目录复制并剔除体积大/用不到的目录：
        #  site-packages —— pip 第三方包（可占 1GB+），本项目只依赖标准库
        #  test / idlelib / turtledemo / ensurepip / venv / lib2to3 / pydoc_data —— 开发辅助
        #  tcl / tkinter —— tk 图形库（WPF 程序不需要）
        #  __pycache__ / *.pyc —— 字节码缓存，运行时可重新生成
        $libDst = Join-Path $dst 'Lib'
        # 目标可能是上一次构建的脏拷贝，先整体删除再干净复制
        if (Test-Path $libDst) { Remove-Item $libDst -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $libDst | Out-Null
        $xcld = @('site-packages','test','idlelib','turtledemo','ensurepip','venv','lib2to3','pydoc_data','tcl','tkinter')
        $libSrc = Join-Path $pyDir 'Lib'
        Get-ChildItem $libSrc -File | Copy-Item -Destination $libDst -Force
        Get-ChildItem $libSrc -Directory | Where-Object { $_.Name -notin $xcld } |
            ForEach-Object { Copy-Item $_.FullName (Join-Path $libDst $_.Name) -Recurse -Force }
        # 字节码缓存运行时可重新生成，删除以减小体积
        Get-ChildItem $libDst -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
        Get-ChildItem $libDst -Recurse -File -Filter '*.pyc' | Remove-Item -Force
        Write-Step "嵌入式 Python 运行时已复制到 $dst（DLL=$($dll.Name)）"
    }
}

# 4) VC++ 运行库（python3xx.dll 的依赖，如 vcruntime140.dll / vcruntime140_1.dll）
#      Windows Sandbox 等干净系统默认没有 VC++ Redistributable，python DLL 加载失败。
#      优先取 Python 安装目录内的同名运行库，回退 System32。分发到：
#      应用根目录 + python DLL 同目录（双保险）。
$sys32 = Join-Path $env:SystemRoot 'System32'
$vcCopied = @()
foreach ($vcName in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
    $vcSrc = $null
    if ($pyDir) {
        $p = Join-Path $pyDir $vcName
        if (Test-Path $p) { $vcSrc = $p }
    }
    if (-not $vcSrc) {
        $p = Join-Path $sys32 $vcName
        if (Test-Path $p) { $vcSrc = $p }
    }
    if ($vcSrc) {
        Copy-Item $vcSrc (Join-Path $TargetDir $vcName) -Force
        if ($pyDst) { Copy-Item $vcSrc (Join-Path $pyDst $vcName) -Force }
        $vcCopied += $vcName
    }
}
if ($vcCopied.Count -gt 0) {
    Write-Step "VC++ 运行库已随程序分发: $($vcCopied -join ', ')"
} else {
    Write-Warning "构建机 System32 未探测到 VC++ 运行库 DLL；若目标机未装 VC++ Redistributable，加载 Python 将失败"
}