using AssetManager.AI.Runtime;
using AssetManager.Core;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.AI.Import;

public sealed record ImportPayload(long SessionId);

/// <summary>
/// The import job (spec "Multi-stage import progress", "Imports commit without confirmation"): scan, grouping,
/// metadata and previews, commit, AI pack pass and AI item pass. The commit needs no confirmation: once previews are
/// done the assets are in the catalog, and the AI stages enrich them there.
/// </summary>
public sealed class ImportJob(CatalogStore catalog, ImportStager stager, ImportEnricher enricher, ImportCommitter committer, ILogger<ImportJob>? logger = null) : IJobHandler
{
    public const string JobType = "import";
    public const int StageCount = 6;
    public const int CommitStage = 3, PackStage = 4, ItemStage = 5;
    public static readonly string[] StageNames = ["Scanning", "Grouping files", "Generating previews", "Commit", "AI pack pass", "AI item pass"];

    public string Type => JobType;

    public JobPool Pool => JobPool.Cpu;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Payload<ImportPayload>().SessionId;
        var p = context.Progress;
        if (await StateAsync(id, ct) is ImportSessionState.Cancelled) return;

        if (await StateAsync(id, ct) is ImportSessionState.Scanning)
        {
            await stager.ScanAndGroupAsync(id, p, StageCount, ct);
            await stager.ProcessAsync(id, p, StageCount, ct);
        }
        var commit = await committer.CommitAsync(id, p, CommitStage, StageCount, ct);
        if (!commit.Committed) return; // cancelled before the commit: the cancel removes what was staged

        using (var timings = AiTimings.Collect())
        {
            await enricher.PackPassAsync(id, p, PackStage, StageCount, ct);
            await enricher.ItemPassAsync(id, p, ItemStage, StageCount, ct);
            if (timings.Totals.Requests > 0) (logger ?? (ILogger)NullLogger.Instance).LogInformation("Import {Id} AI: {Timings}", id, timings.Totals);
        }

        await using (var db = catalog.CreateDbContext())
        {
            var assets = await db.Assets.CountAsync(a => a.Source.CommittedFromSessionId == id, ct);
            var skipped = await db.StagedSkips.CountAsync(s => s.SessionId == id, ct);
            var enriched = await db.Assets.CountAsync(a => a.Source.CommittedFromSessionId == id && a.AiStatus == AiStatus.Enriched, ct);
            p.SetSummary(new { assets, enriched, skipped, replaced = commit.Replaced });
            // Results dismissed while the AI ran kept the files the pack pass reads; it has read them now.
            if (await db.ImportSessions.AnyAsync(s => s.Id == id && s.ResultsDismissed, ct))
                await db.StagedSkips.Where(s => s.SessionId == id).ExecuteDeleteAsync(ct);
        }
    }

    private async Task<ImportSessionState> StateAsync(long id, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        return await db.ImportSessions.Where(s => s.Id == id).Select(s => s.State).FirstAsync(ct);
    }
}

/// <summary>AI enrichment of selected assets ("ai.enrich"): re-enrichment after a re-import and AI corrections.</summary>
public sealed class EnrichAssetsJob(ImportEnricher enricher, ILogger<EnrichAssetsJob>? logger = null) : IJobHandler
{
    public string Type => JobTypes.Enrich;

    public JobPool Pool => JobPool.Ai;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<EnrichPayload>();
        var ids = payload.AssetIds.Where(id => context.Includes(id.ToString())).ToList();
        using (var timings = AiTimings.Collect())
        {
            await enricher.EnrichSelectionAsync(ids, payload.Instruction, payload.Correction, context.Progress, ct);
            if (timings.Totals.Requests > 0) (logger ?? (ILogger)NullLogger.Instance).LogInformation("AI enrichment of {Count} assets: {Timings}", ids.Count, timings.Totals);
        }
        context.Progress.SetSummary(new { assets = ids.Count });
    }
}

public sealed record SourcesPayload(long[] SourceIds);

