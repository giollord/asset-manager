using AssetManager.Core.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Color = Windows.UI.Color;

namespace AssetManager.Services;

/// <summary>
/// The window's frame on Windows (design D1, D2). The page extends into the title bar; the top strip of the window is
/// the system's caption area, so it drags, maximizes on double-click and opens the system menu on right-click. The app
/// bar's interactive elements are reported from the page and become passthrough regions, so they take clicks.
/// </summary>
/// <remarks>
/// WebView2's own <c>app-region</c> support (<c>IsNonClientRegionSupportEnabled</c>) has no effect in the WinUI
/// WebView2 that MAUI uses: it hosts WebView2 as a composition visual, and the system caption strip wins. Task 1 of
/// the shell-refinements change verified this; design D1's fallback (<see cref="InputNonClientPointerSource"/>) is used.
/// </remarks>
public sealed class WindowsWindowChrome : IWindowChrome
{
    private Microsoft.UI.Xaml.Window? _window;
    private AppWindowTitleBar? _titleBar;
    private double _scale = 1;

    public bool IsCustomTitleBar { get; private set; }

    public double CaptionButtonsWidth => IsCustomTitleBar && _titleBar is not null ? _titleBar.RightInset / _scale : 0;

    public event Action? Changed;

    /// <summary>Called from <c>OnWindowCreated</c>. Without title bar customisation the standard title bar stays.</summary>
    public void Attach(Microsoft.UI.Xaml.Window window)
    {
        _window = window;
        // The display scale is known once the window's content is laid out, which is when the title row is collapsed.
        if (!WinUI.TitleBarSetup.Apply(window, WatchScale)) return;
        _titleBar = window.AppWindow.TitleBar;
        IsCustomTitleBar = true;

        window.AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange && !e.DidPresenterChange) return;
            SetDragArea();
            Changed?.Invoke();
        };
    }

    /// <summary>
    /// The strip that moves the window: the full width at the tall title bar's height, which is the app bar's. MAUI sets
    /// the drag rectangles from its own (collapsed) 32 DIP title row whenever the window resizes, so the lower part of
    /// the app bar would not drag. Set again after MAUI's handlers on every resize, maximize and display scale change.
    /// </summary>
    private void SetDragArea()
    {
        if (_window is null || _titleBar is null) return;
        _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            _titleBar.SetDragRectangles([new RectInt32(0, 0, _window.AppWindow.ClientSize.Width, _titleBar.Height)]));
    }

    private bool _watchingScale;

    private void WatchScale()
    {
        if (_watchingScale || _window?.Content?.XamlRoot is not { } root) return;
        _watchingScale = true;
        _scale = root.RasterizationScale;
        root.Changed += (_, _) =>
        {
            if (root.RasterizationScale == _scale) return;
            _scale = root.RasterizationScale;
            SetDragArea();
            Changed?.Invoke();
        };
        SetDragArea();
        Changed?.Invoke();
    }

    public void SetCaptionColors(string foreground, string hoverBackground)
    {
        if (_titleBar is not { } bar || !TryParse(foreground, out var fg) || !TryParse(hoverBackground, out var hover)) return;
        OnUiThread(() =>
        {
            bar.ButtonForegroundColor = fg;
            bar.ButtonHoverForegroundColor = fg;
            bar.ButtonPressedForegroundColor = fg;
            bar.ButtonInactiveForegroundColor = Color.FromArgb((byte)(fg.A / 2), fg.R, fg.G, fg.B);
            bar.ButtonHoverBackgroundColor = hover;
            bar.ButtonPressedBackgroundColor = Color.FromArgb((byte)Math.Min(255, hover.A * 2), hover.R, hover.G, hover.B);
        });
    }

    public void SetInteractiveRegions(IReadOnlyList<ChromeRect> regions)
    {
        if (!IsCustomTitleBar || _window is null) return;
        var scale = _scale;
        var rects = regions
            .Select(r => new RectInt32((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale),
                (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale)))
            .Where(r => r.Width > 0 && r.Height > 0)
            .ToArray();
        OnUiThread(() =>
        {
            var source = InputNonClientPointerSource.GetForWindowId(_window.AppWindow.Id);
            if (rects.Length == 0) source.ClearRegionRects(NonClientRegionKind.Passthrough);
            else source.SetRegionRects(NonClientRegionKind.Passthrough, rects);
        });
    }

    private void OnUiThread(Action action)
    {
        if (_window?.DispatcherQueue is not { } queue) return;
        if (queue.HasThreadAccess) action();
        else queue.TryEnqueue(() => action());
    }

    /// <summary>Parses <c>#rgb</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>.</summary>
    internal static bool TryParse(string css, out Color color)
    {
        color = default;
        var hex = css.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length == 6) hex += "ff";
        if (hex.Length != 8 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
        return true;
    }
}
