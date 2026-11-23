using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public class TagServiceTests
{
    [Fact]
    public async Task Bulk_tag_add_tags_all_and_leaves_existing_unchanged()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 50);
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);

        // 5 already carry it from the AI.
        await tags.AddTagsAsync(ids.Take(5).ToList(), ["medieval"], TagOrigin.AI);
        await tags.AddTagsAsync(ids, ["Medieval"], TagOrigin.User);

        await using var db = c.Db();
        var links = await db.AssetTags.Include(t => t.Tag).Where(t => t.Tag.Name == "medieval").ToListAsync();
        Assert.Equal(50, links.Count);
        Assert.Equal(5, links.Count(l => l.Origin == TagOrigin.AI));
        Assert.Single(await db.Tags.ToListAsync());
    }

    [Fact]
    public async Task Case_insensitive_tag_identity_reuses_existing_tag()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 2);
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync([src.Assets[0].Id], ["sci-fi"], TagOrigin.User);
        await tags.AddTagsAsync([src.Assets[1].Id], ["  Sci-Fi "], TagOrigin.User);

        await using var db = c.Db();
        var tag = Assert.Single(await db.Tags.ToListAsync());
        Assert.Equal("sci-fi", tag.Name);
        Assert.Equal(2, await db.AssetTags.CountAsync());
    }

    [Fact]
    public async Task Remove_tags_deletes_links_and_unused_tags()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 3);
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids, ["forest", "lowpoly"], TagOrigin.Import);
        await tags.RemoveTagsAsync(ids, ["FOREST"]);

        var usage = await tags.GetUsageAsync();
        Assert.Equal("lowpoly", Assert.Single(usage).Name);
        Assert.Equal(3, usage[0].Count);
    }

    [Theory]
    [InlineData(" Sci-Fi  Props ", "sci-fi props")]
    [InlineData("TREE", "tree")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, TagService.Normalize(input));
}
