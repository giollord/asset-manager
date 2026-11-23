using AssetManager.Core.Platform;

namespace AssetManager.Web.Services;

/// <summary>
/// Dev-host stubs: the browser cannot reach the OS clipboard file list, native pickers or Explorer,
/// so every call reports "not supported" (surfaced to the user as a snackbar by UiActions).
/// </summary>
public sealed class UnsupportedPlatformServices : IClipboardFileService, IFolderPicker, IFilePicker, IShellService
{
    public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        throw new PlatformFeatureNotSupportedException("Copying files to the clipboard");

    public Task<string?> PickFolderAsync(CancellationToken ct = default) =>
        throw new PlatformFeatureNotSupportedException("The folder picker");

    public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default) =>
        throw new PlatformFeatureNotSupportedException("The file picker");

    public Task OpenAsync(string path, CancellationToken ct = default) =>
        throw new PlatformFeatureNotSupportedException("Opening files externally");

    public Task RevealAsync(string path, CancellationToken ct = default) =>
        throw new PlatformFeatureNotSupportedException("Revealing files in Explorer");
}
