[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeRoot, [Parameter(Mandatory)][string]$PythonPath)
$ErrorActionPreference = 'Stop'
& $PythonPath (Join-Path $PSScriptRoot 'check_runtime.py') --runtime-root $RuntimeRoot
if ($LASTEXITCODE -ne 0) { throw '运行环境检查未通过；请处理报告中的缺失项。' }
