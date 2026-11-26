using AssetManager.AI.Runtime;
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
/// The import job (design D8, spec "Multi-stage import progress"): scan, grouping, metadata and previews, AI pack
/// pass, AI item pass, and commit (stage 6, run by the confirm job). The session is ready for review after the
/// previews; the user may confirm while the AI stages still run.
/// </summary>
public sealed class ImportJob(CatalogStore catalog, ImportStager stager, ImportEnricher enricher, ILogger<ImportJob>? logger = null) : IJobHandler
{
    public const string JobType = "import";
    public const int StageCount = 6;
    public static readonly string[] StageNames = ["Scanning", "Grouping files", "Generating previews", "AI pack pass", "AI item pass", "Commit"];

    public string Type => JobType;

    public JobPool Pool => JobPool.Cpu;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Payload<ImportPayload>().SessionId;
        var p = context.Progress;
        if (await StateAsync(id, ct) is ImportSessionState.Cancelled) return;

        await stager.ScanAndGroupAsync(id, p, StageCount, ct);
        await stager.ProcessAsync(id, p, StageCount, ct);
        await using (var db = catalog.CreateDbContext())
            await db.ImportSessions.Where(s => s.Id == id && s.State == ImportSessionState.Scanning)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Ready), ct);
        catalog.NotifyContentChanged();

        if (await StateAsync(id, ct) is ImportSessionState.Cancelled) return;
        using (var timings = AiTimings.Collect())
        {
            await enricher.PackPassAsync(id, p, 3, StageCount, ct);
            await enricher.ItemPassAsync(id, p, 4, StageCount, ct);
            if (timings.Totals.Requests > 0) (logger ?? (ILogger)NullLogger.Instance).LogInformation("Import {Id} AI: {Timings}", id, timings.Totals);
        }

        await using (var db = catalog.CreateDbContext())
        {
            var assets = await db.AllAssets.CountAsync(a => a.ImportSessionId == id || a.Source.CommittedFromSessionId == id, ct);
            var skipped = await db.StagedSkips.CountAsync(s => s.SessionId == id, ct);
            var enriched = await db.AllAssets.CountAsync(a => (a.ImportSessionId == id || a.Source.CommittedFromSessionId == id) && a.AiStatus == AiStatus.Enriched, ct);
            if (await StateAsync(id, ct) is ImportSessionState.Committed)
                await db.StagedSkips.Where(s => s.SessionId == id).ExecuteDeleteAsync(ct); // kept for the pack pass after an early confirm
            p.SetSummary(new { assets, enriched, skipped });
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

/// <summary>Starts and cancels imports; what the import wizard calls.</summary>
public sealed class ImportService(CatalogStore catalog, ImportStager stager, IJobQueue jobs)
{
    public async Task<ImportSession> StartAsync(ImportRequest request, CancellationToken ct = default)
    {
        if (request.Inputs.Count == 0) throw new ArgumentException("Nothing to import.", nameof(request));
        var session = await stager.CreateSessionAsync(request, ct);
        var title = request.Inputs.Count == 1 ? $"Import {Path.GetFileName(request.Inputs[0].TrimEnd('\\', '/'))}" : $"Import {request.Inputs.Count} items";
        session.JobId = await jobs.EnqueueAsync(ImportJob.JobType, title, new ImportPayload(session.Id), ct: ct);
        await using var db = catalog.CreateDbContext();
        await db.ImportSessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, session.JobId), ct);
        return session;
    }

    /// <summary>
    /// Runs the import job again for a session whose job failed or was interrupted; it continues after the last stage
    /// that finished (scan, grouping, previews, AI passes). Works before and after confirm.
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
    /// Commits the session's included items (spec "Confirm import"). Allowed once previews are done; AI stages that
    /// are still running continue on the committed assets.
    /// </summary>
    /// <returns>The commit job's id.</returns>
    public async Task<long> ConfirmAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var changed = await db.ImportSessions.Where(s => s.Id == sessionId && s.State == ImportSessionState.Ready)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Committing), ct);
        if (changed == 0) throw new InvalidOperationException("The import is not ready to confirm yet (previews are still being generated) or was already confirmed.");
        return await jobs.EnqueueAsync(ImportCommitJob.JobType, "Commit import", new ImportPayload(sessionId), ct: ct);
    }

    /// <summary>Stops the import and removes everything it staged; nothing reaches the catalog.</summary>
    public async Task CancelAsync(long sessionId, CancellationToken ct = default)
    {
        long? jobId;
        await using (var db = catalog.CreateDbContext())
        {
            jobId = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.JobId).FirstAsync(ct);
            await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Cancelled), ct);
        }
        if (jobId is { } j)
        {
            await jobs.CancelAsync(j, ct);
            try { await jobs.WaitAsync(j, ct).WaitAsync(TimeSpan.FromSeconds(30), ct); } catch (TimeoutException) { }
        }
        await stager.CancelSessionAsync(sessionId, ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Import sessions not yet confirmed or cancelled (shown again after a restart).</summary>
    public async Task<List<ImportSession>> OpenSessionsAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.ImportSessions.AsNoTracking()
            .Where(s => s.State == ImportSessionState.Scanning || s.State == ImportSessionState.Ready || s.State == ImportSessionState.Committing)
            .OrderByDescending(s => s.Id).ToListAsync(ct);
    }
}
