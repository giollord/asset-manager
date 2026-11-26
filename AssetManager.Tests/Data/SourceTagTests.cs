using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

/// <summary>Source tags (spec "Source tags", "Removing source tags", "Per-asset opt-out of a source tag"; design D1).</summary>
public class SourceTagTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<(long AssetId, TagOrigin Origin)>> CarriersAsync(TestCatalog c, string tag)
    {
        await using var db = c.Db();
        return (await db.AssetTags.Where(t => t.Tag.Name == tag).Select(t => new { t.AssetId, t.Origin }).ToListAsync(Ct))
            .Select(t => (t.AssetId, t.Origin)).ToList();
    }

    [Fact]
    public async Task Tag_the_whole_pack()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 240);
        var other = await TestData.SourceAsync(c, 3);

        await new SourceTagService(c.Store).AddAsync(src.Id, ["Pixel Art"], ct: Ct);

        var carriers = await CarriersAsync(c, "pixel art");
        Assert.Equal(240, carriers.Count);
        Assert.All(carriers, t => Assert.Equal(TagOrigin.Source, t.Origin));
        Assert.DoesNotContain(carriers, t => other.Assets.Any(a => a.Id == t.AssetId));
        // Works like any tag in collections, search and full-text search.
        var collection = await new CollectionService(c.Store).SaveAsync(new Collection { Name = "Pixel", IncludeTags = ["pixel art"] }, Ct);
        Assert.Equal(240, await new CollectionService(c.Store).CountAsync(collection, Ct));
        var search = new AssetSearchService(c.Store);
        Assert.Equal(240, await search.CountAsync(new SearchQuery { IncludeTags = ["pixel art"] }, Ct));
        Assert.Equal(240, await search.CountAsync(new SearchQuery { Text = "pixel" }, Ct));
        Assert.Equal([new SourceTagView("pixel art", TagOrigin.User)], await new SourceTagService(c.Store).OfAsync(src.Id, Ct));
    }

    [Fact]
    public async Task Remove_source_tag_keeps_user_copy()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 5);
        var keeper = src.Assets[2].Id;
        await TestData.TagAsync(c, keeper, "medieval");
        var sourceTags = new SourceTagService(c.Store);
        await sourceTags.AddAsync(src.Id, ["medieval"], ct: Ct);
        Assert.Equal(5, (await CarriersAsync(c, "medieval")).Count);

        await sourceTags.RemoveAsync(src.Id, ["medieval"], Ct);

        Assert.Equal([(keeper, TagOrigin.User)], await CarriersAsync(c, "medieval"));
        Assert.Empty(await sourceTags.OfAsync(src.Id, Ct));
    }

    [Fact]
    public async Task New_assets_inherit()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 4);
        await new SourceTagService(c.Store).AddAsync(src.Id, ["low poly"], ct: Ct);
        await using (var db = c.Db())
        {
            for (var i = 0; i < 10; i++) db.Assets.Add(new Asset { Name = $"new{i}", SourceId = src.Id });
            await db.SaveChangesAsync(Ct);
            await SourceTagService.ApplyAsync(db, src.Id, Ct);
        }
        Assert.Equal(14, (await CarriersAsync(c, "low poly")).Count);
    }

    [Fact]
    public async Task Removing_from_one_asset_opts_it_out()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 3);
        var splash = src.Assets[0].Id;
        await new SourceTagService(c.Store).AddAsync(src.Id, ["pixel art"], ct: Ct);
        var tags = new TagService(c.Store);

        await tags.RemoveTagsAsync([splash], ["pixel art"], Ct);
        await new SourceTagService(c.Store).ApplyAsync([src.Id], Ct);
        Assert.DoesNotContain(await CarriersAsync(c, "pixel art"), t => t.AssetId == splash);
        Assert.Equal(2, (await CarriersAsync(c, "pixel art")).Count);

        // Adding it back by hand ends the opt-out.
        await tags.AddTagsAsync([splash], ["pixel art"], TagOrigin.User, Ct);
        await using var db = c.Db();
        Assert.Equal(0, await db.AssetTagOptOuts.CountAsync(Ct));
    }

    [Fact]
    public async Task Opt_out_survives_reimport_with_ai()
    {
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient());
        var dir = kit.Catalog.Dir;
        var first = dir.Write("Splash/splash.png", ImportStagingTests.Png(MagickColors.SkyBlue, 64));
        dir.Write("Splash/hero.png", ImportStagingTests.Png(MagickColors.Red, 16));
        dir.Write("Splash/coin.png", ImportStagingTests.Png(MagickColors.Gold, 16));
        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(first)!], Depth: AiDepth.None));
        long sourceId, splash;
        List<long> all;
        await using (var db = kit.Catalog.Db())
        {
            sourceId = await db.Sources.Select(s => s.Id).SingleAsync(Ct);
            all = await db.Assets.Select(a => a.Id).ToListAsync(Ct);
            splash = await db.Assets.Where(a => a.Name == "splash").Select(a => a.Id).SingleAsync(Ct);
        }
        await new SourceTagService(kit.Store).AddAsync(sourceId, ["pixel art"], TagOrigin.AI, Ct);
        await new TagService(kit.Store).RemoveTagsAsync([splash], ["pixel art"], Ct);

        var reimport = await kit.Jobs.WaitAsync(await kit.Reimports.StartAsync(all, reenrich: true, Ct), Ct);
        Assert.True(reimport.State == JobState.Completed, reimport.Error);
        await using (var db = kit.Catalog.Db())
        {
            var enrich = await kit.Jobs.WaitAsync(await db.Jobs.Where(j => j.Type == "ai.enrich").Select(j => j.Id).SingleAsync(Ct), Ct);
            Assert.True(enrich.State == JobState.Completed, enrich.Error);
        }

        var carriers = await CarriersAsync(kit.Catalog, "pixel art");
        Assert.DoesNotContain(carriers, t => t.AssetId == splash);
        Assert.Equal(2, carriers.Count);
    }
}
