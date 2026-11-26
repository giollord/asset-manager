using AssetManager.Core.Settings;

namespace AssetManager.Tests.Core;

/// <summary>Design D14: the extraction cache defaults to the system drive, not the (portable) data folder.</summary>
public class CacheLocationTests
{
    [Fact]
    public void Default_cache_is_in_local_app_data_and_a_chosen_folder_wins()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "AssetManager", "cache"), AppPaths.CacheFor(new AppSettings()));
        Assert.Equal(@"D:\Cache", AppPaths.CacheFor(new AppSettings { CacheFolder = @"D:\Cache" }));
    }
}
