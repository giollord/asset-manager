using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Core.Search;
using AssetManager.Data.Search;

namespace AssetManager.Tests.Data;

public class AssetSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> NamesAsync(AssetSearchService search, SearchQuery q) =>
        (await search.SearchAsync(q, 0, 100, Ct)).Items.Select(i => i.Name).ToList();

    [Fact]
    public async Task Include_and_exclude_tags()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 4, make: i => new Asset
        {
            Name = new[] { "Oak", "Snowy Pine", "Bush", "Tree Sprite" }[i],
            Type = i == 3 ? AssetType.Image : AssetType.Model3D,
        });
        var ids = src.Assets.ToDictionary(a => a.Name, a => a.Id);
        await TestData.TagAsync(c, ids["Oak"], "tree");
        await TestData.TagAsync(c, ids["Snowy Pine"], "tree", "winter");
        await TestData.TagAsync(c, ids["Bush"], "plant");
        await TestData.TagAsync(c, ids["Tree Sprite"], "tree");
        var search = new AssetSearchService(c.Store);

        var names = await NamesAsync(search, new SearchQuery { Types = [AssetType.Model3D], IncludeTags = ["tree"], ExcludeTags = ["winter"] });

        Assert.Equal(["Oak"], names);
        Assert.Equal(1, await search.CountAsync(new SearchQuery { Types = [AssetType.Model3D], IncludeTags = ["Tree"], ExcludeTags = ["WINTER"] }, Ct));
    }

    [Fact]
    public async Task Commercial_safe_filter_uses_the_effective_license()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 2, s => { s.DisplayName = "CC0 pack"; s.License = new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes }; },
            i => i == 0
                ? new Asset { Name = "free" }
                : new Asset { Name = "overridden", HasLicenseOverride = true, LicenseOverride = new LicenseInfo { Kind = "NC", Commercial = TriState.No } });
        await TestData.SourceAsync(c, 1, s => { s.DisplayName = "NC pack"; s.License = new LicenseInfo { Commercial = TriState.No }; },
            _ => new Asset { Name = "nc" });
        await TestData.SourceAsync(c, 1, s => s.DisplayName = "Unknown", _ => new Asset { Name = "unknown" });
        var search = new AssetSearchService(c.Store);

        Assert.Equal(["free"], await NamesAsync(search, new SearchQuery { Commercial = TriState.Yes }));
        Assert.Equal(["nc", "overridden"], (await NamesAsync(search, new SearchQuery { Commercial = TriState.No })).Order());
        Assert.Equal(["unknown"], await NamesAsync(search, new SearchQuery { Commercial = TriState.Unknown }));
    }

    [Fact]
    public async Task Prefix_match_ranks_names_above_descriptions()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 3, s => s.DisplayName = "Props", i => i switch
        {
            0 => new Asset { Name = "Crate", Description = "A crate next to a barrel." },
            1 => new Asset { Name = "WoodenBarrel_01", Description = "Old." },
            _ => new Asset { Name = "Rock", Description = "Grey." },
        });
        var search = new AssetSearchService(c.Store);

        var page = await search.SearchAsync(new SearchQuery { Text = "barr" }, 0, 10, Ct);

        Assert.Equal(["WoodenBarrel_01", "Crate"], page.Items.Select(i => i.Name));   // name match first
        Assert.Equal(2, page.Total);
        Assert.Equal(["WoodenBarrel_01"], await NamesAsync(search, new SearchQuery { Text = "wood barr" })); // all words must match
        Assert.Empty(await NamesAsync(search, new SearchQuery { Text = "barr", Types = [AssetType.Audio] }));    // and filters still apply
    }

    [Fact]
    public async Task Collection_source_tree_and_model_issue_filters()
    {
        await using var c = await TestCatalog.CreateAsync();
        var folder = await TestData.SourceAsync(c, 1, s => s.DisplayName = "Folder", _ => new Asset { Name = "loose", Type = AssetType.Model3D, TexturesMissing = true });
        await TestData.SourceAsync(c, 2, s => { s.DisplayName = "Inner zip"; s.ParentSourceId = folder.Id; }, i => new Asset
        {
            Name = i == 0 ? "tiny" : "tilted", Type = AssetType.Model3D,
            ScaleAnomaly = i == 0 ? ScaleAnomaly.TooSmall : ScaleAnomaly.None, OrientationUncertain = i == 1,
        });
        await TestData.SourceAsync(c, 1, s => s.DisplayName = "Other", _ => new Asset { Name = "elsewhere", Type = AssetType.Model3D, OrientationVerified = true });
        await using (var db = c.Db())
        {
            db.Collections.Add(new Collection { Name = "Props", IncludeTags = ["prop"] });
            await db.SaveChangesAsync(Ct);
        }
        var tilted = (await NamesAndIdsAsync(c))["tilted"];
        await TestData.TagAsync(c, tilted, "prop");
        var search = new AssetSearchService(c.Store);
        long collectionId;
        await using (var db = c.Db()) collectionId = db.Collections.Single().Id;

        Assert.Equal(["loose", "tilted", "tiny"], (await NamesAsync(search, new SearchQuery { SourceId = folder.Id })).Order());
        Assert.Equal(["tilted"], await NamesAsync(search, new SearchQuery { CollectionId = collectionId }));
        Assert.Equal(["tiny"], await NamesAsync(search, new SearchQuery { ScaleAnomaly = true }));
        Assert.Equal(["tilted"], await NamesAsync(search, new SearchQuery { OrientationUncertain = true }));
        Assert.Equal(["loose"], await NamesAsync(search, new SearchQuery { TexturesMissing = true }));
        Assert.DoesNotContain("elsewhere", await NamesAsync(search, new SearchQuery { OrientationNotVerified = true }));
    }

    [Fact]
    public async Task Availability_follows_mounted_volumes_and_stored_copies()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 2, s => { s.DisplayName = "On E"; s.VolumeId = "E-VOL"; }, i => new Asset { Name = i == 0 ? "e-file" : "e-stored", IsStored = i == 1 });
        await TestData.SourceAsync(c, 1, s => { s.DisplayName = "On F"; s.VolumeId = "F-VOL"; }, _ => new Asset { Name = "f-file" });
        var search = new AssetSearchService(c.Store, new Volumes("E-VOL"));

        Assert.Equal(["e-file", "e-stored"], (await NamesAsync(search, new SearchQuery { Availability = AvailabilityFilter.Available })).Order());
        Assert.Equal(["f-file"], await NamesAsync(search, new SearchQuery { Availability = AvailabilityFilter.Unavailable }));
        Assert.Equal(["e-stored"], await NamesAsync(search, new SearchQuery { Availability = AvailabilityFilter.Stored }));
    }

    [Fact]
    public void Query_round_trips_through_a_url_token()
    {
        var q = new SearchQuery { Types = [AssetType.Audio], Text = "rain drops", IncludeTags = ["sfx"], Commercial = TriState.Yes, TexturesMissing = true, Sort = SearchSort.Name };
        var back = SearchQuery.FromToken(q.ToToken());
        Assert.Equal(q, back);
        Assert.DoesNotContain('+', q.ToToken());
        Assert.True(SearchQuery.FromToken("not a token!").IsEmpty);
        Assert.True(new SearchQuery().IsEmpty);
        Assert.False(q.IsEmpty);
    }

    private static async Task<Dictionary<string, long>> NamesAndIdsAsync(TestCatalog c)
    {
        await using var db = c.Db();
        return db.Assets.ToDictionary(a => a.Name, a => a.Id);
    }

    private sealed class Volumes(params string[] mounted) : IVolumeService
    {
        public IReadOnlyList<VolumeInfo> GetVolumes() => mounted.Select(id => new VolumeInfo(id, id, id + ":\\")).ToList();
        public VolumeInfo? GetVolumeForPath(string path) => null;
        public string? GetRoot(string volumeId) => mounted.Contains(volumeId) ? volumeId + ":\\" : null;
        public event Action? VolumesChanged { add { } remove { } }
    }
}
