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

    [Fact]
    public void Paths_inside_the_data_folder_are_stored_relative()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "games.amdb");

        new JsonAppSettingsStore(path).Update(s =>
        {
            s.CatalogPath = Path.Combine(dir.Path, "catalog.amdb");
            s.ChatModelPath = Path.Combine(dir.Path, "models", "chat.gguf");
            s.MmprojPath = outside;
        });

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("catalog.amdb", (string?)json["CatalogPath"]);
        Assert.Equal(Path.Combine("models", "chat.gguf"), (string?)json["ChatModelPath"]);
        Assert.Equal(outside, (string?)json["MmprojPath"]); // outside the data folder: kept as given
        var reloaded = new JsonAppSettingsStore(path).Current;
        Assert.Equal(Path.Combine(dir.Path, "catalog.amdb"), reloaded.CatalogPath);
        Assert.Equal(Path.Combine(dir.Path, "models", "chat.gguf"), reloaded.ChatModelPath);
    }

    [Fact]
    public void Moved_data_folder_keeps_its_catalog_and_models()
    {
        using var dir = FixtureBuilder.TempDir();
        var old = Path.Combine(dir.Path, "E", "Tools", "AssetManager", "data");
        new JsonAppSettingsStore(Path.Combine(old, "settings.json")).Update(s =>
        {
            s.CatalogPath = Path.Combine(old, "catalog.amdb");
            s.EmbeddingModelPath = Path.Combine(old, "models", "embed.gguf");
        });

        var moved = Path.Combine(dir.Path, "F", "AssetManager", "data");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(old, moved);
        var settings = new JsonAppSettingsStore(Path.Combine(moved, "settings.json")).Current;

        Assert.Equal(Path.Combine(moved, "catalog.amdb"), settings.CatalogPath);
        Assert.Equal(Path.Combine(moved, "models", "embed.gguf"), settings.EmbeddingModelPath);
    }

    [Fact]
    public void Old_settings_with_absolute_paths_still_load()
    {
        using var dir = FixtureBuilder.TempDir();
        var catalog = Path.Combine(dir.Path, "catalog.amdb").Replace(@"\", @"\\");
        var path = dir.Write("settings.json", $$"""{ "CatalogPath": "{{catalog}}", "ChatModelPath": "D:\\Models\\chat.gguf" }""");

        var settings = new JsonAppSettingsStore(path).Current;

        Assert.Equal(Path.Combine(dir.Path, "catalog.amdb"), settings.CatalogPath);
        Assert.Equal(@"D:\Models\chat.gguf", settings.ChatModelPath);
    }

    [Theory]
    [InlineData(@"C:\Data\catalog.amdb", @"C:\Data", "catalog.amdb")]
    [InlineData(@"c:\data\models\a.gguf", @"C:\Data\", @"models\a.gguf")]
    [InlineData(@"C:\Data2\catalog.amdb", @"C:\Data", @"C:\Data2\catalog.amdb")] // a sibling with the same prefix
    [InlineData(@"C:\Data", @"C:\Data", @"C:\Data")] // the root itself is not a file inside it
    [InlineData(@"D:\catalog.amdb", @"C:\Data", @"D:\catalog.amdb")]
    public void Relativize_only_paths_strictly_inside_the_root(string path, string root, string expected) =>
        Assert.Equal(expected, JsonAppSettingsStore.Relativize(path, root));

    [Fact]
    public void A_new_data_folder_gets_its_settings_file()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "data", "settings.json");

        _ = new JsonAppSettingsStore(path);

        Assert.True(File.Exists(path));
    }
}
