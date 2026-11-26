using System.Collections.Concurrent;
using System.Text.Json;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Jobs;

/// <summary>In-memory live job state; the queue publishes into it, the UI subscribes.</summary>
public sealed class ProgressHub : IProgressHub
{
    private readonly ConcurrentDictionary<long, JobSnapshot> _active = new();

    public IReadOnlyList<JobSnapshot> Active => _active.Values.OrderBy(s => s.Id).ToList();

    public JobSnapshot? Get(long jobId) => _active.GetValueOrDefault(jobId);

    public event Action<JobSnapshot>? Changed;

    public event Action<JobSnapshot>? Finished;

    public void Publish(JobSnapshot s)
    {
        _active[s.Id] = s;
        Changed?.Invoke(s);
    }

    public void Finish(JobSnapshot s)
    {
        _active.TryRemove(s.Id, out _);
        Changed?.Invoke(s);
        Finished?.Invoke(s);
    }
}

/// <summary>
/// The <see cref="IJobProgress"/> of one running job: keeps the live snapshot, publishes it to the hub (at most
/// every 100 ms, with a trailing publish so the latest change always arrives), persists it to the Job row (at most once per second, and at every stage change), buffers item
/// errors, and estimates the remaining time from an exponential moving average of the per-item time in the stage.
/// </summary>
public sealed class JobProgress : IJobProgress
{
    /// <summary>Weight of the newest per-item sample in the moving average.</summary>
    public const double EtaAlpha = 0.1;

    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PersistEvery = TimeSpan.FromSeconds(1);

    private readonly CatalogStore _catalog;
    private readonly ProgressHub _hub;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<JobItemError> _pendingErrors = [];
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Job _job;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersist;
    private DateTimeOffset _lastAdvance;
    private double? _secondsPerItem;
    private bool _dirty;
    private bool _forcePersist;
    private bool _finished;
    private ITimer? _trailing;
    private string? _container;
    private readonly List<JobCount> _counts = [];

    public JobProgress(Job job, CatalogStore catalog, ProgressHub hub, TimeProvider? time = null)
    {
        _job = job;
        _catalog = catalog;
        _hub = hub;
        _time = time ?? TimeProvider.System;
        _lastPersist = _lastAdvance = _time.GetUtcNow();
    }

    public JobSnapshot Snapshot
    {
        get { lock (_lock) return Live(); }
    }

    /// <summary>The job's snapshot plus the live-only fields (container, counts). Called under <see cref="_lock"/>.</summary>
    private JobSnapshot Live() => JobSnapshot.Of(_job) with { Container = _container, Counts = [.. _counts] };

    /// <summary>Current moving-average seconds per item (null until two advances happened in the stage).</summary>
    public double? SecondsPerItem
    {
        get { lock (_lock) return _secondsPerItem; }
    }

    public void SetStage(string name, int index, int count, long total = 0) => Update(() =>
    {
        _job.Stage = name;
        _job.StageIndex = index;
        _job.StageCount = Math.Max(count, index + 1);
        _job.Done = 0;
        _job.Total = total;
        _job.EtaSeconds = null;
        _secondsPerItem = null;
        _lastAdvance = _time.GetUtcNow();
        _forcePersist = true;
    });

    public void SetTotal(long total) => Update(() =>
    {
        _job.Total = total;
        Eta();
    });

    public void Advance(long count = 1, string? currentItem = null) => Update(() =>
    {
        var now = _time.GetUtcNow();
        if (count > 0)
        {
            var sample = (now - _lastAdvance).TotalSeconds / count;
            _secondsPerItem = _secondsPerItem is { } avg ? EtaAlpha * sample + (1 - EtaAlpha) * avg : sample;
            _job.Done += count;
        }
        _lastAdvance = now;
        if (currentItem is not null) _job.CurrentItem = currentItem;
        Eta();
    });

    public void SetCurrentItem(string? item) => Update(() => _job.CurrentItem = item);

    public void SetContainer(string? container) => Update(() => _container = container);

    public void SetCount(string label, long value) => Update(() =>
    {
        var i = _counts.FindIndex(c => c.Label == label);
        if (i >= 0) _counts[i] = new JobCount(label, value);
        else _counts.Add(new JobCount(label, value));
    });

    public void Warn(string itemRef, string? itemName, string reason) => Update(() =>
    {
        _job.Warnings++;
        _pendingErrors.Add(new JobItemError { JobId = _job.Id, ItemRef = itemRef, ItemName = itemName, Reason = reason, IsWarning = true });
    });

    public void Fail(string itemRef, string? itemName, string reason) => Update(() =>
    {
        _job.Errors++;
        _pendingErrors.Add(new JobItemError { JobId = _job.Id, ItemRef = itemRef, ItemName = itemName, Reason = reason });
    });

    public void SetSummary(object summary) => Update(() =>
    {
        _job.SummaryJson = JsonSerializer.Serialize(summary, JobJson.Options);
        _forcePersist = true;
    });

