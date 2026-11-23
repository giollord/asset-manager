using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public class CatalogSchemaTests
{
    private static async Task<List<string>> TablesAsync(TestCatalog c)
    {
        await using var db = c.Db();
        return await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type IN ('table')").ToListAsync();
    }

    [Fact]
    public async Task Fresh_catalog_has_expected_tables_and_pragmas()
    {
        await using var c = await TestCatalog.CreateAsync();
        var tables = await TablesAsync(c);
        foreach (var t in new[] { "Volumes", "Sources", "Assets", "AssetFiles", "Tags", "AssetTags", "Collections", "Previews",
                     "StoredFiles", "StoredFileChunks", "Embeddings", "Jobs", "JobItemErrors", "ImportSessions", "StagedSkips", "Settings" })
            Assert.Contains(t, tables);

        await using var db = c.Db();
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT auto_vacuum AS Value FROM pragma_auto_vacuum").SingleAsync());
        Assert.Equal("wal", await db.Database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync());
    }

    [Fact]
    public async Task Entities_round_trip_with_location_and_json_columns()
    {
        await using var c = await TestCatalog.CreateAsync();
        var loc = new Location("ABCD1234", @"Packs\a.zip", ["inner/b.unitypackage", "Assets/x.png"]);
        await using (var db = c.Db())
        {
            var src = new Source { Kind = SourceKind.Archive, Location = loc.OuterFile, DisplayName = "a.zip", License = { Commercial = TriState.Yes } };
            src.Assets.Add(new Asset
            {
                Name = "x",
                Type = AssetType.Image,
                Image = new ImageMetadata { Width = 32, Height = 32, HasAlpha = true, Format = "PNG" },
                Colors = [new("#ff0000", "red", 1)],
                Files = [new AssetFile { Location = loc, FileName = "x.png", Extension = ".png", Size = 10 }],
            });
            db.Sources.Add(src);
            await db.SaveChangesAsync();
        }

        await using (var db = c.Db())
        {
            var a = await db.Assets.Include(x => x.Files).Include(x => x.Source).SingleAsync();
            Assert.Equal(loc, a.Files[0].Location);
            Assert.Equal(32, a.Image!.Width);
            Assert.Equal("red", a.Colors[0].Name);
            Assert.Equal(TriState.Yes, a.EffectiveLicense.Commercial);
        }
    }

    [Fact]
    public async Task Staged_rows_are_hidden_from_catalog_queries()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using var db = c.Db();
        db.Sources.Add(new Source { DisplayName = "staged", ImportSessionId = 1, Assets = [new Asset { Name = "s", ImportSessionId = 1 }] });
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.Assets.CountAsync());
        Assert.Equal(1, await db.AllAssets.CountAsync());
    }
}
