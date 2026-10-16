[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts\gpt-sovits'),
    [switch]$InstallDependencies,
    [switch]$DownloadModels,
    [string]$SourceArchive,
    [string]$FfmpegPath
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$sourceCommit = '1b47ca07ef43a284e0b8bd7f29d3e1e3ff76082a'
$sourceHash = '713c9df5fa2c7b0a2fee96fa8954375c915d64229bb1fda10de37f588479e16c'
$runtimeRoot = Join-Path $installRoot 'GPT-SoVITS'
$downloads = Join-Path $installRoot 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null

function Get-VerifiedFile([string]$Url, [string]$Destination, [string]$Hash) {
    if ((Test-Path -LiteralPath $Destination) -and ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -eq $Hash)) { return }
    $partial = $Destination + '.download'
    Invoke-WebRequest -Uri $Url -OutFile $partial
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) { throw "SHA256 校验失败：$Destination；未安装此文件。" }
    Move-Item -LiteralPath $partial -Destination $Destination -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $runtimeRoot 'api_v2.py'))) {
    if (-not $SourceArchive) { $SourceArchive = Join-Path $downloads 'source.zip' }
    if (-not (Test-Path -LiteralPath $SourceArchive)) {
        Get-VerifiedFile "https://codeload.github.com/RVC-Boss/GPT-SoVITS/zip/$sourceCommit" $SourceArchive $sourceHash
    }
    if ((Get-FileHash -LiteralPath $SourceArchive -Algorithm SHA256).Hash -ne $sourceHash) { throw 'GPT-SoVITS 源码压缩包与已核查版本不符。' }
    $extractRoot = Join-Path $installRoot 'source-extract'
    Expand-Archive -LiteralPath $SourceArchive -DestinationPath $extractRoot -Force
    $extracted = [IO.Path]::GetFullPath((Join-Path $extractRoot "GPT-SoVITS-$sourceCommit"))
    if (-not $extracted.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '源码目录超出安装目录。' }
    if (Test-Path -LiteralPath $runtimeRoot) { throw "目标源码目录已存在且不完整，请检查：$runtimeRoot" }
    Move-Item -LiteralPath $extracted -Destination $runtimeRoot
    Set-Content -LiteralPath (Join-Path $runtimeRoot '.linguaforge-upstream-commit') -Value $sourceCommit -Encoding utf8
}

if ($FfmpegPath) {
    $ffmpeg = Get-Item -LiteralPath $FfmpegPath
    if ($ffmpeg.Name -ne 'ffmpeg.exe') { throw 'FfmpegPath 应指向 ffmpeg.exe。' }
    Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $runtimeRoot 'ffmpeg.exe')
    $portableTools = Join-Path $runtimeRoot 'runtime'
    New-Item -ItemType Directory -Path $portableTools -Force | Out-Null
    Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $portableTools 'ffmpeg.exe')
    $ffprobeSibling = Join-Path $ffmpeg.DirectoryName 'ffprobe.exe'
    if (Test-Path -LiteralPath $ffprobeSibling) { Copy-Item -LiteralPath $ffprobeSibling -Destination (Join-Path $portableTools 'ffprobe.exe') }
}

