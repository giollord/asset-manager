using System.Collections.Concurrent;
using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Processes payload items 1..N, failing those listed in <c>Fail</c>; records what it processed.</summary>
public sealed class CountingHandler(string type = "test.items", JobPool pool = JobPool.Cpu) : IJobHandler
{
    public sealed record Payload(int Count, int[] Fail, int DelayMs = 0);

    public ConcurrentQueue<(long Job, string Item)> Processed { get; } = new();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Type => type;

    public JobPool Pool => pool;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        Started.TrySetResult();
        var p = context.Payload<Payload>();
        var items = Enumerable.Range(1, p.Count).Select(i => i.ToString()).Where(context.Includes).ToList();
        context.Progress.SetStage("Processing", 0, 1, items.Count);
        foreach (var item in items)
        {
            if (p.DelayMs > 0) await Task.Delay(p.DelayMs, ct);
            ct.ThrowIfCancellationRequested();
            Processed.Enqueue((context.Job.Id, item));
            if (p.Fail.Contains(int.Parse(item)) && context.OnlyItems is null) context.Progress.Fail(item, $"Item {item}", "broken");
            context.Progress.Advance(1, $"Item {item}");
        }
        context.Progress.SetSummary(new { processed = items.Count });
    }
}

public class JobQueueTests
{
    private static JobQueue Queue(TestCatalog c, params IJobHandler[] handlers) => new(c.Store, handlers, new ProgressHub());

