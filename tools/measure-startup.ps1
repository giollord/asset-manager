<#
.SYNOPSIS
  Measures a portable Asset Manager folder: how many files it has and how long its last start took.

.DESCRIPTION
  With -Publish, first publishes the portable build into -Folder (keeping an existing data folder).
  Then counts files, folders and top-level entries outside "data", and reads the newest app log in
  <Folder>\data\logs for the last start: seconds from "started" to "Vector index", and each
  "AI server <name> ready (<backend>) in N s" line after that start.

.EXAMPLE
  pwsh tools/measure-startup.ps1 -Folder E:\AssetManager
  pwsh tools/measure-startup.ps1 -Folder C:\Temp\am-portable -Publish
#>
param(
    [Parameter(Mandatory)] [string] $Folder,
    [switch] $Publish
)
$ErrorActionPreference = 'Stop'

if ($Publish) {
    $repo = Split-Path $PSScriptRoot -Parent
    dotnet publish (Join-Path $repo 'AssetManager/AssetManager.csproj') -c Release -f net10.0-windows10.0.19041.0 -p:Portable=true -o $Folder
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

$root = (Resolve-Path $Folder).Path
$data = Join-Path $root 'data'
$app = Get-ChildItem $root -Force | Where-Object { $_.FullName -ne $data }
$files = @($app | Where-Object { -not $_.PSIsContainer }) + @($app | Where-Object PSIsContainer | ForEach-Object { Get-ChildItem $_.FullName -Recurse -File -Force })
$dirs = @($app | Where-Object PSIsContainer) + @($app | Where-Object PSIsContainer | ForEach-Object { Get-ChildItem $_.FullName -Recurse -Directory -Force })

[pscustomobject]@{
    Folder         = $root
    Files          = $files.Count
    Folders        = $dirs.Count
    TopLevel       = $app.Count
    TopLevelFiles  = @($app | Where-Object { -not $_.PSIsContainer }).Count
    TopLevelDirs   = @($app | Where-Object PSIsContainer).Count
    SizeMB         = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB)
} | Format-List

$log = Get-ChildItem (Join-Path $data 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $log) { "No app log under $data\logs."; return }

$lines = Get-Content $log.FullName
$start = ($lines | Select-String -Pattern ' started; data folder ' | Select-Object -Last 1)
if (-not $start) { "No start found in $($log.Name)."; return }
$after = $lines[($start.LineNumber - 1)..($lines.Count - 1)]
function Get-Stamp([string] $line) { [datetimeoffset]::ParseExact($line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', $null) }

$t0 = Get-Stamp $start.Line
"Last start: $($t0.ToString('yyyy-MM-dd HH:mm:ss')) ($($log.Name))"
$index = $after | Where-Object { $_ -match 'Vector index:' } | Select-Object -First 1
if ($index) { 'Started -> vector index: {0:0.0} s' -f ((Get-Stamp $index) - $t0).TotalSeconds }
foreach ($l in $after | Where-Object { $_ -match 'AI server (\w+) ready \((\w+)\) in ([\d.,]+) s' }) {
    $null = $l -match 'AI server (\w+) ready \((\w+)\) in ([\d.,]+) s'
    'AI server {0} ({1}): loaded in {2} s, ready {3:0.0} s after start' -f $Matches[1], $Matches[2], $Matches[3], ((Get-Stamp $l) - $t0).TotalSeconds
}