$python = Join-Path $installRoot '.venv\Scripts\python.exe'
if ($InstallDependencies) {
    # All executables, managed Python distributions, caches and packages stay in InstallDirectory.
    $uvRoot = Join-Path $installRoot 'uv'
    $uvZip = Join-Path $downloads 'uv-0.13.1.zip'
    Get-VerifiedFile 'https://github.com/astral-sh/uv/releases/download/0.13.1/uv-x86_64-pc-windows-msvc.zip' $uvZip '3b2661a463a5baea7f5076ea2b25ab71d77f36a6c0d809e5350651792f80a672'
    if (-not (Test-Path -LiteralPath (Join-Path $uvRoot 'uv.exe'))) { Expand-Archive -LiteralPath $uvZip -DestinationPath $uvRoot -Force }
    $uv = Join-Path $uvRoot 'uv.exe'
    $variableNames = @('UV_PYTHON_INSTALL_DIR','UV_PYTHON_BIN_DIR','UV_CACHE_DIR','UV_NO_MODIFY_PATH','UV_PYTHON_PREFERENCE')
    $savedVariables = @{}
    foreach ($name in $variableNames) { $savedVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        $env:UV_PYTHON_INSTALL_DIR = Join-Path $installRoot 'python'
        $env:UV_PYTHON_BIN_DIR = Join-Path $installRoot 'python-bin'
        $env:UV_CACHE_DIR = Join-Path $installRoot 'cache'
        $env:UV_NO_MODIFY_PATH = '1'
        $env:UV_PYTHON_PREFERENCE = 'only-managed'
        if (-not (Test-Path -LiteralPath $python)) {
            & $uv venv --python 3.10 (Join-Path $installRoot '.venv')
            if ($LASTEXITCODE -ne 0) { throw '项目内 Python / venv 准备失败。' }
        }
        & $uv pip install --python $python 'torch==2.6.0+cpu' 'torchaudio==2.6.0+cpu' --index-url 'https://download.pytorch.org/whl/cpu'
        if ($LASTEXITCODE -ne 0) { throw 'CPU PyTorch 安装失败。' }
        # Upstream's full requirements include ASR and GPU ONNX extras; use the actual v2 training/TTS subset.
        & $uv pip install --python $python -r (Join-Path $PSScriptRoot 'requirements-windows-cpu.lock.txt')
        if ($LASTEXITCODE -ne 0) { throw 'GPT-SoVITS 依赖安装失败。已有安装可保留，检查日志后重试。' }
        & $uv pip freeze --python $python | Set-Content -LiteralPath (Join-Path $installRoot 'installed-packages.txt') -Encoding utf8
        # jieba_fast has no Windows wheel; upstream uses the compatible jieba APIs below.
        # This adapter affects segmentation speed, not the acoustic model or training algorithm.
        $jiebaAdapter = Join-Path $runtimeRoot 'jieba_fast'
        New-Item -ItemType Directory -Path $jiebaAdapter -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $jiebaAdapter '__init__.py') -Value 'from jieba import *' -Encoding utf8
        Set-Content -LiteralPath (Join-Path $jiebaAdapter 'posseg.py') -Value 'from jieba.posseg import *' -Encoding utf8
    }
    finally {
        foreach ($name in $variableNames) { [Environment]::SetEnvironmentVariable($name, $savedVariables[$name], 'Process') }
    }
}

if ($DownloadModels) {
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'models-v2.json') -Raw | ConvertFrom-Json
    $total = ($catalog.files | Measure-Object -Property size -Sum).Sum
    Write-Output ('v2 基座模型总计约 {0:N2} GiB；已存在且校验通过的文件会跳过。' -f ($total / 1GB))
    foreach ($model in $catalog.files) {
        $target = Join-Path (Join-Path $runtimeRoot 'GPT_SoVITS\pretrained_models') $model.path
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Write-Output ('准备模型：' + $model.path)
        $url = 'https://huggingface.co/' + $catalog.repository + '/resolve/' + $catalog.revision + '/' + $model.path
        Get-VerifiedFile $url $target $model.sha256
    }
    # Official GPT-SoVITS Chinese v2 frontend also needs this 562 MiB pronunciation model.
    $g2pwZip = Join-Path $downloads 'G2PWModel_1.1.zip'
    $g2pwTarget = Join-Path $runtimeRoot 'GPT_SoVITS\text\G2PWModel'
    if (-not (Test-Path -LiteralPath (Join-Path $g2pwTarget 'g2pW.onnx'))) {
        Write-Output '准备中文多音字模型 G2PW（约 562 MiB）。'
        Get-VerifiedFile 'https://www.modelscope.cn/models/kamiorinn/g2pw/resolve/master/G2PWModel_1.1.zip' $g2pwZip 'b116f6930a7ee55eef6576a8d8e14bf40c1106583439e8ae924b901512379c64'
        $textRoot = Join-Path $runtimeRoot 'GPT_SoVITS\text'
        Expand-Archive -LiteralPath $g2pwZip -DestinationPath $textRoot -Force
        $g2pwExtracted = [IO.Path]::GetFullPath((Join-Path $textRoot 'G2PWModel_1.1'))
        if (-not $g2pwExtracted.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '中文模型目录超出安装目录。' }
        if (-not (Test-Path -LiteralPath $g2pwTarget)) { Move-Item -LiteralPath $g2pwExtracted -Destination $g2pwTarget }
    }
    if (Test-Path -LiteralPath $python) {
        & $python -X utf8 (Join-Path $PSScriptRoot 'prepare_text_resources.py') --runtime-root $runtimeRoot
        if ($LASTEXITCODE -ne 0) { throw '中文/英文文本资源准备失败，请检查日志。' }
    }
}

$settings = [ordered]@{ RootDirectory = $runtimeRoot; PythonPath = $python; ApiEndpoint = 'http://127.0.0.1:9880/'; Device = 'cpu'; UpstreamCommit = $sourceCommit }
$settings | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'runtime-settings.json') -Encoding utf8
Write-Output ($settings | ConvertTo-Json)
Write-Output '准备脚本结束；只有通过 Test-Runtime.ps1 并成功启动 API 后才表示推理运行环境可用。'
