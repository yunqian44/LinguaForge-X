param([string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $workspaceRoot 'artifacts\media-tools'
$buildRoot = Join-Path $toolRoot 'ffmpeg-9.0.2-essentials_build'
$expectedHash = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $toolRoot 'ffmpeg-essentials.zip'
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        $downloadPath = Join-Path $toolRoot 'ffmpeg-essentials.download'
        Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $downloadPath
        if ((Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash -ne $expectedHash) {
            throw '下载的版本与已验证的 FFmpeg 9.0.2 不一致。请提供匹配版本的本地 ZIP，或在设置页选择自己的 FFmpeg/FFprobe；不会执行新版本。'
        }
        Move-Item -LiteralPath $downloadPath -Destination $ArchivePath
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'FFmpeg 压缩包校验失败。需要已经验证的 9.0.2 essentials ZIP。'
}
if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'bin\ffmpeg.exe'))) {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $toolRoot
}
if (-not (Test-Path -LiteralPath (Join-Path $buildRoot 'bin\ffprobe.exe'))) { throw '缺少 ffprobe.exe，请检查解压目录。' }
Write-Output "FFmpeg 已准备：$buildRoot"
Write-Output '下一次构建会将本地工具和许可证复制到应用的 tools 目录。也可以在设置页指定已有工具。'
