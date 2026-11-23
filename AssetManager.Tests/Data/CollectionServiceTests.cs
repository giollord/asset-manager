using AssetManager.Core.Domain;
using AssetManager.Data.Services;

namespace AssetManager.Tests.Data;

public class CollectionServiceTests
{
    [Fact]
    public async Task Asset_in_multiple_collections()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 3);
        var (both, forestOnly, none) = (src.Assets[0].Id, src.Assets[1].Id, src.Assets[2].Id);
        await TestData.TagAsync(c, both, "forest", "lowpoly");
        await TestData.TagAsync(c, forestOnly, "forest");

        var svc = new CollectionService(c.Store);
        var a = await svc.SaveAsync(new Collection { Name = "A", IncludeTags = ["Forest"] });
        var b = await svc.SaveAsync(new Collection { Name = "B", IncludeTags = ["lowpoly"] });

        Assert.Equal([both, forestOnly], (await svc.MemberIdsAsync(a)).Order());
        Assert.Equal([both], await svc.MemberIdsAsync(b));
        Assert.Equal(["A", "B"], (await svc.ForAssetAsync(both)).Select(x => x.Name));
        Assert.Empty(await svc.ForAssetAsync(none));
    }

    [Fact]
    public async Task Membership_follows_tags()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 1);
        var id = src.Assets[0].Id;
        await TestData.TagAsync(c, id, "forest");
        var svc = new CollectionService(c.Store);
        var col = await svc.SaveAsync(new Collection { Name = "Forest", IncludeTags = ["forest"], ExcludeTags = ["winter"] });
        Assert.Equal(1, await svc.CountAsync(col));

        await new TagService(c.Store).RemoveTagsAsync([id], ["forest"]);
        Assert.Equal(0, await svc.CountAsync(col));

        await TestData.TagAsync(c, id, "forest", "winter");
        Assert.Equal(0, await svc.CountAsync(col));
    }

    [Fact]
    public async Task Collection_requires_a_tag() =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var c = await TestCatalog.CreateAsync();
            await new CollectionService(c.Store).SaveAsync(new Collection { Name = "x" });
        });
}
