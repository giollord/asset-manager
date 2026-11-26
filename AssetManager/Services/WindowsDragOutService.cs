using AssetManager.Core.Platform;
using AssetManager.Data.Services;
using AssetManager.Processing.Export;
using ImageMagick;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using DataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace AssetManager.Services;

/// <summary>
/// Drags catalog files out of the app (design D7, as proven by the task 1.1 spike). The page (browse.js) posts
/// "DragOut:&lt;assetId&gt;" when a card is pressed and moved; the host copies the files of that card, or of the whole
/// selection when the card is selected, into a new temporary folder and starts a WinUI drag from the WebView2 control
/// with the last pointer point it saw.
/// </summary>
/// <remarks>
/// OLE <c>DoDragDrop</c> does not work here: the WinUI WebView2 keeps the mouse. <c>DragUI.SetContentFromDataPackage</c>
/// is not supported in WinUI 3 desktop, so the drag image is the card thumbnail as a software bitmap.
/// </remarks>
public sealed class WindowsDragOutService(DragOutPlanner planner, DragOutState state, PreviewQueries previews, ILogger<WindowsDragOutService> logger)
    : IDragOutService
{
    public const string MessagePrefix = "DragOut:";

    /// <summary>Show "Preparing N files…" only when copying takes longer than this.</summary>
    private static readonly TimeSpan StatusDelay = TimeSpan.FromMilliseconds(150);

    private PointerPoint? _lastPoint;
    private IReadOnlyList<string>? _pending;
    private byte[]? _visual;
    private bool _preparing;
    private DateTime _endedAt;

    public bool IsSupported => true;

    /// <summary>True while our own drag runs and shortly after, so a drop of it back onto the page is not an import.</summary>
    public bool IsOwnDrop => _preparing || _pending is not null || DateTime.UtcNow - _endedAt < TimeSpan.FromSeconds(3);

    public void Attach(WebView2 view)
    {
        // The WebView2 control handles pointer input itself; handledEventsToo lets us see it anyway.
        PointerEventHandler track = (_, e) => _lastPoint = e.GetCurrentPoint(view);
        view.AddHandler(UIElement.PointerPressedEvent, track, true);
        view.AddHandler(UIElement.PointerMovedEvent, track, true);
        view.AddHandler(UIElement.PointerReleasedEvent, track, true);
        view.DragStarting += async (_, args) =>
        {
            if (_pending is not { } files) return;
            var deferral = args.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var f in files) items.Add(await StorageFile.GetFileFromPathAsync(f));
                args.Data.SetStorageItems(items);
                args.Data.RequestedOperation = DataPackageOperation.Copy;
                args.AllowedOperations = DataPackageOperation.Copy;
                await SetVisualAsync(args);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Drag-out: filling the drag failed");
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    /// <summary>Handles the page's message; call it outside the WebView2 event handler (queued on the dispatcher).</summary>
    public async void Start(WebView2 view, string message)
    {
        if (!long.TryParse(message.AsSpan(MessagePrefix.Length), out var pressed)) return;
        if (_lastPoint is not { Properties.IsLeftButtonPressed: true } || _preparing || _pending is not null)
        {
            logger.LogDebug("Drag-out of asset {Asset} ignored: pointer {Pointer}, preparing {Preparing}, dragging {Dragging}",
                pressed, _lastPoint is null ? "unseen" : _lastPoint.Properties.IsLeftButtonPressed ? "pressed" : "released", _preparing, _pending is not null);
            return;
        }
        var ids = DragOutPlanner.Targets(pressed, state.Selection);
        _preparing = true;
        try
        {
            _ = ShowStatusLaterAsync(ids.Count);
            var visual = VisualAsync(pressed);
            var files = await planner.ResolveAsync(ids);
            _visual = await visual;
            _preparing = false;
            state.SetStatus(null);
            if (files.Problem is { } problem) state.Report(problem);
            if (files.Files.Count == 0) return;
            if (_lastPoint is not { Properties.IsLeftButtonPressed: true } point)
            {
                state.Report("The files were ready after the mouse button was released; drag the card again.");
                return;
            }
            _pending = files.Files;
            view.ReleasePointerCaptures();
            var result = await view.StartDragAsync(point);
            logger.LogInformation("Drag-out of {Count} files from {Assets} assets finished with {Result}", files.Files.Count, ids.Count, result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Drag-out failed for asset {Asset}", pressed);
            state.Report($"Could not drag the files: {e.Message}");
        }
        finally
        {
            _preparing = false;
            state.SetStatus(null);
            _pending = null;
            _visual = null;
            _endedAt = DateTime.UtcNow;
        }
    }

    private async Task ShowStatusLaterAsync(int count)
    {
        await Task.Delay(StatusDelay);
        if (_preparing) state.SetStatus($"Preparing {count} file{(count == 1 ? "" : "s")}…");
    }

    /// <summary>The pressed card's thumbnail as a small PNG (any stored format), or null.</summary>
    private async Task<byte[]?> VisualAsync(long assetId)
    {
        try
        {
            if (await previews.ThumbnailAsync(assetId) is not { } thumb) return null;
            return await Task.Run(() =>
            {
                using var image = new MagickImage(thumb.Bytes);
                image.Resize(new MagickGeometry(96, 96));
                return image.ToByteArray(MagickFormat.Png);
            });
        }
        catch (Exception e) when (e is MagickException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task SetVisualAsync(Microsoft.UI.Xaml.DragStartingEventArgs args)
    {
        if (_visual is not { } png) return; // the default visual (a snapshot of the page) stays
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            args.DragUI.SetContentFromSoftwareBitmap(bitmap);
        }
        catch (Exception e)
        {
            // An exception in DragStarting would cancel the drag; keep the default visual instead.
            logger.LogWarning(e, "Drag-out: custom drag visual failed; keeping the default");
        }
    }
}
