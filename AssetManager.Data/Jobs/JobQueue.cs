using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Data.Jobs;

/// <summary>
/// Persistent job queue (design D8). Jobs are rows in the Job table; workers per <see cref="JobPool"/> read job ids
/// from channels. On start, jobs that were pending or interrupted while running are queued again. Every job runs
/// through a <see cref="JobProgress"/> handle; finished jobs are kept as history (the newest <see cref="HistoryLimit"/>).
/// </summary>
public sealed class JobQueue : IJobQueue, IAsyncDisposable
{
    public const int HistoryLimit = 200;

    private readonly CatalogStore _catalog;
    // Handlers are resolved on first use: several of them enqueue follow-up jobs, so they depend on this queue.
    private readonly Lazy<Dictionary<string, IJobHandler>> _handlerMap;

    private Dictionary<string, IJobHandler> _handlers => _handlerMap.Value;
    private readonly ProgressHub _hub;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<JobPool, Channel<long>> _channels = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JobSnapshot>> _waiters = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = [];
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly StartupSignals? _signals;
    private bool _started;

    public JobQueue(CatalogStore catalog, IEnumerable<IJobHandler> handlers, ProgressHub hub, ILogger<JobQueue>? logger = null, TimeProvider? time = null,
        StartupSignals? signals = null)
        : this(catalog, () => handlers, hub, logger, time, signals) { }

    /// <param name="handlers">Resolved on first use, so handlers may depend on <see cref="IJobQueue"/>.</param>
    /// <param name="signals">When given, interrupted jobs wait for the window (design D12); without it they resume at once.</param>
    public JobQueue(CatalogStore catalog, Func<IEnumerable<IJobHandler>> handlers, ProgressHub hub, ILogger<JobQueue>? logger = null, TimeProvider? time = null,
        StartupSignals? signals = null)
    {
        _catalog = catalog;
        _signals = signals;
        _handlerMap = new(() => handlers().ToDictionary(h => h.Type));
        _hub = hub;
        _log = logger ?? (ILogger)NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        foreach (var pool in Enum.GetValues<JobPool>()) _channels[pool] = Channel.CreateUnbounded<long>();
    }

    /// <summary>Concurrent jobs per pool. Handlers parallelize their own items (e.g. previews up to the CPU count).</summary>
    public Dictionary<JobPool, int> PoolSizes { get; } = new() { [JobPool.Cpu] = 2, [JobPool.Io] = 2, [JobPool.Ai] = 1, [JobPool.Render] = 1 };

