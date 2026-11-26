using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Data.Search;
using AssetManager.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AssetManager.Tests.Data;

public class CatalogMigrationTests
{
    [Fact]
    public async Task Opening_old_schema_creates_bak_and_upgrades()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "old.amdb");

        // Build an "old" catalog: only the first migration applied.
        await using (var db = new CatalogDbContext(CatalogStore.BuildOptions(path, [])))
        {
            var migrations = db.Database.GetMigrations().ToList();
            Assert.True(migrations.Count >= 2);
            await db.GetService<IMigrator>().MigrateAsync(migrations[0]);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var store = new CatalogStore();
        await store.OpenAsync(path);

        Assert.True(File.Exists(path + ".bak"));
        await using var upgraded = store.CreateDbContext();
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, await upgraded.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE name = 'asset_fts'").SingleAsync());
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Workflow_features_migration_keeps_assets_tags_and_indexes_notes()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Path.Combine(dir.Path, "pre-workflow.amdb");

        // A catalog with data, then rolled back to the schema before AddWorkflowFeatures (an "older catalog").
        var store = new CatalogStore();
        await store.OpenAsync(path);
        long assetId;
        await using (var db = store.CreateDbContext())
        {
            var asset = new Asset
            {
                Name = "Mossy Boulder",
                Source = new Source { DisplayName = "Rocks Pack" },
                Tags = [new AssetTag { Tag = new Tag { Name = "rock" }, Origin = TagOrigin.User }],
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            assetId = asset.Id;
            var migrations = db.Database.GetMigrations().ToList();
            var target = migrations[migrations.FindIndex(m => m.EndsWith("_AddWorkflowFeatures")) - 1];
            await db.GetService<IMigrator>().MigrateAsync(target);
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_table_info('Assets') WHERE name = 'Notes'").SingleAsync());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var reopened = new CatalogStore();
        await reopened.OpenAsync(path);
        await using (var db = reopened.CreateDbContext())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var asset = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).SingleAsync();
            Assert.Equal("Mossy Boulder", asset.Name);
            Assert.Equal("rock", Assert.Single(asset.Tags).Tag.Name);
            Assert.Equal(0, asset.Rating);
            Assert.False(asset.Favorite);

            // The copied FTS rows still match, and new notes are indexed.
            Assert.Contains(assetId, await MatchAsync(db, "boulder"));
            asset.Notes = "use for level two cliffs";
            await db.SaveChangesAsync();
            Assert.Contains(assetId, await MatchAsync(db, "cliffs"));
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static Task<List<long>> MatchAsync(CatalogDbContext db, string text) =>
        db.Database.SqlQueryRaw<long>("SELECT rowid AS Value FROM asset_fts WHERE asset_fts MATCH {0}", SearchIndexer.BuildMatch(text)!).ToListAsync();

    [Fact]
    public async Task Opening_current_catalog_makes_no_bak()
    {
        await using var c = await TestCatalog.CreateAsync();
        await c.Store.OpenAsync(c.Path);
        Assert.False(File.Exists(c.Path + ".bak"));
    }
}
