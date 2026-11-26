<#
.SYNOPSIS
  Checks a portable publish folder against the spec "Compact package layout" (fast-portable-startup).
.EXAMPLE
  pwsh tools/check-package.ps1 publish/AssetManager
#>
param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'

$required = 'AssetManager.exe', 'portable', 'LICENSE.txt', 'wwwroot.zip',
            'llama/llama-server.exe', 'llama/LICENSE.txt',
            'ffmpeg/ffmpeg.exe', 'ffmpeg/ffprobe.exe', 'ffmpeg/FFMPEG.LICENSE.txt'
$allowedFolders = 'llama', 'ffmpeg', 'licenses'
$maxOtherFiles = 5

$problems = @()
$missing = $required | Where-Object { -not (Test-Path (Join-Path $Root $_)) }
if ($missing) { $problems += "missing: $($missing -join ', ')" }

$top = Get-ChildItem -LiteralPath $Root -Force
$folders = $top | Where-Object PSIsContainer | Where-Object Name -notin $allowedFolders
if ($folders) { $problems += "unexpected folders: $($folders.Name -join ', ')" }
$others = $top | Where-Object { -not $_.PSIsContainer -and $_.Name -notin 'AssetManager.exe', 'portable', 'LICENSE.txt' }
if ($others.Count -gt $maxOtherFiles) { $problems += "more than $maxOtherFiles other files: $($others.Name -join ', ')" }

$forbidden = Get-ChildItem -LiteralPath $Root -Recurse -File -Include *.gguf, *.pdb
if ($forbidden) { $problems += "models or debug symbols: $($forbidden.Name -join ', ')" }

if ($problems) { throw "The package at $Root does not match the compact layout: $($problems -join '; ')" }
$files = (Get-ChildItem -LiteralPath $Root -Recurse -File).Count
"Package OK: $($top.Name -join ', ') ($files files)"
