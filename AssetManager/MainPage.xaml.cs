using AssetManager.Core.Platform;
using Microsoft.AspNetCore.Components.WebView;

namespace AssetManager;

public partial class MainPage : ContentPage
{
    public MainPage(FileDropBroker drops)
    {
        InitializeComponent();
        blazorWebView.BlazorWebViewInitialized += (_, e) => HookFileDrops(e, drops);
    }

    /// <summary>
    /// Drag and drop (design D9): the page's drop handler posts the dropped files with
    /// <c>postMessageWithAdditionalObjects</c>; WebView2 hands them over as <c>CoreWebView2File</c> with real paths.
    /// </summary>
    private static void HookFileDrops(BlazorWebViewInitializedEventArgs e, FileDropBroker drops)
    {
#if WINDOWS
        // MAUI before 10.0.120 creates the WebView2 control with AllowDrop = false, so files dragged in from Explorer
        // never reach the page (fixed upstream in 10.0.120; harmless to set again).
        e.WebView.AllowDrop = true;
        // Drag-out (design D7): browse.js posts "DragOut:<id>"; the host starts a native drag of the files.
        var dragOut = IPlatformApplication.Current!.Services.GetRequiredService<Services.WindowsDragOutService>();
        dragOut.Attach(e.WebView);
        e.WebView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            string? message;
            try { message = args.TryGetWebMessageAsString(); }
            catch (ArgumentException) { return; }
            if (message?.StartsWith(Services.WindowsDragOutService.MessagePrefix) == true)
            {
                // Leave the WebView2 event handler before starting the drag.
                e.WebView.DispatcherQueue.TryEnqueue(() => dragOut.Start(e.WebView, message));
                return;
            }
            if (message != "FilesDropped" || args.AdditionalObjects is not { } objects) return;
            if (dragOut.IsOwnDrop) return; // our own drag-out dropped back onto the page
            var paths = objects.OfType<Microsoft.Web.WebView2.Core.CoreWebView2File>().Select(f => f.Path).ToList();
            if (paths.Count > 0) drops.Publish(paths);
        };
#endif
    }
}