    private void Eta() => _job.EtaSeconds = _secondsPerItem is { } s && _job.Total > 0 ? Math.Max(0, _job.Total - _job.Done) * s : null;

    private void Update(Action change)
    {
        JobSnapshot? publish = null;
        var persist = false;
        lock (_lock)
        {
            change();
            _dirty = true;
            var now = _time.GetUtcNow();
            if (now - _lastPublish >= PublishEvery)
            {
                _lastPublish = now;
                publish = Live();
            }
            else
            {
                ArmTrailingPublish(now);
            }
            if (_forcePersist || now - _lastPersist >= PersistEvery)
            {
                _forcePersist = false;
                _lastPersist = now;
                persist = true;
            }
        }
        if (publish is not null) _hub.Publish(publish);
        if (persist) _ = PersistAsync(CancellationToken.None);
    }

    /// <summary>
    /// A throttled change must still reach the UI: e.g. the name of an archive set just before a long extraction
    /// would otherwise stay unpublished until the next change. Arms one timer for the end of the publish window.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private void ArmTrailingPublish(DateTimeOffset now)
    {
        if (_trailing is not null || _finished) return;
        var wait = PublishEvery - (now - _lastPublish);
        if (wait < TimeSpan.FromMilliseconds(1)) wait = TimeSpan.FromMilliseconds(1);
        _trailing = _time.CreateTimer(_ => PublishTrailing(), null, wait, Timeout.InfiniteTimeSpan);
    }

    private void PublishTrailing()
    {
        lock (_lock)
        {
            _trailing?.Dispose();
            _trailing = null;
            // Published under the lock, so a finished job can never be re-added to the hub's active list.
            if (_finished) return;
            _lastPublish = _time.GetUtcNow();
            _hub.Publish(Live());
        }
    }

    /// <summary>Marks the job running and publishes it.</summary>
    internal async Task StartAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _job.State = JobState.Running;
            _job.StartedAt = _time.GetUtcNow().UtcDateTime;
            _job.Attempts++;
            _dirty = true;
        }
        await PersistAsync(ct);
        _hub.Publish(Snapshot);
    }

    /// <summary>Writes the final state and the remaining errors; returns the final snapshot.</summary>
    internal async Task<JobSnapshot> FinishAsync(JobState state, string? error)
    {
        lock (_lock)
        {
            _job.State = state;
            _job.Error = error;
            _job.FinishedAt = _time.GetUtcNow().UtcDateTime;
            _job.EtaSeconds = null;
            if (state == JobState.Completed) _job.CurrentItem = null;
            _dirty = true;
            _finished = true;
            _container = null;
            _trailing?.Dispose();
            _trailing = null;
        }
        await PersistAsync(CancellationToken.None);
        var final = Snapshot;
        _hub.Finish(final);
        return final;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await _persistGate.WaitAsync(ct);
        try
        {
            Job j;
            List<JobItemError> errors;
            lock (_lock)
            {
                if (!_dirty && _pendingErrors.Count == 0) return;
                _dirty = false;
                j = new Job();
                Copy(_job, j);
                errors = [.. _pendingErrors];
                _pendingErrors.Clear();
            }
            await using var db = _catalog.CreateDbContext();
            await db.Jobs.Where(x => x.Id == j.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, j.State).SetProperty(x => x.Attempts, j.Attempts)
                .SetProperty(x => x.StartedAt, j.StartedAt).SetProperty(x => x.FinishedAt, j.FinishedAt)
                .SetProperty(x => x.Stage, j.Stage).SetProperty(x => x.StageIndex, j.StageIndex).SetProperty(x => x.StageCount, j.StageCount)
                .SetProperty(x => x.Done, j.Done).SetProperty(x => x.Total, j.Total).SetProperty(x => x.CurrentItem, j.CurrentItem)
                .SetProperty(x => x.Warnings, j.Warnings).SetProperty(x => x.Errors, j.Errors).SetProperty(x => x.EtaSeconds, j.EtaSeconds)
                .SetProperty(x => x.SummaryJson, j.SummaryJson).SetProperty(x => x.Error, j.Error), ct);
            if (errors.Count > 0)
            {
                db.JobItemErrors.AddRange(errors);
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static void Copy(Job from, Job to)
    {
        to.Id = from.Id; to.State = from.State; to.Attempts = from.Attempts; to.StartedAt = from.StartedAt; to.FinishedAt = from.FinishedAt;
        to.Stage = from.Stage; to.StageIndex = from.StageIndex; to.StageCount = from.StageCount; to.Done = from.Done; to.Total = from.Total;
        to.CurrentItem = from.CurrentItem; to.Warnings = from.Warnings; to.Errors = from.Errors; to.EtaSeconds = from.EtaSeconds;
        to.SummaryJson = from.SummaryJson; to.Error = from.Error;
    }
}
