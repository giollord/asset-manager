using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Storage;
using AssetManager.Tests.Data;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Processing;

public class LocationResolverTests
{
    [Theory]
    [InlineData(null, @"C:\pack\a.png")]
    [InlineData("1A2B3C4D", @"Packs\a|b%7C.zip", "inner|1.unitypackage", "Assets/100%/x.png")]
    [InlineData("1A2B3C4D", "", "a.zip")]
    public void Location_round_trips(string? volume, string path, params string[] inner)
    {
        var loc = new Location(volume, path, inner);
        var parsed = Location.Parse(loc.ToString());
        Assert.Equal(loc, parsed);
        Assert.Equal(volume, parsed.VolumeId);
        Assert.Equal(path, parsed.Path);
        Assert.Equal(inner, parsed.Inner);
    }

    [Fact]
    public void Location_navigation()
    {
        var zip = new Location("V1", @"Packs\a.zip");
        var tex = zip.Child("b.unitypackage").Child("Assets/Tex/wood.png");
        Assert.Equal("wood.png", tex.FileName);
        Assert.Equal(zip.Child("b.unitypackage"), tex.Container);
        Assert.Equal(zip, tex.OuterFile);
        Assert.Equal(@"F:\Packs\a.zip → b.unitypackage → Assets/Tex/wood.png", tex.Display(@"F:\"));
    }

    [Fact]
    public async Task Resolves_live_path_on_relettered_volume()
    {
        using var kit = new ProcessingKit();
        var e = kit.Dir.Write(@"E\Packs\barrel.png", "PNG-E");
        var f = Path.Combine(kit.Dir.Path, "F");
        kit.Volumes.Mount("CAFE0001", "Backup", Path.Combine(kit.Dir.Path, "E"));
        var loc = new AssetManager.Processing.Locations.VolumeRegistry(kit.Volumes, kit.Catalog).FromPath(e);
        Assert.Equal(new Location("CAFE0001", @"Packs\barrel.png"), loc);

        // The drive comes back under another letter (a different root).
        Directory.Move(Path.Combine(kit.Dir.Path, "E"), f);
        kit.Volumes.Detach("CAFE0001");
        kit.Volumes.Mount("CAFE0001", "Backup", f);

        using var resolved = await kit.Resolver.ResolveAsync(loc);
        Assert.Equal(ResolvedFrom.Live, resolved.From);
        Assert.Equal(Path.Combine(f, @"Packs\barrel.png"), resolved.Path);
        Assert.Equal("PNG-E", await File.ReadAllTextAsync(resolved.Path));
    }

    [Fact]
    public async Task Resolves_entry_inside_nested_containers()
    {
        using var kit = new ProcessingKit();
        var guid = FixtureBuilder.Guid("wood");
        var package = FixtureBuilder.UnityPackage([new UnityPackageEntry(guid, "Assets/Tex/wood.png", Encoding.UTF8.GetBytes("WOOD"))]);
        var zip = kit.Dir.Write(@"V\packs\bundle.zip", FixtureBuilder.Zip(new FixtureFile("inner/pack.unitypackage", package)));
        kit.Volumes.Mount("V1", "Data", Path.Combine(kit.Dir.Path, "V"));

        var loc = new Location("V1", @"packs\bundle.zip", ["inner/pack.unitypackage", "Assets/Tex/wood.png"]);
        using var resolved = await kit.Resolver.ResolveAsync(loc);
        Assert.Equal(ResolvedFrom.Container, resolved.From);
        Assert.Equal("WOOD", await File.ReadAllTextAsync(resolved.Path));
        Assert.Equal("wood.png", Path.GetFileName(resolved.Path));
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task Missing_volume_names_the_drive()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new ProcessingKit(c.Store);
        kit.Dir.Write(@"E\a.png", "x");
        kit.Volumes.Mount("CAFE0001", "Backup", Path.Combine(kit.Dir.Path, "E"));
        var registry = new AssetManager.Processing.Locations.VolumeRegistry(kit.Volumes, c.Store);
        await registry.RememberAsync(["CAFE0001"]);
        kit.Volumes.Detach("CAFE0001");

        var ex = await Assert.ThrowsAsync<OriginalUnavailableException>(() => kit.Resolver.ResolveAsync(new Location("CAFE0001", "a.png")));
        Assert.Contains("\"Backup\"", ex.Missing);
        Assert.Contains(Path.Combine(kit.Dir.Path, "E"), ex.Missing);

        // A mounted volume with a missing file names the file.
        kit.Volumes.Mount("CAFE0001", "Backup", Path.Combine(kit.Dir.Path, "E"));
        ex = await Assert.ThrowsAsync<OriginalUnavailableException>(() => kit.Resolver.ResolveAsync(new Location("CAFE0001", "gone.png")));
        Assert.Contains("gone.png", ex.Missing);
    }

    [Fact]
    public async Task Stored_copy_wins_over_live_and_container()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new ProcessingKit(c.Store);
        var live = kit.Dir.Write(@"V\a.png", "LIVE");
        kit.Volumes.Mount("V1", "Data", Path.Combine(kit.Dir.Path, "V"));
        var src = await TestData.SourceAsync(c, 1, make: _ => new Asset
        {
            Name = "a",
            Files = [new AssetFile { Location = new("V1", "a.png"), FileName = "a.png", Extension = ".png", Size = 4 }],
        });
        var file = src.Assets[0].Files[0];

        using (var r = await kit.Resolver.ResolveAsync(file)) Assert.Equal(ResolvedFrom.Live, r.From);

        await new StoredOriginalsService(c.Store, kit.Resolver).StoreAsync([src.Assets[0].Id]);
        File.WriteAllText(live, "CHANGED");

        file.Stored = null; // as loaded without the navigation: the resolver looks the copy up itself
        using var stored = await kit.Resolver.ResolveAsync(file);
        Assert.Equal(ResolvedFrom.StoredCopy, stored.From);
        Assert.Equal("LIVE", await File.ReadAllTextAsync(stored.Path));
    }
}
