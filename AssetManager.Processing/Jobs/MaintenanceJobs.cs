using AssetManager.Core.Jobs;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Storage;

namespace AssetManager.Processing.Jobs;

public static class JobTypes
{
    public const string RegeneratePreviews = "previews.regenerate";
    public const string StoreOriginals = "storage.store";
    public const string RemoveStoredOriginals = "storage.unstore";
    public const string Compact = "storage.compact";
    public const string Reimport = "assets.reimport";
    /// <summary>Extract to a folder; payload <see cref="Export.ExtractPayload"/>.</summary>
    public const string Extract = "assets.extract";
    /// <summary>Copy to the clipboard as files; payload <see cref="AssetsPayload"/>.</summary>
    public const string Copy = "assets.copy";
    /// <summary>AI enrichment of selected assets (handled in AssetManager.AI); payload <see cref="Import.EnrichPayload"/>.</summary>
    public const string Enrich = "ai.enrich";
    /// <summary>AI description of a collection (handled in AssetManager.AI); payload <see cref="CollectionPayload"/>.</summary>
    public const string DescribeCollection = "ai.describe-collection";
}

/// <summary>Payload of jobs over a set of assets; null ids means "all that apply" (e.g. all outdated previews).</summary>
public sealed record AssetsPayload(long[]? AssetIds);

public sealed record CollectionPayload(long CollectionId);

/// <summary>Regenerates previews for the given assets (or all outdated ones). Skipped assets become failed items.</summary>
public sealed class RegeneratePreviewsJob(PreviewService previews) : IJobHandler
{
    public string Type => JobTypes.RegeneratePreviews;

    public JobPool Pool => JobPool.Render;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = context.Payload<AssetsPayload>().AssetIds ?? (await previews.OutdatedAsync(ct)).ToArray();
        ids = ids.Where(id => context.Includes(id.ToString())).ToArray();
        context.Progress.SetStage("Regenerating previews", 0, 1, ids.Length);
        var done = 0;
        var result = await previews.RegenerateAsync(ids, new SyncProgress(n =>
        {
            context.Progress.Advance(n - done, n < ids.Length ? $"Asset {ids[n]}" : null);
            done = n;
        }), ct);
        foreach (var s in result.Skipped) context.Progress.Fail(s.AssetId.ToString(), s.Name, s.Reason);
        context.Progress.SetSummary(new { regenerated = result.Regenerated, skipped = result.Skipped.Count });
    }
}

public sealed class StoreOriginalsJob(StoredOriginalsService storage) : IJobHandler
{
    public string Type => JobTypes.StoreOriginals;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = (context.Payload<AssetsPayload>().AssetIds ?? []).Where(id => context.Includes(id.ToString())).ToArray();
        context.Progress.SetStage("Storing originals", 0, 1, ids.Length);
        var done = 0;
        var result = await storage.StoreAsync(ids, new SyncProgress(n =>
        {
            context.Progress.Advance(n - done);
            done = n;
        }), ct);
        foreach (var s in result.Skipped) context.Progress.Fail(s.AssetId.ToString(), s.Name, s.Reason);
        context.Progress.SetSummary(new { stored = result.Stored, alreadyStored = result.AlreadyStored, skipped = result.Skipped.Count });
    }
}

public sealed class RemoveStoredOriginalsJob(StoredOriginalsService storage) : IJobHandler
{
    public string Type => JobTypes.RemoveStoredOriginals;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = (context.Payload<AssetsPayload>().AssetIds ?? []).Where(id => context.Includes(id.ToString())).ToArray();
        context.Progress.SetStage("Removing stored originals", 0, 1, ids.Length);
        var removed = await storage.RemoveAsync(ids, ct);
        context.Progress.Advance(ids.Length);
        context.Progress.SetSummary(new { removed });
    }
}

public sealed class CompactJob(StoredOriginalsService storage) : IJobHandler
{
    public string Type => JobTypes.Compact;

    public JobPool Pool => JobPool.Io;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        context.Progress.SetStage("Compacting the catalog", 0, 1, 1);
        await storage.CompactAsync(ct);
        context.Progress.Advance();
    }
}

/// <summary>An <see cref="IProgress{T}"/> that calls back synchronously (the built-in one posts to a sync context).</summary>
public sealed class SyncProgress(Action<int> report) : IProgress<int>
{
    public void Report(int value) => report(value);
}
