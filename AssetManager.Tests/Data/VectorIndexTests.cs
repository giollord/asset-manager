using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Data.Search;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Data;

public class VectorIndexTests
{
    private const int Dim = SqliteVecIndex.DefaultDimensions;

    private static float[] Vec(int seed, float[]? near = null, float noise = 1f)
    {
        var rnd = new Random(seed);
        var v = new float[Dim];
        for (var i = 0; i < Dim; i++) v[i] = (near?[i] ?? 0) + (float)(rnd.NextDouble() * 2 - 1) * noise;
        return v;
    }

    [Fact]
    public async Task SqliteVec_and_in_memory_return_same_top3()
    {
        using var dir = FixtureBuilder.TempDir();
        var vec = new SqliteVecInterceptor();
        Assert.True(vec.Available, "vec0.dll should be copied to the test output");
        var store = new CatalogStore(null, [vec]);
        await store.OpenAsync(Path.Combine(dir.Path, "c.amdb"));

        // 50 assets; 3 of them close to the query at increasing distances.
        var query = Vec(1);
        var ids = new List<long>();
        await using (var db = store.CreateDbContext())
        {
            var src = new Source { DisplayName = "s" };
            for (var i = 0; i < 50; i++) src.Assets.Add(new Asset { Name = $"a{i}" });
            db.Add(src);
            await db.SaveChangesAsync();
            ids.AddRange(src.Assets.Select(a => a.Id));
        }

        var sqlite = new SqliteVecIndex(store);
        await sqlite.EnsureTableAsync();
        for (var i = 0; i < ids.Count; i++)
        {
            var v = i switch
            {
                7 => Vec(100, query, 0.1f),
                23 => Vec(101, query, 0.3f),
                41 => Vec(102, query, 0.6f),
                _ => Vec(200 + i),
            };
            await sqlite.UpsertAsync(ids[i], v, "test");
        }

        var memory = new InMemoryVectorIndex(store);
        var fromVec = (await sqlite.SearchAsync(query, 3)).Select(h => h.AssetId).ToList();
        var fromMem = (await memory.SearchAsync(query, 3)).Select(h => h.AssetId).ToList();

        Assert.Equal([ids[7], ids[23], ids[41]], fromVec);
        Assert.Equal(fromVec, fromMem);

        // Removal keeps both in sync.
        await sqlite.RemoveAsync([ids[7]]);
        await memory.RebuildAsync();
        Assert.Equal(ids[23], (await sqlite.SearchAsync(query, 1))[0].AssetId);
        Assert.Equal(ids[23], (await memory.SearchAsync(query, 1))[0].AssetId);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void Missing_extension_reports_unavailable() =>
        Assert.False(new SqliteVecInterceptor(@"C:\nope\vec0.dll").Available);
}
