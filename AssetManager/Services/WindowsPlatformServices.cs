using System.Diagnostics;
using AssetManager.Core.Platform;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;
using WinDataPackage = Windows.ApplicationModel.DataTransfer.DataPackage;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using Windows.Storage;

namespace AssetManager.Services;

/// <summary>Windows implementations of the platform services the shared UI uses (design D9).</summary>
public sealed class WindowsFilePicker : AssetManager.Core.Platform.IFilePicker
{
    public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (multiple)
            {
                var results = await Microsoft.Maui.Storage.FilePicker.Default.PickMultipleAsync();
                return (IReadOnlyList<string>)(results?.Where(r => r is not null).Select(r => r!.FullPath).ToList() ?? []);
            }
            var one = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync();
            return one is null ? [] : [one.FullPath];
        });
}

public sealed class WindowsFolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(CancellationToken ct = default) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        });

    internal static IntPtr MainWindowHandle() =>
        Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is MauiWinUIWindow w
            ? w.WindowHandle
            : throw new InvalidOperationException("The main window is not available.");
}

public sealed class WindowsShellService : IShellService
{
    public Task OpenAsync(string path, CancellationToken ct = default)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task RevealAsync(string path, CancellationToken ct = default)
    {
        // Explorer parses /select itself: only the path may be quoted. ArgumentList would quote "/select,<path>" as a
        // whole when the path has spaces, and Explorer then opens Documents instead.
        var psi = new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
        Process.Start(psi);
        return Task.CompletedTask;
    }
}

/// <summary>Puts files on the clipboard as a file list, so they paste into Explorer or a game engine.</summary>
public sealed class WindowsClipboardFileService : IClipboardFileService
{
    public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var items = new List<IStorageItem>();
            foreach (var p in paths) items.Add(await StorageFile.GetFileFromPathAsync(p));
            var package = new WinDataPackage { RequestedOperation = WinDataPackageOperation.Copy };
            package.SetStorageItems(items);
            WinClipboard.SetContent(package);
            WinClipboard.Flush(); // keep the content after the app exits
        });
}
