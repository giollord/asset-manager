using AssetManager.Core.Settings;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Settings_round_trip()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "settings.json");

        var store = new JsonAppSettingsStore(path);
        store.Update(s =>
        {
            s.CatalogPath = @"D:\cat.amdb";
            s.Theme = ThemeMode.Dark;
            s.ChatModelPath = @"D:\m.gguf";
        });

        var reloaded = new JsonAppSettingsStore(path).Current;
        Assert.Equal(@"D:\cat.amdb", reloaded.CatalogPath);
        Assert.Equal(ThemeMode.Dark, reloaded.Theme);
        Assert.Equal(@"D:\m.gguf", reloaded.ChatModelPath);
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = dir.Write("settings.json", "{ not json");
        Assert.Equal(ThemeMode.System, new JsonAppSettingsStore(path).Current.Theme);
        Assert.Equal(ThemeMode.System, new JsonAppSettingsStore(Path.Combine(dir.Path, "none.json")).Current.Theme);
    }
}
