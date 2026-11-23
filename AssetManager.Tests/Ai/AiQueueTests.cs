using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data.Jobs;
using AssetManager.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

public class AiQueueTests
{
    private static (AiQueue Queue, JobQueue Jobs, AiRuntime Runtime) Create(TestCatalog c, TimeProvider? time = null, params IJobHandler[] handlers)
    {
        var hub = new ProgressHub();
        var jobs = new JobQueue(c.Store, handlers, hub);
        var runtime = new AiRuntime(new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")));
        return (new AiQueue(c.Store, runtime, jobs, hub, time), jobs, runtime);
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        await using var c = await TestCatalog.CreateAsync();
        var (queue, jobs, runtime) = Create(c);
        await using var _ = jobs;
        await using var __ = runtime;
        var fake = new FakeChatClient("""{"description":"ok"}""");
        var gated = new GatedChatClient(fake, queue);

        queue.Pause();
        var work = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
                await new StructuredChat(gated).AskAsync<CollectionDescriptionResult>([new ChatMessage(ChatRole.User, $"item {i}")], ct: TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.True(queue.IsPaused);
        Assert.Empty(fake.Requests); // nothing reaches the model while paused
        Assert.False(work.IsCompleted);

        queue.Resume();
        await work.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(["item 0", "item 1", "item 2"], fake.Requests.Select(r => r.Messages[^1].Text));
    }

    [Fact]
    public async Task Queue_overview()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using (var db = c.Db())
        {
            var source = new Source { DisplayName = "big pack" };
            for (var i = 0; i < 5000; i++) source.Assets.Add(new Asset { Name = $"a{i}", AiStatus = AiStatus.Pending });
            source.Assets.Add(new Asset { Name = "done", AiStatus = AiStatus.Enriched });
            db.Add(source);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var (queue, jobs, runtime) = Create(c, time);
        await using var _ = jobs;
        await using var __ = runtime;

        var empty = await queue.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal((5000, 0d, (TimeSpan?)null), (empty.Pending, empty.ItemsPerMinute, empty.TimeToDrain)); // no rate yet: no estimate

        // 8 minutes at 10 items per minute (one every 6 s), 1 in 20 failing.
        for (var i = 0; i < 80; i++)
        {
            time.Now += TimeSpan.FromSeconds(6);
            queue.RecordItem(success: i % 20 != 0);
        }
        var status = await queue.StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5000, status.Pending);
        Assert.InRange(status.ItemsPerMinute, 9.9, 10.1); // measured over the last 5 minutes only
        Assert.InRange(status.TimeToDrain!.Value.TotalMinutes, 495, 505);
        Assert.Equal((76, 4), (status.Completed, status.Failed));

        queue.ResetRun();
        Assert.Equal((0, 0, 0d), ((await queue.StatusAsync(TestContext.Current.CancellationToken)).Completed, queue.ItemsPerMinute > 0 ? 1 : 0, queue.ItemsPerMinute));
    }

    [Fact]
    public async Task Cancel_stops_ai_jobs_and_empties_the_queue()
    {
        await using var c = await TestCatalog.CreateAsync();
        await using (var db = c.Db())
        {
            var source = new Source { DisplayName = "p" };
            for (var i = 0; i < 10; i++) source.Assets.Add(new Asset { Name = $"a{i}", AiStatus = AiStatus.Pending });
            db.Add(source);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var slow = new CountingHandler("ai.enrich", JobPool.Ai);
        var (queue, jobs, runtime) = Create(c, null, slow);
        await using var _ = jobs;
        await using var __ = runtime;
        await jobs.StartAsync(TestContext.Current.CancellationToken);
        var id = await jobs.EnqueueAsync("ai.enrich", "Enrich", new CountingHandler.Payload(1000, [], 20), ct: TestContext.Current.CancellationToken);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        queue.Pause();

        await queue.CancelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobState.Cancelled, (await jobs.WaitAsync(id, TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, await queue.PendingCountAsync(TestContext.Current.CancellationToken));
        Assert.False(queue.IsPaused);
        await using var read = c.Db();
        Assert.Equal(10, await read.Assets.CountAsync(a => a.AiStatus == AiStatus.NotEnriched, TestContext.Current.CancellationToken));
    }
}
