using AssetManager.Core.Jobs;

namespace AssetManager.Tests.Core;

public class BackgroundIoTests
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    [Fact]
    public async Task Work_in_the_scope_and_what_it_starts_runs_in_background_mode()
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<bool>();
        await Task.Run(async () =>
        {
            using (BackgroundIo.Enter())
            {
                seen.Add(BackgroundIo.IsCurrentThreadBackground);
                await Task.Yield(); // continuation on another pool thread
                seen.Add(BackgroundIo.IsCurrentThreadBackground);
                seen.Add(await Task.Run(() => BackgroundIo.IsCurrentThreadBackground, ct));
                var parallel = new System.Collections.Concurrent.ConcurrentBag<bool>();
                await Parallel.ForEachAsync(Enumerable.Range(0, 8), ct, (_, _) =>
                {
                    parallel.Add(BackgroundIo.IsCurrentThreadBackground);
                    return ValueTask.CompletedTask;
                });
                seen.AddRange(parallel);
            }
            seen.Add(!BackgroundIo.IsCurrentThreadBackground); // left again when the scope ends
        }, ct);

        Assert.All(seen, b => Assert.Equal(Windows, b));
    }

    [Fact]
    public async Task Pool_threads_return_to_normal_mode_outside_the_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        // Plenty of background work on pool threads, then plenty of normal work: none of the normal work may run in background mode.
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            using (BackgroundIo.Enter()) await Task.Delay(5, ct);
        }, ct)));
        var normal = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await Task.Delay(1, ct);
            return BackgroundIo.IsCurrentThreadBackground;
        }, ct)));

        Assert.DoesNotContain(true, normal);
    }

    [Fact]
    public void Thread_with_a_synchronization_context_is_never_switched()
    {
        bool? inside = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            using (BackgroundIo.Enter()) inside = BackgroundIo.IsCurrentThreadBackground;
        });
        thread.Start();
        thread.Join();

        Assert.False(inside);
    }

    [Fact]
    public async Task Scope_flag_flows_but_does_not_leak()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.False(BackgroundIo.IsActive);
        using (BackgroundIo.Enter())
            Assert.True(await Task.Run(() => BackgroundIo.IsActive, ct));
        Assert.False(BackgroundIo.IsActive);
    }
}
