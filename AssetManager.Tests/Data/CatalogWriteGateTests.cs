using AssetManager.Data;

namespace AssetManager.Tests.Data;

/// <summary>
/// The in-process write turn that parallel savers (import preview generation) take, so SQLite's unfair busy handler
/// cannot starve one of them past the busy timeout ("database is locked").
/// </summary>
public class CatalogWriteGateTests
{
    [Fact]
    public async Task Write_turns_are_taken_one_at_a_time()
    {
        var store = new CatalogStore();
        var ct = TestContext.Current.CancellationToken;
        int inside = 0, most = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 64), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (_, token) =>
        {
            using var turn = await store.AcquireWriteAsync(token);
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            await Task.Yield();
            Interlocked.Decrement(ref inside);
        });
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task Releasing_a_turn_twice_does_not_let_two_writers_in()
    {
        var store = new CatalogStore();
        var ct = TestContext.Current.CancellationToken;
        var first = await store.AcquireWriteAsync(ct);
        first.Dispose();
        first.Dispose();
        using var second = await store.AcquireWriteAsync(ct);
        var third = store.AcquireWriteAsync(ct);
        await Task.Delay(50, ct);
        Assert.False(third.IsCompleted);
        second.Dispose();
        (await third).Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
