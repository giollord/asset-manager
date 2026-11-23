namespace AssetManager.Core.Platform;

/// <summary>Thrown by a host that cannot provide a platform feature (for example the Web dev host).</summary>
public sealed class PlatformFeatureNotSupportedException(string feature)
    : NotSupportedException($"{feature} is not supported on this host.")
{
    public string Feature { get; } = feature;
}

/// <summary>Places files on the OS clipboard as a file list (paste into Explorer or an engine).</summary>
public interface IClipboardFileService
{
    Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
}

public interface IFolderPicker
{
    /// <summary>Returns the chosen folder or null when cancelled.</summary>
    Task<string?> PickFolderAsync(CancellationToken ct = default);
}

public interface IFilePicker
{
    /// <summary>Returns the chosen files; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default);
}

public interface IShellService
{
    /// <summary>Opens a file with the OS default application.</summary>
    Task OpenAsync(string path, CancellationToken ct = default);

    /// <summary>Reveals a file in the file explorer.</summary>
    Task RevealAsync(string path, CancellationToken ct = default);
}

/// <summary>A mounted volume identified independently of its drive letter.</summary>
public sealed record VolumeInfo(string Id, string Label, string RootPath);

public interface IVolumeService
{
    /// <summary>Currently mounted volumes.</summary>
    IReadOnlyList<VolumeInfo> GetVolumes();

    /// <summary>The volume that contains <paramref name="path"/>, or null when unknown.</summary>
    VolumeInfo? GetVolumeForPath(string path);

    /// <summary>The current root (e.g. <c>F:\</c>) of a volume id, or null when it is not mounted.</summary>
    string? GetRoot(string volumeId);

    /// <summary>Raised when volumes are mounted, removed or re-lettered.</summary>
    event Action? VolumesChanged;
}

/// <summary>
/// Files dropped onto the app window (design D9): the host forwards the real paths of a WebView2 drop here, and the
/// import page takes them. Paths dropped while the import page is closed wait in <see cref="TakePending"/>.
/// </summary>
public sealed class FileDropBroker
{
    private readonly object _gate = new();
    private readonly List<string> _pending = [];

    /// <summary>Raised on any thread after paths were added to the pending list.</summary>
    public event Action? Dropped;

    public void Publish(IEnumerable<string> paths)
    {
        lock (_gate)
            _pending.AddRange(paths.Where(p => !string.IsNullOrWhiteSpace(p)));
        Dropped?.Invoke();
    }

    /// <summary>Returns and clears the paths dropped so far.</summary>
    public IReadOnlyList<string> TakePending()
    {
        lock (_gate)
        {
            var result = _pending.ToList();
            _pending.Clear();
            return result;
        }
    }
}
