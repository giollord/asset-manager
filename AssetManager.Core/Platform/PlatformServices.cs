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
/// <summary>Dragging catalog files out of the app into Explorer or an engine (design D7); only the desktop host supports it.</summary>
public interface IDragOutService
{
    bool IsSupported { get; }
}

/// <summary>
/// Shared state of a drag-out (design D7): Browse publishes its selection (a drag of a selected card takes the whole
/// selection); the host reports progress ("Preparing N files…") and problems, which the layout shows.
/// </summary>
public sealed class DragOutState
{
    /// <summary>The assets selected in Browse (live; read on the UI thread).</summary>
    public IReadOnlyCollection<long> Selection { get; set; } = [];

    /// <summary>Raised with a status text while files are prepared, and with null when done.</summary>
    public event Action<string?>? StatusChanged;

    /// <summary>Raised with a message for the user (e.g. unavailable assets).</summary>
    public event Action<string>? Problem;

    public void SetStatus(string? status) => StatusChanged?.Invoke(status);

    public void Report(string message) => Problem?.Invoke(message);
}

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

/// <summary>A rectangle in CSS pixels, relative to the top-left corner of the page.</summary>
public readonly record struct ChromeRect(double X, double Y, double Width, double Height);

/// <summary>
/// The desktop window's frame (spec "window-chrome", design D2): whether the app bar is the window's title bar, how much
/// room the system caption buttons take at its right end, and the colours of those buttons.
/// </summary>
public interface IWindowChrome
{
    /// <summary>True when the app bar is the window's title bar and the system draws its caption buttons over it.</summary>
    bool IsCustomTitleBar { get; }

    /// <summary>Width of the system caption buttons in CSS pixels; 0 without a custom title bar.</summary>
    double CaptionButtonsWidth { get; }

    /// <summary>Raised when <see cref="CaptionButtonsWidth"/> changes (display scale or window size).</summary>
    event Action? Changed;

    /// <summary>Colours of the caption buttons, as CSS hex colours (<c>#rrggbb</c> or <c>#rrggbbaa</c>).</summary>
    void SetCaptionColors(string foreground, string hoverBackground);

    /// <summary>
    /// The interactive elements of the app bar. The rest of the title bar strip moves the window; these rectangles take
    /// clicks like the rest of the page.
    /// </summary>
    void SetInteractiveRegions(IReadOnlyList<ChromeRect> regions);
}

/// <summary>A host without a window frame of its own (the web dev host in a browser tab): nothing to reserve or colour.</summary>
public sealed class NoWindowChrome : IWindowChrome
{
    public bool IsCustomTitleBar => false;
    public double CaptionButtonsWidth => 0;
    public event Action? Changed { add { } remove { } }
    public void SetCaptionColors(string foreground, string hoverBackground) { }
    public void SetInteractiveRegions(IReadOnlyList<ChromeRect> regions) { }
}