/// <summary>"Suggest source tags" (<see cref="JobType"/>): the AI tag consensus step for chosen sources (spec "When consensus runs").</summary>
public sealed class ConsensusJob(ImportEnricher enricher) : IJobHandler
{
    public const string JobType = "ai.consensus";

    public string Type => JobType;

    public JobPool Pool => JobPool.Ai;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var ids = context.Payload<SourcesPayload>().SourceIds.Where(id => context.Includes($"source:{id}")).ToList();
        await enricher.ConsensusForSourcesAsync(ids, context.Progress, ct);
        context.Progress.SetSummary(new { sources = ids.Count });
    }
}

/// <summary>Starts, resumes, cancels and dismisses imports; what the import wizard calls.</summary>
public sealed class ImportService(CatalogStore catalog, ImportStager stager, IJobQueue jobs)
{
    /// <summary>Starts an import; the results of earlier imports are dismissed (spec "Import results").</summary>
    public async Task<ImportSession> StartAsync(ImportRequest request, CancellationToken ct = default)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("Nothing to import.", nameof(request));
        var session = await stager.CreateSessionAsync(request, ct);
        await using (var db = catalog.CreateDbContext())
        {
            var earlier = await db.ImportSessions.Where(s => s.Id != session.Id && s.State == ImportSessionState.Committed && !s.ResultsDismissed)
                .Select(s => s.Id).ToListAsync(ct);
            foreach (var id in earlier) await DismissAsync(id, ct);
        }
        var title = request.Inputs.Count == 1 ? $"Import {Path.GetFileName(request.Inputs[0].TrimEnd('\\', '/'))}" : $"Import {request.Inputs.Count} items";
        session.JobId = await jobs.EnqueueAsync(ImportJob.JobType, title, new ImportPayload(session.Id), ct: ct);
        await using var db2 = catalog.CreateDbContext();
        await db2.ImportSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, session.JobId), ct);
        return session;
    }

    /// <summary>
    /// Runs the import job again for a session whose job failed or was interrupted; it continues after the last stage
    /// that finished (scan, grouping, previews, commit, AI passes).
    /// </summary>
    /// <returns>The new job's id.</returns>
    public async Task<long> ResumeAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var session = await db.ImportSessions.FirstAsync(s => s.Id == sessionId, ct);
        if (session.State == ImportSessionState.Cancelled) throw new InvalidOperationException("The import was cancelled.");
        if (session.AiDepth == AiDepth.Full)
            // Items left without AI (model missing, a failed request) get another try.
            await db.AllAssets.Where(a => (a.ImportSessionId == sessionId || a.Source.CommittedFromSessionId == sessionId)
                                          && (a.AiStatus == AiStatus.NotEnriched || a.AiStatus == AiStatus.Failed))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Pending), ct);
        var title = session.Inputs.Count == 1 ? $"Import {Path.GetFileName(session.Inputs[0].TrimEnd('\\', '/'))}" : $"Import {session.Inputs.Count} items";
        var jobId = await jobs.EnqueueAsync(ImportJob.JobType, title + " (resumed)", new ImportPayload(sessionId), ct: ct);
        await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, jobId), ct);
        return jobId;
    }

    /// <summary>
    /// Cancels the import (spec "Cancelling an import"). Before the commit it stops the job and removes everything it
    /// staged, so nothing reaches the catalog. After the commit it only stops the remaining AI work: the assets stay.
    /// </summary>
    /// <returns>True when the import was discarded; false when it was already committed and only its AI stopped.</returns>
    public async Task<bool> CancelAsync(long sessionId, CancellationToken ct = default)
    {
        long? jobId;
        bool discard;
        await using (var db = catalog.CreateDbContext())
        {
            jobId = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.JobId).FirstAsync(ct);
            // Wins the race against the commit: whichever changes the state first decides.
            discard = await db.ImportSessions.Where(s => s.Id == sessionId && (s.State == ImportSessionState.Scanning || s.State == ImportSessionState.Ready))
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Cancelled), ct) > 0;
        }
        if (jobId is { } j)
        {
            await jobs.CancelAsync(j, ct);
            try { await jobs.WaitAsync(j, ct).WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (TimeoutException) { }
        }
        if (discard) await stager.CancelSessionAsync(sessionId, ct);
        catalog.NotifyContentChanged();
        return discard;
    }

    /// <summary>Hides the import results (spec "Import results") and drops the skipped-items list they showed.</summary>
    public async Task DismissAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ResultsDismissed, true), ct);
        // A committed import whose AI is still running keeps the noted files and documents the pack pass reads.
        var running = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.StagedThrough).FirstAsync(ct) < ImportEnricher.PackPassDone
                      && await db.ImportSessions.AnyAsync(s => s.Id == sessionId && s.AiDepth != AiDepth.None, ct);
        await db.StagedSkips.Where(s => s.SessionId == sessionId && !(running && (s.Kind == SkipKind.Noted || s.Kind == SkipKind.Document)))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// What the Import page shows again (also after a restart): imports still running or committing, then the newest
    /// committed import whose results were not dismissed.
    /// </summary>
    public async Task<List<ImportSession>> OpenSessionsAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var running = await db.ImportSessions.AsNoTracking()
            .Where(s => s.State == ImportSessionState.Scanning || s.State == ImportSessionState.Ready || s.State == ImportSessionState.Committing)
            .OrderByDescending(s => s.Id).ToListAsync(ct);
        var results = await db.ImportSessions.AsNoTracking()
            .Where(s => s.State == ImportSessionState.Committed && !s.ResultsDismissed).OrderByDescending(s => s.Id).Take(1).ToListAsync(ct);
        return [.. running, .. results];
    }

    /// <summary>
    /// Sessions an earlier version left waiting for confirmation ("ready for review", no job running) are committed:
    /// their import job is queued again, and it commits them and runs the AI stages that are left (migration of the
    /// change "import without confirmation").
    /// </summary>
    /// <returns>The number of sessions queued.</returns>
    public async Task<int> CommitWaitingAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var active = db.Jobs.Where(j => j.State == JobState.Pending || j.State == JobState.Running).Select(j => (long?)j.Id);
        var waiting = await db.ImportSessions.AsNoTracking()
            .Where(s => s.State == ImportSessionState.Ready && !active.Contains(s.JobId)).ToListAsync(ct);
        foreach (var session in waiting)
        {
            await db.ImportSessions.Where(s => s.Id == session.Id && s.State == ImportSessionState.Ready)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Committing), ct);
            var title = session.Inputs.Count == 1 ? $"Import {Path.GetFileName(session.Inputs[0].TrimEnd('\\', '/'))}" : $"Import {session.Inputs.Count} items";
            var jobId = await jobs.EnqueueAsync(ImportJob.JobType, title, new ImportPayload(session.Id), ct: ct);
            await db.ImportSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, jobId), ct);
        }
        return waiting.Count;
    }

    /// <summary>Imports still running (scan, previews or commit); a new import waits until they are done.</summary>
    public async Task<bool> AnyRunningAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.ImportSessions.AnyAsync(s => s.State == ImportSessionState.Scanning || s.State == ImportSessionState.Ready || s.State == ImportSessionState.Committing, ct);
    }
}

/// <summary>Commits the sessions an earlier version left waiting for confirmation, once the job queue runs.</summary>
internal sealed class ImportRecoveryStartupTask(ImportService imports, ILogger<ImportRecoveryStartupTask>? logger = null) : IStartupTask
{
    public int Order => 16; // after the job queue starts (15)

    public async Task RunAsync(CancellationToken ct)
    {
        var n = await imports.CommitWaitingAsync(ct);
        if (n > 0) (logger ?? (ILogger)NullLogger.Instance).LogInformation("Committing {Count} imports that were waiting for confirmation", n);
    }
}
