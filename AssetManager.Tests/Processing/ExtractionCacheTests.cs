using AssetManager.Processing.Cache;
using AssetManager.Tests.Fixtures;

namespace AssetManager.Tests.Processing;

public class ExtractionCacheTests
{
    private static Task<CacheLease> AddAsync(ExtractionCache cache, string key, int bytes) =>
        cache.GetOrCreateAsync(key, (dir, _) => File.WriteAllBytesAsync(Path.Combine(dir, "data.bin"), new byte[bytes]));

    [Fact]
    public async Task Eviction_brings_cache_under_limit_without_deleting_locked_entries()
    {
        using var dir = FixtureBuilder.TempDir();
        long limit = long.MaxValue;
        var cache = new ExtractionCache(dir.Path, () => new CacheLimits(limit, TimeSpan.FromDays(14)));

        // Oldest first: a (locked), b, c, d, e — 100 KB each.
        var locked = await AddAsync(cache, "a", 100_000);
        foreach (var k in new[] { "b", "c", "d", "e" }) (await AddAsync(cache, k, 100_000)).Dispose();
        Assert.Equal(500_000, cache.CurrentSize());

        limit = 250_000;
        var result = cache.Evict();

        Assert.True(cache.CurrentSize() <= limit);
        Assert.True(Directory.Exists(cache.PathOf("a")), "locked entry must survive");
        Assert.False(Directory.Exists(cache.PathOf("b")));
        Assert.False(Directory.Exists(cache.PathOf("c")));
        Assert.False(Directory.Exists(cache.PathOf("d")));
        Assert.True(Directory.Exists(cache.PathOf("e")), "most recently used entry is kept");
        Assert.Equal(3, result.Removed);
        Assert.Equal(1, result.SkippedLocked);

        locked.Dispose();
        limit = 0;
        cache.Evict();
        Assert.Equal(0, cache.CurrentSize());
    }

    [Fact]
    public async Task Old_entries_expire_and_hits_refresh_last_use()
    {
        using var dir = FixtureBuilder.TempDir();
        var cache = new ExtractionCache(dir.Path, () => new CacheLimits(long.MaxValue, TimeSpan.FromDays(7)));
        (await AddAsync(cache, "old", 10)).Dispose();
        Assert.Equal(0, cache.Evict(DateTime.UtcNow.AddDays(6)).Removed);
        Assert.Equal(1, cache.Evict(DateTime.UtcNow.AddDays(8)).Removed);
        Assert.Null(cache.TryOpen("old"));
    }

    [Fact]
    public async Task Fill_runs_once_and_failed_fill_leaves_nothing()
    {
        using var dir = FixtureBuilder.TempDir();
        var cache = new ExtractionCache(dir.Path, () => new CacheLimits(long.MaxValue, TimeSpan.FromDays(7)));
        var fills = 0;
        var leases = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.GetOrCreateAsync("k", async (d, _) =>
        {
            Interlocked.Increment(ref fills);
            await Task.Delay(50);
            await File.WriteAllTextAsync(Path.Combine(d, "f.txt"), "x");
        })));
        Assert.Equal(1, fills);
        foreach (var l in leases) l.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync("bad", (_, _) => throw new InvalidOperationException()));
        Assert.Null(cache.TryOpen("bad"));
        Assert.Single(Directory.EnumerateDirectories(dir.Path));
    }
}
