using System.Text.Json;
using System.Text.Json.Nodes;
using AssetManager.Core.Domain;

namespace AssetManager.Core.Jobs;

/// <summary>
/// The one progress handle every job reports through (design D8): stage, done/total, current item, warnings and
/// per-item errors. Implementations persist throttled snapshots and compute the ETA.
/// </summary>
public interface IJobProgress
{
    /// <summary>Starts stage <paramref name="index"/> (0-based) of <paramref name="count"/>; resets done/total and the ETA.</summary>
    void SetStage(string name, int index, int count, long total = 0);

    void SetTotal(long total);

    /// <summary>Marks <paramref name="count"/> items done; <paramref name="currentItem"/> names the item being worked on next.</summary>
    void Advance(long count = 1, string? currentItem = null);

    void SetCurrentItem(string? item);

    void Warn(string itemRef, string? itemName, string reason);

    /// <summary>Records a failed item (kept as a <see cref="JobItemError"/>; "Retry failed" re-runs exactly these).</summary>
    void Fail(string itemRef, string? itemName, string reason);

    /// <summary>Outcome counts kept with the finished job, e.g. <c>new { updated = 3, added = 1, removed = 0 }</c>.</summary>
    void SetSummary(object summary);
}

/// <summary>Which worker pool runs a job type (design D8: CPU work in parallel, one AI and one renderer job at a time).</summary>
public enum JobPool { Cpu, Io, Ai, Render }

/// <summary>Context a handler runs with.</summary>
public sealed class JobContext(Job job, IJobProgress progress)
{
    public const string OnlyItemsProperty = "onlyItems";

    public Job Job { get; } = job;
    public IJobProgress Progress { get; } = progress;

    public T Payload<T>() => JsonSerializer.Deserialize<T>(Job.PayloadJson, JobJson.Options)!;

    /// <summary>For "Retry failed" jobs: the item refs to process; null means all items of the payload.</summary>
    public IReadOnlySet<string>? OnlyItems =>
        JsonNode.Parse(Job.PayloadJson) is JsonObject o && o[OnlyItemsProperty] is JsonArray a
            ? a.Select(n => n!.GetValue<string>()).ToHashSet()
            : null;

    public bool Includes(string itemRef) => OnlyItems is not { } only || only.Contains(itemRef);
}

/// <summary>Runs one job type. Register implementations in DI; the queue finds them by <see cref="Type"/>.</summary>
public interface IJobHandler
{
    string Type { get; }

    JobPool Pool { get; }

    Task RunAsync(JobContext context, CancellationToken ct);
}

/// <summary>A job's live state as the UI sees it.</summary>
public sealed record JobSnapshot(
    long Id, string Type, string Title, JobState State, string? Stage, int StageIndex, int StageCount,
    long Done, long Total, string? CurrentItem, int Warnings, int Errors, DateTime CreatedAt, DateTime? StartedAt,
    DateTime? FinishedAt, double? EtaSeconds, string? Error, string? SummaryJson, long? ParentJobId)
{
    /// <summary>Progress of the current stage, 0..1 (null while the total is unknown).</summary>
    public double? StageFraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;

    /// <summary>Overall progress across stages, 0..1: finished stages count fully, the current one by its fraction.</summary>
    public double? Fraction => State == JobState.Completed ? 1
        : StageCount <= 1 ? StageFraction
        : (StageIndex + (StageFraction ?? 0)) / StageCount;

    public TimeSpan Elapsed(DateTime now) => StartedAt is { } s ? (FinishedAt ?? now) - s : TimeSpan.Zero;

    public bool IsActive => State is JobState.Pending or JobState.Running;

    public static JobSnapshot Of(Job j) => new(j.Id, j.Type, j.Title, j.State, j.Stage, j.StageIndex, j.StageCount, j.Done, j.Total,
        j.CurrentItem, j.Warnings, j.Errors, j.CreatedAt, j.StartedAt, j.FinishedAt, j.EtaSeconds, j.Error, j.SummaryJson, j.ParentJobId);
}

/// <summary>Live job state for the UI: active jobs and those that just finished.</summary>
public interface IProgressHub
{
    IReadOnlyList<JobSnapshot> Active { get; }

    JobSnapshot? Get(long jobId);

    /// <summary>Raised (throttled) whenever a job's snapshot changes.</summary>
    event Action<JobSnapshot>? Changed;

    /// <summary>Raised once when a job completes, fails or is cancelled.</summary>
    event Action<JobSnapshot>? Finished;
}

public interface IJobQueue
{
    Task<long> EnqueueAsync(string type, string title, object payload, long? parentJobId = null, CancellationToken ct = default);

    Task CancelAsync(long jobId, CancellationToken ct = default);

    /// <summary>Creates a new job of the same type that processes only the failed items of <paramref name="jobId"/>.</summary>
    Task<long?> RetryFailedAsync(long jobId, CancellationToken ct = default);

    /// <summary>Completes when the job has finished (for callers that need the result, and for tests).</summary>
    Task<JobSnapshot> WaitAsync(long jobId, CancellationToken ct = default);
}

public static class JobJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
