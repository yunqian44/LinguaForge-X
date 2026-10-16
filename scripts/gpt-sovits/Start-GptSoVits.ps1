[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RuntimeRoot,
    [Parameter(Mandatory)][string]$PythonPath,
    [ValidateSet('auto','cpu','cuda')][string]$Device = 'auto',
    [ValidateRange(1024,65535)][int]$Port = 9880
)
$ErrorActionPreference = 'Stop'
& $PythonPath -u (Join-Path $PSScriptRoot 'start_api.py') --runtime-root $RuntimeRoot --device $Device --port $Port
if ($LASTEXITCODE -ne 0) { throw "GPT-SoVITS API 已退出，代码 $LASTEXITCODE。" }
