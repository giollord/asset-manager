using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Import;

/// <summary>
/// Confirms an import session (design D8, spec "Confirm import"): each staged source commits in one transaction that
/// drops its excluded assets and clears <c>ImportSessionId</c> on the rest. Asset ids do not change, so a still-running
/// import job simply continues its AI stages on the committed assets (found through
/// <see cref="Source.CommittedFromSessionId"/>). If that job is no longer running while assets still await AI, it
/// is queued again; it skips its finished stages and resumes with the pending ones.
/// </summary>
public sealed class ImportCommitJob(CatalogStore catalog, IJobQueue jobs, SourceVersions versions, ILogger<ImportCommitJob>? logger = null) : IJobHandler
{
    public const string JobType = "import.commit";
    public const int Stage = 5;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    public string Type => JobType;

    public JobPool Pool => JobPool.Cpu;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var sessionId = context.Payload<ImportPayload>().SessionId;
        var p = context.Progress;
        List<Source> sources;
        await using (var db = catalog.CreateDbContext())
            sources = await db.AllSources.AsNoTracking().Where(s => s.ImportSessionId == sessionId).ToListAsync(ct);
        p.SetStage(ImportJob.StageNames[Stage], Stage, ImportJob.StageCount, sources.Count);

        // Innermost sources first, so a container left without assets or children can be dropped.
        var byId = sources.ToDictionary(s => s.Id);
        int Depth(Source s) => s.ParentSourceId is { } parent && byId.TryGetValue(parent, out var ps) ? 1 + Depth(ps) : 0;
        int committedAssets = 0, excluded = 0, emptySources = 0, replaced = 0;
        var restore = new List<long>();
        foreach (var source in sources.OrderByDescending(Depth).ThenBy(s => s.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (source.ReplacesSourceId is { } oldId && source.ReplaceAccepted != false && await ReplaceAsync(sessionId, source.Id, oldId, restore, ct))
            {
                replaced++;
                await using (var db = catalog.CreateDbContext())
                    committedAssets += await db.Assets.CountAsync(a => a.SourceId == oldId, ct);
                p.Advance(1, source.DisplayName);
                continue;
            }
            var (kept, dropped, empty) = await CommitSourceAsync(sessionId, source.Id, ct);
            committedAssets += kept;
            excluded += dropped;
            if (empty) emptySources++;
            p.Advance(1, source.DisplayName);
        }

        long? importJob;
        int pendingAi;
        await using (var db = catalog.CreateDbContext())
        {
            // The pack pass reads the noted files and documents; if it has not run yet the import job removes them.
            if (await db.ImportSessions.AnyAsync(s => s.Id == sessionId && (s.StagedThrough >= 4 || s.AiDepth == AiDepth.None), ct))
                await db.StagedSkips.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(ct);
            await TagService.DeleteUnusedAsync(db, ct);
            await db.ImportSessions.Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Committed), ct);
            var session = await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
            importJob = session.JobId;
            pendingAi = session.AiDepth == AiDepth.None ? 0
                : await db.Assets.CountAsync(a => a.Source.CommittedFromSessionId == sessionId && a.AiStatus == AiStatus.Pending, ct);
            if (pendingAi > 0 && importJob is { } j && !await db.Jobs.AnyAsync(x => x.Id == j && (x.State == JobState.Pending || x.State == JobState.Running), ct))
            {
                var title = await db.Jobs.Where(x => x.Id == j).Select(x => x.Title).FirstOrDefaultAsync(ct) ?? "Import";
                var resumed = await jobs.EnqueueAsync(ImportJob.JobType, $"AI enrichment: {title}", new ImportPayload(sessionId), j, ct);
                await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, resumed), ct);
            }
        }
        if (restore.Count > 0)
            await jobs.EnqueueAsync(JobTypes.StoreOriginals, $"Store originals of {restore.Count} updated assets", new AssetsPayload([.. restore]), ct: ct);
        catalog.NotifyContentChanged();
        _log.LogInformation("Import {Session} committed: {Assets} assets, {Excluded} excluded, {Pending} awaiting AI", sessionId, committedAssets, excluded, pendingAi);
        p.SetSummary(new { assets = committedAssets, excluded, replaced, pendingAi, emptySources });
    }

    /// <summary>Applies an accepted new-version replacement in one transaction; false when the old source is gone.</summary>
    private async Task<bool> ReplaceAsync(long sessionId, long stagedId, long oldId, List<long> restore, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        if (!await db.Sources.AnyAsync(s => s.Id == oldId, ct)) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        restore.AddRange(await versions.ReplaceAsync(db, sessionId, stagedId, oldId, ct));
        await tx.CommitAsync(ct);
        return true;
    }

    /// <returns>Committed and dropped asset counts, and whether the source was dropped for being empty.</returns>
    private async Task<(int Kept, int Dropped, bool Empty)> CommitSourceAsync(long sessionId, long sourceId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dropped = await db.AllAssets.Where(a => a.SourceId == sourceId && a.ImportSessionId == sessionId && !a.Included).ExecuteDeleteAsync(ct);
        var kept = await db.AllAssets.Where(a => a.SourceId == sourceId && a.ImportSessionId == sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ImportSessionId, (long?)null), ct);
        var empty = kept == 0
            && !await db.AllAssets.AnyAsync(a => a.SourceId == sourceId, ct)
            && !await db.AllSources.AnyAsync(s => s.ParentSourceId == sourceId, ct);
        if (empty)
            await db.AllSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync(ct);
        else
            await db.AllSources.Where(s => s.Id == sourceId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ImportSessionId, (long?)null)
                .SetProperty(s => s.CommittedFromSessionId, sessionId), ct);
        await tx.CommitAsync(ct);
        return (kept, dropped, empty);
    }
}
