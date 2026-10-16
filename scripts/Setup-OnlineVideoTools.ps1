param(
    [string]$YtDlpPath,
    [string]$DenoArchivePath
)

# These immutable official releases are intentionally pinned. Updating a tool requires
# reviewing its release and updating the published checksums and notices together.
$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $workspaceRoot 'artifacts\online-tools'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null

function Save-VerifiedFile {
    param([string]$Name, [string]$Uri, [string]$Sha256, [string]$LocalSource)
    $destination = Join-Path $toolRoot $Name
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $Sha256) {
            throw "文件校验失败：$destination。请移走该文件后重试；不会使用未经验证的工具。"
        }
        return $destination
    }

    $pending = Join-Path $toolRoot ($Name + '.' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        if ($LocalSource) {
            if (-not (Test-Path -LiteralPath $LocalSource -PathType Leaf)) { throw "找不到文件：$LocalSource" }
            Copy-Item -LiteralPath $LocalSource -Destination $pending
        } else {
            Invoke-WebRequest -Uri $Uri -OutFile $pending -UseBasicParsing
        }
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $Sha256) {
            throw "下载文件 SHA256 校验失败：$Name。请使用脚本指定的官方版本。"
        }
        Move-Item -LiteralPath $pending -Destination $destination
        return $destination
    } finally {
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    }
}

$ytDlp = Save-VerifiedFile -Name 'yt-dlp.exe' `
    -Uri 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe' `
    -Sha256 '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A' -LocalSource $YtDlpPath
$denoZip = Save-VerifiedFile -Name 'deno-2.9.7-x86_64-pc-windows-msvc.zip' `
    -Uri 'https://github.com/denoland/deno/releases/download/v2.9.7/deno-x86_64-pc-windows-msvc.zip' `
    -Sha256 'A0C3101B4158D1DFB7D6A78A7BF0F3DE80C96BB423C152BEEC8BEB22786F2238' -LocalSource $DenoArchivePath

$denoPath = Join-Path $toolRoot 'deno.exe'
$denoHash = 'E020F3E232BD16E33768DEE528E5983349C962952051CED0A5D58AD42F5D9B33'
if (-not (Test-Path -LiteralPath $denoPath)) {
    # Extract only the expected leaf to a fixed destination; never extract archive paths.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($denoZip)
    $pending = Join-Path $toolRoot ('deno.' + [Guid]::NewGuid().ToString('N') + '.download')
    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -ceq 'deno.exe' })
        if ($entries.Count -ne 1) { throw 'Deno 压缩包中未找到唯一的 deno.exe。' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $pending)
        if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $denoHash) {
            throw 'Deno 可执行文件 SHA256 校验失败。'
        }
        Move-Item -LiteralPath $pending -Destination $denoPath
    } finally {
        $archive.Dispose()
        if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
    }
}
if ((Get-FileHash -LiteralPath $denoPath -Algorithm SHA256).Hash -ne $denoHash) {
    throw "文件校验失败：$denoPath。请移走该文件后重试。"
}

$null = Save-VerifiedFile -Name 'yt-dlp-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.08.19/LICENSE' `
    -Sha256 '7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C'
$null = Save-VerifiedFile -Name 'yt-dlp-THIRD-PARTY-LICENSES.txt' `
    -Uri 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/2026.08.19/THIRD_PARTY_LICENSES.txt' `
    -Sha256 '472AEFE951C7DB35E1657C1D13FD337140511ED6F2B329205105AD441C5A02B7'
$null = Save-VerifiedFile -Name 'Deno-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/denoland/deno/v2.9.7/LICENSE.md' `
    -Sha256 'F62497FFFECC0852960C8D3E6934B9DB86D16396E9B604072E923892CAE3A588'
$null = Save-VerifiedFile -Name 'Deno-V8-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/v8/v8/15.0.245.2/LICENSE' `
    -Sha256 '6AB33AF8774A0F396EE3AEEB761E3229057682D6F9FA7F572E390C2CB3A6E509'
$null = Save-VerifiedFile -Name 'Deno-V8-BSD-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/v8/v8/15.0.245.2/LICENSE.v8' `
    -Sha256 '4AF93C12062C58058378DE2397DC1C92BBFF9DDFB1D583A01C84127557CE97CA'
$null = Save-VerifiedFile -Name 'Deno-Rusty-V8-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/denoland/rusty_v8/v150.4.0/LICENSE' `
    -Sha256 '1C6356FB751D45F0C53093EBF8A7F5E580E802F51999178E19D60F3EC39E147D'
$null = Save-VerifiedFile -Name 'Deno-TypeScript-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/microsoft/TypeScript/v6.0.3/LICENSE.txt' `
    -Sha256 'A7D00BFD54525BC694B6E32F64C7EBCF5E6B7AE3657BE5CC12767BCE74654A47'
$null = Save-VerifiedFile -Name 'Deno-Node-Definitions-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/denoland/deno/v2.9.7/cli/tsc/dts/node/LICENSE' `
    -Sha256 'C2CFCCB812FE482101A8F04597DFC5A9991A6B2748266C47AC91B6A5AAE15383'
$null = Save-VerifiedFile -Name 'Deno-Undici-Definitions-LICENSE.txt' `
    -Uri 'https://raw.githubusercontent.com/denoland/deno/v2.9.7/cli/tsc/dts/node/undici/LICENSE' `
    -Sha256 'A6DB8096B2707BC0102D256917D4D33F298BA36D8C3F25DE067A2B5BB379DB27'

Write-Output "yt-dlp 2026.08.19 已准备：$ytDlp"
Write-Output "Deno 2.9.7 已准备：$denoPath"
Write-Output '下一次构建会将工具和许可证复制到应用的 tools 目录。未修改系统 PATH。'
