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

    /// <summary>A portable copy on a slow drive keeps ffmpeg's work files on the system drive, like the cache.</summary>
    [Fact]
    public void Portable_temp_is_in_local_app_data_and_an_override_keeps_its_own()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "AssetManager", "temp"), AppPaths.TempFor(new AppDataLocation(@"E:\AssetManager\data", DataMode.Portable)));
        Assert.Equal(@"D:\Data\temp", AppPaths.TempFor(new AppDataLocation(@"D:\Data", DataMode.Override)));
    }
}
