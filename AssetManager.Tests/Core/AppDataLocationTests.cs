using AssetManager.Core.Settings;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Core;

/// <summary>Spec "app-data-location": which data folder the app uses.</summary>
public class AppDataLocationTests
{
    private static Func<string, string?> Env(string? overrideDir = null) =>
        name => name == AppDataLocation.OverrideVariable ? overrideDir : null;

    [Fact]
    public void Development_build_uses_the_user_profile()
    {
        using var dir = new TempDir();
        var dev = Path.Combine(dir.Path, "profile");

        var location = AppDataLocation.Resolve(Path.Combine(dir.Path, "app"), Env(), dev);

        Assert.Equal(new AppDataLocation(dev, DataMode.Development), location);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "app", "data")));
    }

    [Fact]
    public void Portable_copy_uses_the_data_folder_next_to_the_exe()
    {
        using var dir = new TempDir();
        dir.Write("app/portable", "");

        var location = AppDataLocation.Resolve(Path.Combine(dir.Path, "app"), Env(), Path.Combine(dir.Path, "profile"));

        Assert.Equal(new AppDataLocation(Path.Combine(dir.Path, "app", "data"), DataMode.Portable), location);
        Assert.True(Directory.Exists(location.Root));
        Assert.Empty(Directory.GetFiles(location.Root)); // the write probe is removed
    }

    [Fact]
    public void Portable_copy_in_a_folder_that_cannot_be_written_falls_back_with_a_reason()
    {
        using var dir = new TempDir();
        dir.Write("app/portable", "");
        dir.Write("app/data", "a file where the data folder should be"); // the folder cannot be created
        var dev = Path.Combine(dir.Path, "profile");

        var location = AppDataLocation.Resolve(Path.Combine(dir.Path, "app"), Env(), dev);

        Assert.Equal((dev, DataMode.Fallback), (location.Root, location.Mode));
        Assert.Contains("cannot be written", location.Reason);
        Assert.Contains(Path.Combine(dir.Path, "app", "data"), location.Reason);
    }

    [Fact]
    public void Override_wins_over_portable_and_is_created()
    {
        using var dir = new TempDir();
        dir.Write("app/portable", "");
        var custom = Path.Combine(dir.Path, "AMData");

        var location = AppDataLocation.Resolve(Path.Combine(dir.Path, "app"), Env($"\"{custom}\""), Path.Combine(dir.Path, "profile"));

        Assert.Equal(new AppDataLocation(custom, DataMode.Override), location);
        Assert.True(Directory.Exists(custom));
    }
}
