using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Data.Services;

namespace AssetManager.Tests.Data;

/// <summary>Seeding helpers for catalog tests.</summary>
public static class TestData
{
    public static async Task<Source> SourceAsync(TestCatalog c, int assets, Action<Source>? configure = null, Func<int, Asset>? make = null)
    {
        await using var db = c.Db();
        var src = new Source { DisplayName = "Pack", Kind = SourceKind.Folder, Location = new(null, @"C:\pack"), ImportedAt = DateTime.UtcNow };
        configure?.Invoke(src);
        for (var i = 0; i < assets; i++)
        {
            var a = make?.Invoke(i) ?? new Asset { Name = $"asset{i}", Type = AssetType.Image };
            src.Assets.Add(a);
        }
        db.Sources.Add(src);
        await db.SaveChangesAsync();
        return src;
    }

    public static async Task TagAsync(TestCatalog c, long assetId, params string[] tags)
    {
        await new TagService(c.Store).AddTagsAsync([assetId], tags, TagOrigin.User);
    }
}

/// <summary>Large catalogs for performance tests: rows cloned in SQL (bypassing EF and the FTS interceptor).</summary>
public static class BulkData
{
    /// <summary>
    /// Adds <paramref name="count"/> assets spread over <paramref name="sources"/> sources, cycling through the asset
    /// types; every 10th asset gets the tag "tree" and every 7th the tag "winter".
    /// </summary>
    public static async Task SeedAsync(TestCatalog c, int count, int sources = 100)
    {
        var src = await TestData.SourceAsync(c, 1, make: _ => new Asset { Name = "template", Type = AssetType.Image });
        await using var db = c.Db();
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task Exec(string sql, bool inTransaction = true)
        {
            await using var cmd = conn.CreateCommand();
            if (inTransaction) cmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        var columns = new List<string>();
        await using (var info = conn.CreateCommand())
        {
            info.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            info.CommandText = "PRAGMA table_info(Assets)";
            await using var r = await info.ExecuteReaderAsync();
            while (await r.ReadAsync()) columns.Add(r.GetString(1));
        }
        var typeCount = Enum.GetValues<AssetType>().Length;
        await Exec($"INSERT INTO Sources (Kind, Location, DisplayName, ImportedAt, UserEdited, Links, ReplaceScore, License_Commercial, License_Attribution) " +
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {sources}) " +
            $"SELECT Kind, Location, 'Bulk ' || i, ImportedAt, UserEdited, Links, 0, License_Commercial, License_Attribution FROM Sources, n WHERE Id = {src.Id}");
        var copy = columns.Where(col => col is not ("Id" or "Name" or "Type" or "SourceId")).ToList();
        await Exec($"INSERT INTO Assets (Name, Type, SourceId, {string.Join(", ", copy)}) " +
            $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {count}) " +
            $"SELECT 'bulk ' || i, CASE i % {typeCount} {string.Join(" ", Enum.GetValues<AssetType>().Select((t, k) => $"WHEN {k} THEN '{t}'"))} END, " +
            $"(SELECT MIN(Id) FROM Sources WHERE DisplayName LIKE 'Bulk %') + i % {sources}, {string.Join(", ", copy)} FROM Assets, n WHERE Id = {src.Assets[0].Id}");
        await Exec("INSERT OR IGNORE INTO Tags (Name) VALUES ('tree'), ('winter')");
        await Exec("INSERT INTO AssetTags (AssetId, TagId, Origin) SELECT a.Id, t.Id, 'User' FROM Assets a, Tags t WHERE t.Name = 'tree' AND a.Name LIKE 'bulk %' AND a.Id % 10 = 0");
        await Exec("INSERT INTO AssetTags (AssetId, TagId, Origin) SELECT a.Id, t.Id, 'User' FROM Assets a, Tags t WHERE t.Name = 'winter' AND a.Name LIKE 'bulk %' AND a.Id % 7 = 0");
        await tx.CommitAsync();
        await Exec("ANALYZE", inTransaction: false);
    }
}
