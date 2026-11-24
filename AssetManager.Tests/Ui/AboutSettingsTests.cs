using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Fixtures;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>Spec "Data folder shown in Settings".</summary>
public class AboutSettingsTests : BunitContext
{
    private sealed class RecordingShell : IShellService
    {
        public List<string> Opened { get; } = [];
        public Task OpenAsync(string path, CancellationToken ct = default) { Opened.Add(path); return Task.CompletedTask; }
        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private (IRenderedComponent<AboutSettings> About, RecordingShell Shell) Render(AppDataLocation location)
    {
        var shell = new RecordingShell();
        Services.AddMudServices();
        Services.AddSingleton(location);
        Services.AddSingleton<IShellService>(shell);
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        return (Render<AboutSettings>(), shell);
    }

    [Fact]
    public void Portable_mode_shows_the_data_folder_and_opens_it()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "data");
        var (about, shell) = Render(new AppDataLocation(root, DataMode.Portable));

        Assert.StartsWith("Portable", about.Find("#about-mode").TextContent);
        Assert.Equal(root, about.Find("#about-data-folder").TextContent.Trim());
        Assert.Equal(AppVersion.Current, about.Find("#about-version").TextContent);
        Assert.Empty(about.FindAll("#about-fallback"));

        about.Find("#about-open-folder").Click();

        Assert.Equal([root], shell.Opened);
    }

    [Fact]
    public void Fallback_explains_why()
    {
        var (about, _) = Render(new AppDataLocation(@"C:\Users\me\AppData\Local\AssetManager", DataMode.Fallback,
            @"The app folder cannot be written (C:\Program Files\AssetManager\data): Access denied."));

        Assert.Contains("Access denied", about.Find("#about-fallback").TextContent);
        Assert.Contains("user profile", about.Find("#about-mode").TextContent);
    }
}
