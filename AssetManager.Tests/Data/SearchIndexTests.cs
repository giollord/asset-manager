using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public class SearchIndexTests
{
    private static async Task<List<long>> MatchAsync(CatalogDbContext db, string text) =>
        await db.Database.SqlQueryRaw<long>("SELECT rowid AS Value FROM asset_fts WHERE asset_fts MATCH {0}", SearchIndexer.BuildMatch(text)!).ToListAsync();

    [Fact]
    public async Task Prefix_query_finds_name_and_tag_only_matches()
    {
        await using var c = await TestCatalog.CreateAsync();
        long barrel, crate, other;
        await using (var db = c.Db())
        {
            var tag = new Tag { Name = "barrels" };
            var src = new Source { DisplayName = "Props Pack" };
            var a1 = new Asset { Name = "Wooden Barrel", Source = src };
            var a2 = new Asset { Name = "Crate", Source = src, Tags = [new AssetTag { Tag = tag, Origin = TagOrigin.AI }] };
            var a3 = new Asset { Name = "Rock", Source = src };
            db.AddRange(a1, a2, a3);
            await db.SaveChangesAsync();
            (barrel, crate, other) = (a1.Id, a2.Id, a3.Id);
        }

        await using (var db = c.Db())
        {
            var hits = await MatchAsync(db, "barr");
            Assert.Contains(barrel, hits);
            Assert.Contains(crate, hits);
            Assert.DoesNotContain(other, hits);

            // Source name is indexed too.
            Assert.Equal(3, (await MatchAsync(db, "props")).Count);
        }
    }

    [Fact]
    public async Task Index_follows_tag_removal_rename_and_delete()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using var db = c.Db();
        var a = new Asset { Name = "Thing", Source = new Source { DisplayName = "S" }, Tags = [new AssetTag { Tag = new Tag { Name = "forest" } }] };
        db.Add(a);
        await db.SaveChangesAsync();
        Assert.Single(await MatchAsync(db, "fore"));

        a.Tags.Clear();
        a.Name = "TreeStump";
        await db.SaveChangesAsync();
        Assert.Empty(await MatchAsync(db, "fore"));
        Assert.Single(await MatchAsync(db, "stump"));

        db.Remove(a);
        await db.SaveChangesAsync();
        Assert.Empty(await MatchAsync(db, "tree"));
    }

    [Theory]
    [InlineData("WoodenBarrel_01", "WoodenBarrel_01 Wooden Barrel 01")]
    [InlineData("rock", "rock")]
    [InlineData("SM_PropHUDPanel", "SM_PropHUDPanel SM Prop HUD Panel")]
    public void WithWords_splits_identifiers(string input, string expected) => Assert.Equal(expected, SearchIndexer.WithWords(input));

    [Fact]
    public void BuildMatch_quotes_and_prefixes_words() =>
        Assert.Equal("\"wood\"* AND \"barr\"*", SearchIndexer.BuildMatch("wood, barr"));
}
