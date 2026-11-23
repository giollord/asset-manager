using AssetManager.Core.Settings;
using AssetManager.Shared.Components;
using AssetManager.Tests.Fixtures;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class ThemeToggleTests : BunitContext
{
    public ThemeToggleTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Clicking_cycles_theme_and_persists_it()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new JsonAppSettingsStore(path);
        Services.AddSingleton<IAppSettingsStore>(store);
        Render<MudBlazor.MudPopoverProvider>();

        var cut = Render<ThemeToggle>();
        cut.Find("button").Click();
        Assert.Equal(ThemeMode.Light, store.Current.Theme);
        cut.Find("button").Click();
        Assert.Equal(ThemeMode.Dark, store.Current.Theme);

        // A fresh store (as after an app restart) reads the persisted choice.
        Assert.Equal(ThemeMode.Dark, new JsonAppSettingsStore(path).Current.Theme);

        cut.Find("button").Click();
        Assert.Equal(ThemeMode.System, store.Current.Theme);
    }
}
