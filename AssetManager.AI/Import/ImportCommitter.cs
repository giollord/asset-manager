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

/// <summary>The commit's totals, for the job summary.</summary>
public sealed record CommitOutcome(bool Committed, int Assets, int Duplicates, int Replaced, int EmptySources);

/// <summary>
/// Commits an import session (spec "Imports commit without confirmation"), run by the import job once previews are
/// done: each staged source commits in one transaction that drops its duplicates (listing them among the skipped
/// items) and clears <c>ImportSessionId</c> on the rest. Asset ids do not change, so the AI stages that follow work on
/// the committed assets (found through <see cref="Source.CommittedFromSessionId"/>). What each source got is kept in
/// <see cref="ImportSession.Results"/> for the import results. Running it again for the same session is harmless.
/// </summary>
public sealed class ImportCommitter(CatalogStore catalog, IJobQueue jobs, SourceVersions versions, ILogger<ImportCommitter>? logger = null)
{
    public const string AlreadyCataloged = "already cataloged";

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>
    /// Commits the session, unless it was cancelled first (then <see cref="CommitOutcome.Committed"/> is false). A
    /// cancel that arrives later only stops the AI work; see <see cref="ImportService.CancelAsync"/>.
    /// </summary>
    public async Task<CommitOutcome> CommitAsync(long sessionId, IJobProgress p, int stage, int stageCount, CancellationToken ct)
    {
        await using (var db = catalog.CreateDbContext())
        {
            // Wins the race against a cancel: whichever changes the state first decides.
            await db.ImportSessions.Where(s => s.Id == sessionId && (s.State == ImportSessionState.Scanning || s.State == ImportSessionState.Ready))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Committing), ct);
            var state = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.State).FirstAsync(ct);
            if (state == ImportSessionState.Cancelled) return new CommitOutcome(false, 0, 0, 0, 0);
            if (state == ImportSessionState.Committed)
            {
                p.SetStage(ImportJob.StageNames[stage], stage, stageCount, 0);
                return new CommitOutcome(true, 0, 0, 0, 0);
            }
        }

        List<Source> sources;
        long? into;
        List<ImportSourceResult> results;
        await using (var db = catalog.CreateDbContext())
        {
            sources = await db.AllSources.AsNoTracking().Where(s => s.ImportSessionId == sessionId).ToListAsync(ct);
            var session = await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
            into = session.IntoSourceId;
            results = session.Results ?? []; // a commit interrupted half-way keeps what it already recorded
        }
        p.SetStage(ImportJob.StageNames[stage], stage, stageCount, sources.Count);

        // Innermost sources first, so a container left without assets or children can be dropped.
        var byId = sources.ToDictionary(s => s.Id);
        int Depth(Source s) => s.ParentSourceId is { } parent && byId.TryGetValue(parent, out var ps) ? 1 + Depth(ps) : 0;
        int committedAssets = 0, duplicates = 0, emptySources = 0, replaced = 0;
        var restore = new List<long>();
        foreach (var source in sources.OrderByDescending(Depth).ThenBy(s => s.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (into is { } target && source.ParentSourceId is null && await MergeAsync(sessionId, source.Id, target, ct) is { } merged)
            {
                committedAssets += merged.Kept;
                duplicates += merged.Dropped;
                await AddResultAsync(results, target, merged.Kept, sessionId, ct);
                p.Advance(1, source.DisplayName);
                continue;
            }
            if (source.ReplacesSourceId is { } oldId && source.ReplaceAccepted == true && await ReplaceAsync(sessionId, source.Id, oldId, restore, ct) is { } diff)
            {
                replaced++;
                duplicates += diff.Dropped;
                int kept;
                await using (var db = catalog.CreateDbContext())
                    kept = await db.Assets.CountAsync(a => a.SourceId == oldId, ct);
                committedAssets += kept;
                await AddResultAsync(results, oldId, kept, sessionId, ct, diff.Diff);
                p.Advance(1, source.DisplayName);
                continue;
            }
            var (keptHere, dropped, empty) = await CommitSourceAsync(sessionId, source.Id, ct);
            committedAssets += keptHere;
            duplicates += dropped;
            if (empty) emptySources++;
            else await AddResultAsync(results, source.Id, keptHere, sessionId, ct);
            p.Advance(1, source.DisplayName);
        }

        await using (var db = catalog.CreateDbContext())
        {
            await TagService.DeleteUnusedAsync(db, ct);
            var session = await db.ImportSessions.FirstAsync(s => s.Id == sessionId, ct);
            session.Results = results;
            session.State = ImportSessionState.Committed;
            await db.SaveChangesAsync(ct);
        }
        if (restore.Count > 0)
            await jobs.EnqueueAsync(JobTypes.StoreOriginals, $"Store originals of {restore.Count} updated assets", new AssetsPayload([.. restore]), ct: ct);
        catalog.NotifyContentChanged();
        _log.LogInformation("Import {Session} committed: {Assets} assets, {Duplicates} duplicates skipped, {Replaced} sources replaced",
            sessionId, committedAssets, duplicates, replaced);
        return new CommitOutcome(true, committedAssets, duplicates, replaced, emptySources);
    }

    /// <summary>Records what a source got; a source that appears again (several roots merged into it) adds up.</summary>
    private async Task AddResultAsync(List<ImportSourceResult> results, long sourceId, int assets, long sessionId, CancellationToken ct, VersionDiff? diff = null)
    {
        string name;
        await using (var db = catalog.CreateDbContext())
            name = await db.Sources.Where(s => s.Id == sourceId).Select(s => s.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var i = results.FindIndex(r => r.SourceId == sourceId);
        var result = diff is null
            ? new ImportSourceResult(sourceId, name, assets + (i >= 0 ? results[i].Assets : 0))
            : new ImportSourceResult(sourceId, name, assets, true, diff.Updated.Count, diff.Added.Count, diff.Removed.Count);
        if (i >= 0) results[i] = result with { Replaced = result.Replaced || results[i].Replaced };
        else results.Add(result);
        await using (var db = catalog.CreateDbContext())
            await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Results, results.ToList()), ct);
    }

    /// <summary>
    /// The duplicates of a staged source become skipped items ("already cataloged"), listed under
    /// <paramref name="ownerId"/> in the import results (null: "Not imported"), before the commit deletes them.
    /// </summary>
    private static async Task<int> SkipDuplicatesAsync(CatalogDbContext db, long sessionId, long stagedSourceId, long? ownerId, CancellationToken ct)
    {
        var rows = await db.AssetFiles.IgnoreQueryFilters()
            .Where(f => f.Role == FileRole.Primary && f.Asset.SourceId == stagedSourceId && f.Asset.ImportSessionId == sessionId && !f.Asset.Included)
            .Select(f => new { f.RelativePath, f.Location }).ToListAsync(ct);
        db.StagedSkips.AddRange(rows.Select(r => new StagedSkip
        {
            SessionId = sessionId, SourceId = ownerId, Path = r.RelativePath, Kind = SkipKind.Duplicate,
            Reason = AlreadyCataloged, Location = r.Location,
        }));
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Applies a new-version replacement in one transaction; null when the old source is gone.</summary>
    private async Task<(VersionDiff Diff, int Dropped)?> ReplaceAsync(long sessionId, long stagedId, long oldId, List<long> restore, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        if (!await db.Sources.AnyAsync(s => s.Id == oldId, ct)) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var diff = await SourceVersions.DiffAsync(db, stagedId, oldId, ct); // the staged source is gone after the replacement
        var dropped = await SkipDuplicatesAsync(db, sessionId, stagedId, oldId, ct);
        restore.AddRange(await versions.ReplaceAsync(db, sessionId, stagedId, oldId, ct));
        await SourceTagService.ApplyAsync(db, oldId, ct); // the new version's assets inherit the source tags
        await tx.CommitAsync(ct);
        return (diff, dropped);
    }

    /// <summary>
    /// Files imported into an existing source (spec "Acting on rescan results"): the staged root's assets and nested
    /// sources move to that source, which then gives the new assets its source tags. Null when the source is gone.
    /// </summary>
    private async Task<(int Kept, int Dropped)?> MergeAsync(long sessionId, long stagedId, long targetId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        if (!await db.Sources.AnyAsync(s => s.Id == targetId, ct)) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SkipDuplicatesAsync(db, sessionId, stagedId, targetId, ct);
        var dropped = await db.AllAssets.Where(a => a.SourceId == stagedId && a.ImportSessionId == sessionId && !a.Included).ExecuteDeleteAsync(ct);
        var kept = await db.AllAssets.Where(a => a.SourceId == stagedId && a.ImportSessionId == sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.SourceId, targetId).SetProperty(a => a.ImportSessionId, (long?)null), ct);
        await db.AllSources.Where(s => s.ParentSourceId == stagedId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentSourceId, targetId), ct);
        await db.StagedSkips.Where(s => s.SourceId == stagedId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceId, targetId), ct);
        await db.AllSources.Where(s => s.Id == stagedId).ExecuteDeleteAsync(ct);
        await SourceTagService.ApplyAsync(db, targetId, ct);
        await tx.CommitAsync(ct);
        _log.LogInformation("Import {Session}: {Kept} new assets added to source {Source}", sessionId, kept, targetId);
        return (kept, dropped);
    }

    /// <returns>Committed and dropped asset counts, and whether the source was dropped for being empty.</returns>
    private static async Task<(int Kept, int Dropped, bool Empty)> CommitSourceAsync(CatalogStore catalog, long sessionId, long sourceId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var dropped = await db.AllAssets.Where(a => a.SourceId == sourceId && a.ImportSessionId == sessionId && !a.Included).CountAsync(ct);
        var kept = await db.AllAssets.CountAsync(a => a.SourceId == sourceId && a.ImportSessionId == sessionId && a.Included, ct);
        var empty = kept == 0
            && !await db.AllAssets.AnyAsync(a => a.SourceId == sourceId && a.Included, ct)
            && !await db.AllSources.AnyAsync(s => s.ParentSourceId == sourceId, ct);
        // Duplicates are listed under the source, or, when it is dropped for being empty, under its parent or none.
        var owner = empty ? await db.AllSources.Where(s => s.Id == sourceId).Select(s => s.ParentSourceId).FirstOrDefaultAsync(ct) : sourceId;
        if (dropped > 0) await SkipDuplicatesAsync(db, sessionId, sourceId, owner, ct);
        await db.AllAssets.Where(a => a.SourceId == sourceId && a.ImportSessionId == sessionId && !a.Included).ExecuteDeleteAsync(ct);
        await db.AllAssets.Where(a => a.SourceId == sourceId && a.ImportSessionId == sessionId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ImportSessionId, (long?)null), ct);
        if (empty)
        {
            // Its skips move to the parent, so the results still list them.
            await db.StagedSkips.Where(s => s.SessionId == sessionId && s.SourceId == sourceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceId, owner), ct);
            await db.AllSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.AllSources.Where(s => s.Id == sourceId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ImportSessionId, (long?)null)
                .SetProperty(s => s.CommittedFromSessionId, sessionId), ct);
            await SourceTagService.ApplyAsync(db, sourceId, ct); // files imported into an existing source
        }
        await tx.CommitAsync(ct);
        return (kept, dropped, empty);
    }

    private Task<(int Kept, int Dropped, bool Empty)> CommitSourceAsync(long sessionId, long sourceId, CancellationToken ct) =>
        CommitSourceAsync(catalog, sessionId, sourceId, ct);
}
