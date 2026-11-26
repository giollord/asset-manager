using AssetManager.Core;
using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Data.Jobs;
using AssetManager.Shared.Layout;
using AssetManager.Tests.Fixtures;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The shell around every page: app bar, window chrome (spec "window-chrome") and theme.</summary>
public class MainLayoutTests : BunitContext
{
    private readonly TempDir _dir = FixtureBuilder.TempDir();

    public sealed class FakeChrome : IWindowChrome
    {
        public bool IsCustomTitleBar { get; set; } = true;
        public double CaptionButtonsWidth { get; set; } = 138.4;
        public event Action? Changed;
        public List<(string Foreground, string Hover)> Colors { get; } = [];
        public void SetCaptionColors(string foreground, string hoverBackground)
        {
            lock (Colors) Colors.Add((foreground, hoverBackground));
        }
        /// <summary>Written on the renderer's thread and read on the test's, hence the lock.</summary>
        public string? LastForeground { get { lock (Colors) return Colors.Count == 0 ? null : Colors[^1].Foreground; } }
        public void SetInteractiveRegions(IReadOnlyList<ChromeRect> regions) { }
        public void RaiseChanged() => Changed?.Invoke();
    }

    private sealed class NoShell : IShellService
    {
        public Task OpenAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    public MainLayoutTests()
    {
        Services.AddMudServices();
        var settings = new JsonAppSettingsStore(Path.Combine(_dir.Path, "settings.json"));
        Services.AddSingleton<IAppSettingsStore>(settings);
        Services.AddSingleton(new AssetManager.Core.Updates.UpdateChecker(settings));
        Services.AddSingleton<IShellService>(new NoShell());
        Services.AddSingleton(new AppStartup([], NullLogger<AppStartup>.Instance));
        Services.AddSingleton(new StartupSignals());
        Services.AddSingleton<FileDropBroker>();
        Services.AddSingleton<DragOutState>();
        Services.AddSingleton<IProgressHub>(new ProgressHub());
        Services.AddSingleton<AssetManager.Shared.Services.WebViewModelRenderer>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dir.Dispose();
        base.Dispose(disposing);
    }

    [Fact]
    public void App_bar_reserves_the_caption_buttons_width_and_follows_changes()
    {
        var chrome = new FakeChrome();
        Services.AddSingleton<IWindowChrome>(chrome);

        var cut = Render<MainLayout>();
        Assert.Contains("padding-right: 139px", cut.Find(".mud-appbar").GetAttribute("style"));

        chrome.CaptionButtonsWidth = 207;
        chrome.RaiseChanged();
        cut.WaitForAssertion(() => Assert.Contains("padding-right: 207px", cut.Find(".mud-appbar").GetAttribute("style")));
    }

    [Fact]
    public void Browser_host_reserves_nothing()
    {
        Services.AddSingleton<IWindowChrome>(new NoWindowChrome());

        var cut = Render<MainLayout>();
        Assert.DoesNotContain("padding-right", cut.Find(".mud-appbar").GetAttribute("style") ?? "");
    }

    [Fact]
    public void App_bar_has_no_theme_switch()
    {
        Services.AddSingleton<IWindowChrome>(new NoWindowChrome());

        var cut = Render<MainLayout>();
        // Spec "Dark and light theme": the theme is chosen in Settings → Appearance. With no job running, the app bar
        // holds only the navigation toggle (spec "Idle app bar").
        var buttons = cut.FindAll(".mud-appbar button");
        Assert.Equal(["Toggle navigation"], buttons.Select(b => b.GetAttribute("aria-label")).ToArray());
    }

    [Fact]
    public void Caption_colours_follow_the_theme()
    {
        var chrome = new FakeChrome();
        Services.AddSingleton<IWindowChrome>(chrome);
        var store = Services.GetRequiredService<IAppSettingsStore>();
        store.Update(s => s.Theme = ThemeMode.Dark);

        var cut = Render<MainLayout>();
        var dark = new MudBlazor.MudTheme().PaletteDark.AppbarText.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA);
        cut.WaitForAssertion(() => Assert.Equal(dark, chrome.LastForeground));

        // The settings change reaches the layout through InvokeAsync, so it may land after Update returns.
        store.Update(s => s.Theme = ThemeMode.Light);
        var light = new MudBlazor.MudTheme().PaletteLight.AppbarText.ToString(MudBlazor.Utilities.MudColorOutputFormats.HexA);
        cut.WaitForAssertion(() => Assert.Equal(light, chrome.LastForeground));
    }
}

public class NoWindowChromeTests
{
    [Fact]
    public void Web_host_has_no_custom_title_bar()
    {
        IWindowChrome chrome = new NoWindowChrome();
        Assert.False(chrome.IsCustomTitleBar);
        Assert.Equal(0, chrome.CaptionButtonsWidth);
        chrome.Changed += () => throw new InvalidOperationException("never raised");
        chrome.SetCaptionColors("#ffffff", "#ffffff20");
        chrome.SetInteractiveRegions([new ChromeRect(0, 0, 10, 10)]);
    }
}
