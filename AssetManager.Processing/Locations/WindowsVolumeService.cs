using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AssetManager.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Locations;

/// <summary>
/// Windows volumes identified by their volume serial number (<c>GetVolumeInformationW</c>), so a drive that comes
/// back under another letter is still recognized. Drives are polled every few seconds; <see cref="VolumesChanged"/>
/// fires when a volume is mounted, removed or re-lettered. Network and SUBST drives are not treated as volumes
/// (their paths are stored as absolute paths).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVolumeService : IVolumeService, IDisposable
{
    private readonly ILogger _log;
    private readonly Timer _timer;
    private volatile IReadOnlyList<VolumeInfo> _volumes;
    private int _polling;

    public WindowsVolumeService(ILogger<WindowsVolumeService>? logger = null, TimeSpan? pollInterval = null)
    {
        _log = logger ?? (ILogger)NullLogger.Instance;
        _volumes = Probe();
        var interval = pollInterval ?? TimeSpan.FromSeconds(3);
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    public event Action? VolumesChanged;

    public IReadOnlyList<VolumeInfo> GetVolumes() => _volumes;

    public VolumeInfo? GetVolumeForPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return _volumes
            .Where(v => full.StartsWith(v.RootPath, StringComparison.OrdinalIgnoreCase))
            .MaxBy(v => v.RootPath.Length);
    }

    public string? GetRoot(string volumeId) =>
        _volumes.FirstOrDefault(v => string.Equals(v.Id, volumeId, StringComparison.OrdinalIgnoreCase))?.RootPath;

    /// <summary>Re-reads the drive list now (also done by the poll timer).</summary>
    public void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var now = Probe();
            var before = _volumes;
            if (now.Count == before.Count && now.Zip(before).All(p => p.First == p.Second)) return;
            _volumes = now;
            _log.LogInformation("Volumes changed: {Volumes}", string.Join(", ", now.Select(v => $"{v.RootPath}={v.Id}")));
            VolumesChanged?.Invoke();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Volume poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    public void Dispose() => _timer.Dispose();

    private static List<VolumeInfo> Probe()
    {
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown) continue;
            var root = d.RootDirectory.FullName;
            if (IsSubst(root)) continue;
            var label = new StringBuilder(261);
            var fs = new StringBuilder(261);
            if (!GetVolumeInformationW(root, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity)) continue; // not ready
            var id = serial.ToString("X8");
            if (list.Any(v => v.Id == id)) continue; // cloned disks share a serial; first one wins
            list.Add(new VolumeInfo(id, label.ToString(), root));
        }
        return list.OrderBy(v => v.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsSubst(string root)
    {
        var target = new StringBuilder(1024);
        return QueryDosDeviceW(root.TrimEnd('\\'), target, target.Capacity) > 0 && target.ToString().StartsWith(@"\??\", StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder fileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int ucchMax);
}

/// <summary>Volume service for hosts without volume identity: every path is stored as an absolute path.</summary>
public sealed class NoVolumeService : IVolumeService
{
    public event Action? VolumesChanged { add { } remove { } }

    public IReadOnlyList<VolumeInfo> GetVolumes() => [];

    public VolumeInfo? GetVolumeForPath(string path) => null;

    public string? GetRoot(string volumeId) => null;
}
