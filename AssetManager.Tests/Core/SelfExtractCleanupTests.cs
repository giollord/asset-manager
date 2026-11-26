using AssetManager.Core.Platform;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Core;

/// <summary>Design D1: unpacked copies of older single-file versions are removed.</summary>
public class SelfExtractCleanupTests
{
    [Fact]
    public void Deletes_other_versions_but_keeps_the_running_one()
    {
        using var dir = FixtureBuilder.TempDir();
        var app = Path.Combine(dir.Path, ".net", "AssetManager");
        var current = Directory.CreateDirectory(Path.Combine(app, "newHash")).FullName;
        File.WriteAllText(Path.Combine(current, "AssetManager.dll"), "x");
        foreach (var old in new[] { "oldHash1", "oldHash2" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(app, old, "sub")).FullName, "f.dll"), "x");

        Assert.Equal(2, SelfExtractCleanupStartupTask.DeleteOtherVersions(current + Path.DirectorySeparatorChar));

        Assert.Equal(["newHash"], Directory.GetDirectories(app).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(current, "AssetManager.dll")));
    }

    [Fact]
    public void Folder_in_use_is_skipped()
    {
        using var dir = FixtureBuilder.TempDir();
        var app = Path.Combine(dir.Path, ".net", "AssetManager");
        var current = Directory.CreateDirectory(Path.Combine(app, "newHash")).FullName;
        var busy = Path.Combine(Directory.CreateDirectory(Path.Combine(app, "busyHash")).FullName, "f.dll");
        File.WriteAllText(busy, "x");

        using (File.Open(busy, FileMode.Open, FileAccess.Read, FileShare.None)) // another running copy
            Assert.Equal(0, SelfExtractCleanupStartupTask.DeleteOtherVersions(current));

        Assert.True(File.Exists(busy));
    }

    [Fact]
    public void Normal_build_folder_is_left_alone()
    {
        using var dir = FixtureBuilder.TempDir();
        var bin = Directory.CreateDirectory(Path.Combine(dir.Path, "bin", "Release")).FullName;
        Directory.CreateDirectory(Path.Combine(dir.Path, "bin", "Debug"));

        Assert.Equal(0, SelfExtractCleanupStartupTask.DeleteOtherVersions(bin));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "bin", "Debug")));
    }
}
