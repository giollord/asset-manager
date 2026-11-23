using AssetManager.Data;
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
    public async Task Opening_current_catalog_makes_no_bak()
    {
        await using var c = await TestCatalog.CreateAsync();
        await c.Store.OpenAsync(c.Path);
        Assert.False(File.Exists(c.Path + ".bak"));
    }
}