    /// <summary>How long interrupted jobs wait after the window is ready before they run again (design D12).</summary>
    public TimeSpan ResumeSettle { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Interrupted jobs resume after this long even if the window never reports ready.</summary>
    public TimeSpan ResumeFallback { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// After the window, how long interrupted jobs wait at most for the AI models to load: loading gigabytes of model
    /// while a job fills the same drive with extracted files makes both crawl (design D12).
    /// </summary>
    public TimeSpan ResumeAiWait { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Completes once interrupted jobs have been queued again (immediately without startup signals).</summary>
    public Task Resumed => _resumed.Task;

    private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Starts the workers and queues pending and interrupted jobs again. Safe to call more than once.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            if (_started) return;
            _started = true;
            List<Job> resume;
            await using (var db = _catalog.CreateDbContext())
            {
                // A job that was running when the app closed starts over (its handler skips work already done).
                await db.Jobs.Where(j => j.State == JobState.Running).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Pending), ct);
                resume = await db.Jobs.AsNoTracking().Where(j => j.State == JobState.Pending).OrderBy(j => j.Id).ToListAsync(ct);
            }
            foreach (var (pool, size) in PoolSizes)
                for (var i = 0; i < size; i++) _workers.Add(Task.Run(() => WorkerAsync(pool, _shutdown.Token)));
            // Shown as waiting right away; run once the window is ready and the AI has loaded, so they do not compete
            // with either for the disk.
            foreach (var j in resume) _hub.Publish(JobSnapshot.Of(j));
            if (_signals is null || resume.Count == 0) await ResumeAsync(resume, ct);
            else
            {
                _log.LogInformation("{Count} interrupted jobs will resume once the window is ready and the AI has loaded", resume.Count);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _signals.WaitForWindowAsync(ResumeSettle, ResumeFallback, _time, _shutdown.Token);
                        await Task.WhenAny(_signals.AiSettled, Task.Delay(ResumeAiWait, _time, _shutdown.Token));
                        _shutdown.Token.ThrowIfCancellationRequested();
                        await ResumeAsync(resume, _shutdown.Token);
                    }
                    catch (OperationCanceledException) { } // shutting down: they stay pending for the next start
                }, CancellationToken.None);
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task ResumeAsync(List<Job> resume, CancellationToken ct)
    {
        foreach (var j in resume) await _channels[PoolOf(j.Type)].Writer.WriteAsync(j.Id, ct);
        if (resume.Count > 0) _log.LogInformation("Resumed {Count} pending jobs", resume.Count);
        _resumed.TrySetResult();
    }

    public async Task<long> EnqueueAsync(string type, string title, object payload, long? parentJobId = null, CancellationToken ct = default)
    {
        if (!_handlers.ContainsKey(type)) throw new ArgumentException($"No handler for job type '{type}'.", nameof(type));
        var job = new Job
        {
            Type = type,
            Title = title,
            PayloadJson = payload as string ?? JsonSerializer.Serialize(payload, JobJson.Options),
            State = JobState.Pending,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ParentJobId = parentJobId,
        };
        await using (var db = _catalog.CreateDbContext())
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
        }
        _hub.Publish(JobSnapshot.Of(job));
        await _channels[PoolOf(type)].Writer.WriteAsync(job.Id, ct);
        return job.Id;
    }

    public async Task CancelAsync(long jobId, CancellationToken ct = default)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            return;
        }
        await using var db = _catalog.CreateDbContext();
        var n = await db.Jobs.Where(j => j.Id == jobId && j.State == JobState.Pending).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, JobState.Cancelled).SetProperty(j => j.FinishedAt, _time.GetUtcNow().UtcDateTime), ct);
        if (n > 0) Complete(JobSnapshot.Of(await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct)), finishHub: true);
    }

    public async Task<long?> RetryFailedAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = _catalog.CreateDbContext();
        var job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == jobId, ct);
        var failed = await db.JobItemErrors.Where(e => e.JobId == jobId && !e.IsWarning).Select(e => e.ItemRef).Distinct().ToListAsync(ct);
        if (failed.Count == 0) return null;
        var payload = JsonNode.Parse(job.PayloadJson) as JsonObject ?? new JsonObject();
        payload[JobContext.OnlyItemsProperty] = new JsonArray(failed.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
        var title = job.Title.StartsWith("Retry: ") ? job.Title : "Retry: " + job.Title;
        return await EnqueueAsync(job.Type, title, payload.ToJsonString(), jobId, ct);
    }

    public async Task<JobSnapshot> WaitAsync(long jobId, CancellationToken ct = default)
    {
        var tcs = _waiters.GetOrAdd(jobId, _ => new TaskCompletionSource<JobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously));
        await using (var db = _catalog.CreateDbContext())
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) throw new KeyNotFoundException($"Job {jobId} does not exist.");
            if (job.IsFinished && _waiters.TryRemove(jobId, out var done)) done.TrySetResult(JobSnapshot.Of(job));
        }
        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>Finished and active jobs, newest first (the Jobs page history).</summary>
    public async Task<List<JobSnapshot>> HistoryAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = _catalog.CreateDbContext();
        var jobs = await db.Jobs.AsNoTracking().OrderByDescending(j => j.Id).Take(take).ToListAsync(ct);
        return jobs.Select(j => _hub.Get(j.Id) ?? JobSnapshot.Of(j)).ToList();
    }

    public async Task<List<JobItemError>> ItemErrorsAsync(long jobId, CancellationToken ct = default)
    {
        await using var db = _catalog.CreateDbContext();
        return await db.JobItemErrors.AsNoTracking().Where(e => e.JobId == jobId).OrderBy(e => e.Id).ToListAsync(ct);
    }

    private JobPool PoolOf(string type) => _handlers.TryGetValue(type, out var h) ? h.Pool : JobPool.Io;

    private async Task WorkerAsync(JobPool pool, CancellationToken stop)
    {
        // Jobs, and everything they start, run at background CPU/I/O priority so the window's reads go first (design D11).
        using var background = BackgroundIo.Enter();
        try
        {
            await foreach (var id in _channels[pool].Reader.ReadAllAsync(stop))
                await RunAsync(id, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task RunAsync(long jobId, CancellationToken stop)
    {
        Job? job;
        await using (var db = _catalog.CreateDbContext())
            job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, stop);
        if (job is not { State: JobState.Pending }) return; // cancelled while queued, or already handled

        var progress = new JobProgress(job, _catalog, _hub, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[jobId] = cts;
        var state = JobState.Completed;
        string? error = null;
        try
        {
            await progress.StartAsync(stop);
            if (!_handlers.TryGetValue(job.Type, out var handler)) throw new InvalidOperationException($"No handler for job type '{job.Type}'.");
            await handler.RunAsync(new JobContext(job, progress), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (stop.IsCancellationRequested) return; // app shutting down: the job stays Running and resumes next start
            state = JobState.Cancelled;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Job {Id} ({Type}) failed", jobId, job.Type);
            state = JobState.Failed;
            error = e.Message;
        }
        finally
        {
            _running.TryRemove(jobId, out _);
        }

        var final = await progress.FinishAsync(state, error);
        Complete(final, finishHub: false);
        await PruneAsync();
    }

    private void Complete(JobSnapshot final, bool finishHub)
    {
        if (finishHub) _hub.Finish(final);
        if (_waiters.TryRemove(final.Id, out var tcs)) tcs.TrySetResult(final);
    }

    private async Task PruneAsync()
    {
        try
        {
            await using var db = _catalog.CreateDbContext();
            var keep = db.Jobs.Where(j => j.FinishedAt != null).OrderByDescending(j => j.FinishedAt).ThenByDescending(j => j.Id).Take(HistoryLimit).Select(j => j.Id);
            var old = await db.Jobs.Where(j => j.FinishedAt != null && !keep.Contains(j.Id)).Select(j => j.Id).ToListAsync();
            if (old.Count == 0) return;
            await db.JobItemErrors.Where(e => old.Contains(e.JobId)).ExecuteDeleteAsync();
            await db.Jobs.Where(j => old.Contains(j.Id)).ExecuteDeleteAsync();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Pruning job history failed");
        }
    }

    private int _disposed;

    /// <summary>Stops the workers. Idempotent: DI disposes the queue once per registration (JobQueue and IJobQueue).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _shutdown.CancelAsync();
        foreach (var c in _channels.Values) c.Writer.TryComplete();
        try { await Task.WhenAll(_workers); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}

/// <summary>Starts the queue once the catalog is open (after the vector index, before AI).</summary>
internal sealed class JobQueueStartupTask(JobQueue queue) : IStartupTask
{
    public int Order => 15;

    public Task RunAsync(CancellationToken ct) => queue.StartAsync(ct);
}
