using AssetManager.Core.Domain;
using AssetManager.Data;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task A_write_waits_for_an_open_transaction_on_another_context()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var catalog = await TestCatalog.CreateAsync();
        await using var holder = catalog.Db();
        await using var tx = await holder.Database.BeginTransactionAsync(ct);
        holder.Tags.Add(new Tag { Name = "held" });
        await holder.SaveChangesAsync(ct);

        var other = Task.Run(async () =>
        {
            await using var db = catalog.Db();
            return await db.Tags.Where(t => t.Name == "held").ExecuteUpdateAsync(u => u.SetProperty(t => t.Name, "renamed"), ct);
        }, ct);
        await Task.Delay(300, ct);
        Assert.False(other.IsCompleted); // queued behind the transaction's turn, not failing or racing it

        await tx.CommitAsync(ct);
        Assert.Equal(1, await other); // it ran after the commit, so it saw the row
    }

    [Fact]
    public async Task A_transaction_disposed_without_commit_releases_the_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var catalog = await TestCatalog.CreateAsync();
        await using (var db = catalog.Db())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Tags.Add(new Tag { Name = "dropped" });
            await db.SaveChangesAsync(ct);
        } // no commit: rolled back when disposed

        await using var next = catalog.Db();
        next.Tags.Add(new Tag { Name = "kept" });
        await next.SaveChangesAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(["kept"], await next.Tags.Select(t => t.Name).ToListAsync(ct));
    }

    /// <summary>
    /// The reported failure: an import's parallel preview saves kept the database busy while AI enrichment of an
    /// older import saved its results, and the enrichment's writes failed with "database is locked".
    /// </summary>
    [Fact]
    public async Task A_lone_writer_is_not_starved_by_parallel_transactions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var catalog = await TestCatalog.CreateAsync();
        await using (var db = catalog.Db())
        {
            db.Tags.Add(new Tag { Name = "enriched-0" });
            await db.SaveChangesAsync(ct);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var importers = Enumerable.Range(0, 12).Select(w => Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                await using var db = catalog.Db();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                db.Tags.Add(new Tag { Name = $"preview-{w}-{i}" });
                await db.SaveChangesAsync(ct);
                await Task.Delay(5, ct); // a preview save holds its transaction for a moment
                await tx.CommitAsync(ct);
            }
        }, ct)).ToList();

        var slowest = TimeSpan.Zero;
        for (var i = 1; i <= 40; i++)
        {
            var started = DateTime.UtcNow;
            await using var db = catalog.Db();
            Assert.Equal(1, await db.Tags.Where(t => t.Name == $"enriched-{i - 1}").ExecuteUpdateAsync(u => u.SetProperty(t => t.Name, $"enriched-{i}"), ct));
            if (DateTime.UtcNow - started > slowest) slowest = DateTime.UtcNow - started;
        }
        await stop.CancelAsync();
        await Task.WhenAll(importers);

        // A fair queue: each write waits for at most one turn of each importer, far below the busy timeout.
        Assert.True(slowest < TimeSpan.FromSeconds(5), $"slowest write took {slowest}");
    }

    [Theory]
    [InlineData("SELECT * FROM Assets", false)]
    [InlineData("  select 1", false)]
    [InlineData("-- tag\nSELECT 1", false)]
    [InlineData("UPDATE Assets SET Name = 'x'", true)]
    [InlineData("INSERT INTO Tags (Name) VALUES ('a') RETURNING Id", true)]
    [InlineData("DELETE FROM asset_fts", true)]
    [InlineData("-- tag\nDELETE FROM Tags", true)]
    public void Writes_are_told_from_queries(string sql, bool write) => Assert.Equal(write, CatalogWriteGate.IsWrite(sql));

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
