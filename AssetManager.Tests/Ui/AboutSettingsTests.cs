using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Core.Updates;
using AssetManager.Tests.Core;
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
    private readonly TempDir _dir = new();
    private JsonAppSettingsStore? _settings;
    private UpdateChecker? _updates;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _dir.Dispose();
        base.Dispose(disposing);
    }

    private sealed class RecordingShell : IShellService
    {
        public List<string> Opened { get; } = [];
        public Task OpenAsync(string path, CancellationToken ct = default) { Opened.Add(path); return Task.CompletedTask; }
        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private (IRenderedComponent<AboutSettings> About, RecordingShell Shell) Render(AppDataLocation location, UpdateCheckerTests.FakeGitHub? github = null)
    {
        var shell = new RecordingShell();
        _settings = new JsonAppSettingsStore(Path.Combine(_dir.Path, "settings.json"));
        _updates = new UpdateChecker(_settings, github ?? new UpdateCheckerTests.FakeGitHub()) { CurrentVersion = "1.2.0" };
        Services.AddSingleton<IAppSettingsStore>(_settings);
        Services.AddSingleton(_updates);
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
        Assert.Contains("selectable", about.Find("#about-version").ClassList);
        Assert.Contains("selectable", about.Find("#about-data-folder").ClassList);
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

    /// <summary>Spec "Newer release": "1.3.0 available" links to the release page.</summary>
    [Fact]
    public void Newer_release_is_shown_with_a_link()
    {
        var github = new UpdateCheckerTests.FakeGitHub(() => UpdateCheckerTests.FakeGitHub.Release("v1.3.0"));
        var (about, shell) = Render(new AppDataLocation(_dir.Path, DataMode.Development), github);
        Assert.Empty(about.FindAll("#about-update"));
        Assert.True(about.Find("#about-check-now").HasAttribute("disabled")); // off by default: no "Check now"

        about.Find("input#about-update-check, #about-update-check input").Change(true);
        about.WaitForAssertion(() => Assert.False(about.Find("#about-check-now").HasAttribute("disabled")));
        Assert.Empty(github.Requests); // turning it on alone makes no request
        about.Find("#about-check-now").Click();

        about.WaitForAssertion(() => Assert.Equal("1.3.0 available", about.Find("#about-update").TextContent.Trim()));
        Assert.Single(github.Requests);
        about.Find("#about-update").Click();
        Assert.Equal(["https://github.com/giollord/asset-manager/releases/tag/v1.3.0"], shell.Opened);
        Assert.StartsWith("Checked", about.Find("#about-update-status").TextContent);
    }

    /// <summary>Spec "Offline": no error, only a note that the last check failed.</summary>
    [Fact]
    public void Offline_check_shows_a_note()
    {
        var (about, _) = Render(new AppDataLocation(_dir.Path, DataMode.Development));
        _settings!.Update(s => s.UpdateCheckEnabled = true);
        about.WaitForAssertion(() => Assert.False(about.Find("#about-check-now").HasAttribute("disabled")));

        about.Find("#about-check-now").Click();

        about.WaitForAssertion(() => Assert.StartsWith("The last check failed", about.Find("#about-update-status").TextContent));
        Assert.Empty(about.FindAll("#about-update"));
    }
}
