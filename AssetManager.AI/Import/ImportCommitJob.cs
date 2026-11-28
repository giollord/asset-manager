using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Import;

/// <summary>
/// A commit queued on its own ("import.commit"). Imports now commit inside the import job (see
/// <see cref="ImportCommitter"/>); this handler stays so that commit jobs an earlier version queued still run. If the
/// import job is no longer running while assets still await AI, it is queued again; it skips its finished stages.
/// </summary>
public sealed class ImportCommitJob(CatalogStore catalog, IJobQueue jobs, ImportCommitter committer) : IJobHandler
{
    public const string JobType = "import.commit";

    public string Type => JobType;

    public JobPool Pool => JobPool.Cpu;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var sessionId = context.Payload<ImportPayload>().SessionId;
        var outcome = await committer.CommitAsync(sessionId, context.Progress, ImportJob.CommitStage, ImportJob.StageCount, ct);
        int pendingAi = 0;
        if (outcome.Committed)
            await using (var db = catalog.CreateDbContext())
            {
                var session = await db.ImportSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId, ct);
                pendingAi = session.AiDepth == AiDepth.None ? 0
                    : await db.Assets.CountAsync(a => a.Source.CommittedFromSessionId == sessionId && a.AiStatus == AiStatus.Pending, ct);
                if (pendingAi > 0 && session.JobId is { } j && !await db.Jobs.AnyAsync(x => x.Id == j && (x.State == JobState.Pending || x.State == JobState.Running), ct))
                {
                    var title = await db.Jobs.Where(x => x.Id == j).Select(x => x.Title).FirstOrDefaultAsync(ct) ?? "Import";
                    var resumed = await jobs.EnqueueAsync(ImportJob.JobType, $"AI enrichment: {title}", new ImportPayload(sessionId), j, ct);
                    await db.ImportSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(s => s.JobId, resumed), ct);
                }
            }
        context.Progress.SetSummary(new { assets = outcome.Assets, duplicates = outcome.Duplicates, replaced = outcome.Replaced, pendingAi, emptySources = outcome.EmptySources });
    }
}
