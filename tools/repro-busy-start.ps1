<#
.SYNOPSIS
  Cold-starts a portable Asset Manager folder and reports every few seconds whether its window responds.

.DESCRIPTION
  Drops the OS file cache for every file under -Folder (opening a file without buffering makes Windows discard its
  cached pages), starts <Folder>\AssetManager.exe, and prints the window's "responding" state and the app's disk reads
  per second until -Seconds have passed. The app is closed at the end unless -KeepRunning is given.
  Use it to reproduce a start from a slow drive while background work is pending, e.g. an interrupted import.
  With -Shots, each sample also saves a picture of the window (even when other windows cover it) to that folder.

.EXAMPLE
  pwsh tools/repro-busy-start.ps1 -Folder E:\AM-test -Seconds 90
#>
param(
    [Parameter(Mandatory)] [string] $Folder,
    [int] $Seconds = 90,
    [int] $Interval = 5,
    [switch] $KeepRunning,
    [string] $Shots
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $Folder).Path
$exe = Join-Path $root 'AssetManager.exe'
if (-not (Test-Path $exe)) { throw "No AssetManager.exe in $root" }
if (Get-Process AssetManager -ErrorAction SilentlyContinue | Where-Object Path -eq $exe) { throw "$exe is already running." }

$sw = [Diagnostics.Stopwatch]::StartNew()
$purged = 0
foreach ($f in [IO.Directory]::EnumerateFiles($root, '*', 'AllDirectories')) {
    try {
        $fs = [IO.FileStream]::new($f, 'Open', 'Read', 'ReadWrite', 4096, [IO.FileOptions]0x20000000) # FILE_FLAG_NO_BUFFERING
        $fs.Dispose(); $purged++
    } catch { }
}
'Dropped the cache of {0} files in {1:0.0} s' -f $purged, $sw.Elapsed.TotalSeconds

if ($Shots) {
    New-Item -ItemType Directory -Force $Shots | Out-Null
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class ReproWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
}
function Save-Shot([IntPtr] $hwnd, [string] $path) {
    $r = New-Object ReproWin+RECT
    if (-not [ReproWin]::GetWindowRect($hwnd, [ref] $r) -or $r.R -le $r.L) { return }
    $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [void][ReproWin]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT: includes the WebView
    $g.ReleaseHdc($hdc); $g.Dispose()
    $small = New-Object Drawing.Bitmap $bmp, ([int]($bmp.Width / 2)), ([int]($bmp.Height / 2))
    $small.Save($path); $small.Dispose(); $bmp.Dispose()
}

$p = Start-Process $exe -PassThru -WorkingDirectory $root
$sw.Restart()
$notResponding = 0
$lastReads = $null
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep -Seconds $Interval
    $p.Refresh()
    if ($p.HasExited) { "exited with code $($p.ExitCode)"; break }
    $reads = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").ReadOperationCount
    $rate = if ($null -ne $lastReads) { [math]::Round(($reads - $lastReads) / $Interval) } else { '-' }
    $lastReads = $reads
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { '{0,4} s: no window yet' -f [int]$sw.Elapsed.TotalSeconds; continue }
    if (-not $p.Responding) { $notResponding++ }
    if ($Shots) { Save-Shot $p.MainWindowHandle (Join-Path $Shots ('{0:000}.png' -f [int]$sw.Elapsed.TotalSeconds)) }
    '{0,4} s: responding={1}  reads/s={2}' -f [int]$sw.Elapsed.TotalSeconds, $p.Responding, $rate
}
if (-not $KeepRunning -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
"Not responding in $notResponding sample(s)."