    [Fact]
    public async Task Resume_after_restart()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using (var db = c.Db())
        {
            db.Jobs.AddRange(
                new Job { Type = "test.items", Title = "a", PayloadJson = """{"count":3,"fail":[]}""", State = JobState.Pending, CreatedAt = DateTime.UtcNow },
                new Job { Type = "test.items", Title = "b", PayloadJson = """{"count":2,"fail":[]}""", State = JobState.Running, CreatedAt = DateTime.UtcNow, Attempts = 1 },
                new Job { Type = "test.items", Title = "done", PayloadJson = """{"count":9,"fail":[]}""", State = JobState.Completed, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var handler = new CountingHandler();
        await using var queue = Queue(c, handler);

        await queue.StartAsync(TestContext.Current.CancellationToken);
        var a = await queue.WaitAsync(1, TestContext.Current.CancellationToken);
        var b = await queue.WaitAsync(2, TestContext.Current.CancellationToken);

        Assert.Equal((JobState.Completed, 3L, 3L), (a.State, a.Done, a.Total));
        Assert.Equal(JobState.Completed, b.State);
        Assert.Equal(5, handler.Processed.Count); // the finished job was not run again
        await using var read = c.Db();
        Assert.Equal(2, (await read.Jobs.SingleAsync(j => j.Id == 2, TestContext.Current.CancellationToken)).Attempts);
    }

    [Fact]
    public async Task Interrupted_jobs_wait_for_the_window_but_new_jobs_run_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        await using (var db = c.Db())
        {
            db.Jobs.Add(new Job { Type = "test.items", Title = "interrupted", PayloadJson = """{"count":2,"fail":[]}""", State = JobState.Running, CreatedAt = DateTime.UtcNow, Attempts = 1 });
            await db.SaveChangesAsync(ct);
        }
        var signals = new StartupSignals();
        var handler = new CountingHandler();
        var hub = new ProgressHub();
        await using var queue = new JobQueue(c.Store, [handler], hub, signals: signals) { ResumeSettle = TimeSpan.Zero, ResumeFallback = TimeSpan.FromMinutes(5) };

        await queue.StartAsync(ct);
        Assert.Equal(JobState.Pending, hub.Get(1)?.State); // listed as waiting at once

        // A job the user starts now runs before the window is ready.
        var fresh = await queue.EnqueueAsync("test.items", "new", new CountingHandler.Payload(1, []), ct: ct);
        Assert.Equal(JobState.Completed, (await queue.WaitAsync(fresh, ct)).State);
        Assert.False(queue.Resumed.IsCompleted);
        Assert.DoesNotContain(handler.Processed, p => p.Job == 1);

        signals.SignalWindowReady();
        await Task.Delay(200, ct);
        Assert.False(queue.Resumed.IsCompleted); // the AI models are still loading

        signals.SignalAiSettled();
        Assert.Equal(JobState.Completed, (await queue.WaitAsync(1, ct)).State);
        Assert.True(queue.Resumed.IsCompleted);
        Assert.Equal(2, handler.Processed.Count(p => p.Job == 1));
    }

    [Fact]
    public async Task Interrupted_jobs_resume_after_the_fallback_without_a_window()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        await using (var db = c.Db())
        {
            db.Jobs.Add(new Job { Type = "test.items", Title = "pending", PayloadJson = """{"count":1,"fail":[]}""", State = JobState.Pending, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        await using var queue = new JobQueue(c.Store, [new CountingHandler()], new ProgressHub(), signals: new StartupSignals())
            { ResumeSettle = TimeSpan.Zero, ResumeFallback = TimeSpan.FromMilliseconds(50), ResumeAiWait = TimeSpan.FromMilliseconds(50) };

        await queue.StartAsync(ct);

        Assert.Equal(JobState.Completed, (await queue.WaitAsync(1, ct)).State);
    }

    [Fact]
    public async Task Retry_failed_items()
    {
        await using var c = await TestCatalog.CreateAsync();
        var handler = new CountingHandler();
        await using var queue = Queue(c, handler);
        await queue.StartAsync(TestContext.Current.CancellationToken);

        var first = await queue.EnqueueAsync("test.items", "Previews", new CountingHandler.Payload(20, [2, 3, 5, 7, 11, 13, 17]), ct: TestContext.Current.CancellationToken);
        var done = await queue.WaitAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal((JobState.Completed, 7), (done.State, done.Errors));
        Assert.Equal(7, (await queue.ItemErrorsAsync(first, TestContext.Current.CancellationToken)).Count);

        var retry = await queue.RetryFailedAsync(first, TestContext.Current.CancellationToken);
        var retried = await queue.WaitAsync(retry!.Value, TestContext.Current.CancellationToken);

        Assert.Equal(("Retry: Previews", first, 7L), (retried.Title, retried.ParentJobId!.Value, retried.Total));
        Assert.Equal(["2", "3", "5", "7", "11", "13", "17"], handler.Processed.Where(p => p.Job == retry).Select(p => p.Item).Order().OrderBy(int.Parse));
        Assert.Null(await queue.RetryFailedAsync(retry.Value, TestContext.Current.CancellationToken)); // nothing failed this time
    }

    [Fact]
    public async Task History_after_restart_and_pruning()
    {
        await using var c = await TestCatalog.CreateAsync();
        long id;
        await using (var queue = Queue(c, new CountingHandler()))
        {
            await queue.StartAsync(TestContext.Current.CancellationToken);
            id = await queue.EnqueueAsync("test.items", "Import Forest", new CountingHandler.Payload(4, [1]), ct: TestContext.Current.CancellationToken);
            await queue.WaitAsync(id, TestContext.Current.CancellationToken);
        }

        await using (var restarted = Queue(c, new CountingHandler()))
        {
            await restarted.StartAsync(TestContext.Current.CancellationToken);
            var history = await restarted.HistoryAsync(ct: TestContext.Current.CancellationToken);
            var job = Assert.Single(history);
            Assert.Equal(("Import Forest", JobState.Completed, """{"processed":4}""", 1), (job.Title, job.State, job.SummaryJson, job.Errors));
        }

        // 205 finished jobs + one more run: the oldest are pruned to the newest 200.
        await using (var db = c.Db())
        {
            var t0 = DateTime.UtcNow.AddDays(-1);
            db.Jobs.AddRange(Enumerable.Range(0, 205).Select(i => new Job
                { Type = "test.items", Title = $"old {i}", State = JobState.Completed, CreatedAt = t0, FinishedAt = t0.AddSeconds(i) }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var queue = Queue(c, new CountingHandler()))
        {
            await queue.StartAsync(TestContext.Current.CancellationToken);
            await queue.WaitAsync(await queue.EnqueueAsync("test.items", "new", new CountingHandler.Payload(1, []), ct: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        }
        await using var read = c.Db();
        Assert.Equal(JobQueue.HistoryLimit, await read.Jobs.CountAsync(TestContext.Current.CancellationToken));
        Assert.True(await read.Jobs.AnyAsync(j => j.Title == "new", TestContext.Current.CancellationToken));
        Assert.True(await read.Jobs.AnyAsync(j => j.Title == "Import Forest", TestContext.Current.CancellationToken)); // finished after the backdated ones
        Assert.False(await read.Jobs.AnyAsync(j => j.Title == "old 6", TestContext.Current.CancellationToken));
        Assert.True(await read.Jobs.AnyAsync(j => j.Title == "old 7", TestContext.Current.CancellationToken));
        Assert.False(await read.Jobs.AnyAsync(j => j.Title == "old 0", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancel_running_and_pending_jobs()
    {
        await using var c = await TestCatalog.CreateAsync();
        var slow = new CountingHandler("test.slow", JobPool.Ai);
        var hub = new ProgressHub();
        var finished = new ConcurrentQueue<JobSnapshot>();
        hub.Finished += finished.Enqueue;
        await using var queue = new JobQueue(c.Store, [slow], hub);
        await queue.StartAsync(TestContext.Current.CancellationToken);

        var running = await queue.EnqueueAsync("test.slow", "slow", new CountingHandler.Payload(1000, [], 20), ct: TestContext.Current.CancellationToken);
        var pending = await queue.EnqueueAsync("test.slow", "waits (AI pool runs one job)", new CountingHandler.Payload(1, []), ct: TestContext.Current.CancellationToken);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Active.Count);

        await queue.CancelAsync(pending, TestContext.Current.CancellationToken);
        await queue.CancelAsync(running, TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Cancelled, (await queue.WaitAsync(running, TestContext.Current.CancellationToken)).State);
        Assert.Equal(JobState.Cancelled, (await queue.WaitAsync(pending, TestContext.Current.CancellationToken)).State);
        Assert.DoesNotContain(slow.Processed, p => p.Job == pending);
        Assert.Empty(hub.Active);
        Assert.Equal(2, finished.Count);
    }

    [Fact]
    public async Task Failing_handler_marks_the_job_failed()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using var queue = Queue(c, new ThrowingHandler());
        await queue.StartAsync(TestContext.Current.CancellationToken);

        var snap = await queue.WaitAsync(await queue.EnqueueAsync("test.throw", "boom", new { }, ct: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Equal((JobState.Failed, "Disk on fire"), (snap.State, snap.Error));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync("nope", "x", new { }, ct: TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingHandler : IJobHandler
    {
        public string Type => "test.throw";
        public JobPool Pool => JobPool.Io;
        public Task RunAsync(JobContext context, CancellationToken ct) => throw new IOException("Disk on fire");
    }

    [Fact]
    public async Task Eta_converges_for_a_constant_per_item_time()
    {
        await using var c = await TestCatalog.CreateAsync();
        var job = new Job { Type = "t", Title = "t", CreatedAt = DateTime.UtcNow };
        await using (var db = c.Db())
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var progress = new JobProgress(job, c.Store, new ProgressHub(), time);

        progress.SetStage("Generating previews", 2, 6, 200);
        time.Now += TimeSpan.FromSeconds(30); // a slow first item (cold cache)
        progress.Advance();
        for (var i = 0; i < 60; i++)
        {
            time.Now += TimeSpan.FromSeconds(2);
            progress.Advance(1, $"file{i}.png");
        }

        var snap = progress.Snapshot;
        Assert.InRange(progress.SecondsPerItem!.Value, 2.0, 2.1);
        Assert.InRange(snap.EtaSeconds!.Value, 139 * 2.0, 139 * 2.1);
        Assert.Equal(("Generating previews", 61L, "file59.png"), (snap.Stage, snap.Done, snap.CurrentItem));
        Assert.Equal((2 + 61 / 200.0) / 6, snap.Fraction!.Value, 6);

        progress.SetStage("AI pack pass", 3, 6, 10);
        Assert.Null(progress.Snapshot.EtaSeconds); // a new stage starts a new estimate
    }

    [Fact]
    public async Task Last_change_in_a_burst_is_published_without_a_further_update()
    {
        await using var c = await TestCatalog.CreateAsync();
        var job = new Job { Type = "t", Title = "t", CreatedAt = DateTime.UtcNow };
        await using (var db = c.Db())
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var hub = new ProgressHub();
        var progress = new JobProgress(job, c.Store, hub, new ManualTime(DateTimeOffset.UtcNow));

        progress.SetCurrentItem("first.png");     // published at once
        progress.SetCurrentItem("big-archive.zip"); // within the 100 ms window: throttled
        Assert.Equal("first.png", hub.Get(job.Id)!.CurrentItem);

        // Nothing else is reported (a long extraction), yet the latest item arrives.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (hub.Get(job.Id)!.CurrentItem != "big-archive.zip" && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal("big-archive.zip", hub.Get(job.Id)!.CurrentItem);
    }

    [Fact]
    public async Task Container_and_counts_are_published_live_and_keep_their_order()
    {
        await using var c = await TestCatalog.CreateAsync();
        var job = new Job { Type = "t", Title = "t", CreatedAt = DateTime.UtcNow };
        await using (var db = c.Db())
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var hub = new ProgressHub();
        var progress = new JobProgress(job, c.Store, hub, time);

        progress.SetContainer("Packs.zip → Forest.unitypackage");
        progress.SetCount("files found", 1);
        progress.SetCount("importable", 1);
        time.Now += TimeSpan.FromSeconds(1);
        progress.SetCount("files found", 1200); // updated in place, still first
        time.Now += TimeSpan.FromSeconds(1);
        progress.SetCount("importable", 800);

        var published = hub.Get(job.Id)!;
        Assert.Equal("Packs.zip → Forest.unitypackage", published.Container);
        Assert.Equal([new JobCount("files found", 1200), new JobCount("importable", 800)], published.Counts);

        // Live only: the Job row does not carry them, so history snapshots come without.
        await using (var db = c.Db())
        {
            var row = await db.Jobs.SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
            Assert.Empty(JobSnapshot.Of(row).Counts);
            Assert.Null(JobSnapshot.Of(row).Container);
        }
    }
}
