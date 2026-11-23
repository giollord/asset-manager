using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Processing;
using AssetManager.Tests.Data;

namespace AssetManager.Tests.Processing;

public class VolumeChangeTests
{
    [Fact]
    public async Task Detaching_a_drive_updates_the_availability_counts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        var volumes = new FakeVolumeService();
        volumes.Mount("EEEE0001", "Assets", Path.Combine(c.Dir.Path, "E"));
        await TestData.SourceAsync(c, 4, s => { s.VolumeId = "EEEE0001"; s.Location = new Location("EEEE0001", "pack"); });
        var stats = new CatalogStatsService(c.Store, new AssetSearchService(c.Store, volumes));
        await new VolumeChangeStartupTask(volumes, c.Store).RunAsync(ct);
        int Count(CatalogStats s, string label) => s.Availability.Single(x => x.Label == label).Count;

        var before = await stats.GetAsync(ct);
        Assert.Equal((4, 0), (Count(before, "Original available"), Count(before, "Unavailable")));

        volumes.Detach("EEEE0001");

        var after = await stats.GetAsync(ct);
        Assert.Equal((0, 4), (Count(after, "Original available"), Count(after, "Unavailable")));
        Assert.Equal(before.Assets, after.Assets); // the catalog itself is unchanged
    }
}
