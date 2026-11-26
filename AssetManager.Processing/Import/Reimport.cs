using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Processing.Files;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Jobs;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Storage;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Import;

public sealed record ReimportPayload(long[] AssetIds, bool Reenrich);

/// <summary>Payload of the AI enrichment job ("ai.enrich"): re-enrichment and AI corrections on a selection.</summary>
public sealed record EnrichPayload(long[] AssetIds, string? Instruction = null, string? Correction = null);

/// <summary>
/// Re-imports selected assets (spec "Re-import selected assets"): re-reads the files (size and fast hash), then
/// regenerates metadata and previews. User-edited fields and user tags are untouched: handlers respect
/// <see cref="Asset.UserEdited"/> and only previews and technical metadata are rewritten. With re-enrichment the
/// assets become pending and an "ai.enrich" job follows.
/// </summary>
public sealed class ReimportJob(CatalogStore catalog, ILocationResolver resolver, PreviewService previews, PreviewSettings previewSettings, IJobQueue jobs)
    : IJobHandler
{
    public string Type => JobTypes.Reimport;

    public JobPool Pool => JobPool.Cpu;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<ReimportPayload>();
        var ids = payload.AssetIds.Where(id => context.Includes(id.ToString())).ToArray();
        var p = context.Progress;
        p.SetStage("Re-reading originals", 0, 1, ids.Length);
        var options = await previewSettings.GetAsync(ct);
        var processed = new List<long>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            if (await RefreshFilesAsync(id, ct) is { } missing)
            {
                p.Fail(id.ToString(), missing.Name, missing.Reason);
                p.Advance(1, missing.Name);
                continue;
            }
            var outcome = await previews.ProcessOneAsync(id, options, ct);
            if (outcome.Skipped is { } s) p.Fail(id.ToString(), s.Name, s.Reason);
            else
            {
                if (outcome.PreviewError is { } e) p.Warn(id.ToString(), outcome.Name, e);
                processed.Add(id);
            }
            p.Advance(1, outcome.Name);
        }

        await using (var db = catalog.CreateDbContext())
        {
            foreach (var chunk in processed.Chunk(500))
                await db.Assets.Where(a => chunk.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.EmbeddingDirty, true), ct);
            await AssetManager.Data.Services.SourceTagService.ApplyForAssetsAsync(db, processed, ct);
            if (payload.Reenrich && processed.Count > 0)
            {
                foreach (var chunk in processed.Chunk(500))
                    await db.Assets.Where(a => chunk.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Pending), ct);
                await jobs.EnqueueAsync(JobTypes.Enrich, $"AI enrichment of {processed.Count} re-imported assets", new EnrichPayload([.. processed]), context.Job.Id, ct);
            }
        }
        catalog.NotifyContentChanged();
        p.SetSummary(new { reimported = processed.Count, skipped = ids.Length - processed.Count, reenrich = payload.Reenrich });
    }

    /// <summary>Updates each file's size and fast hash from its current content.</summary>
    private async Task<SkippedAsset?> RefreshFilesAsync(long id, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.Assets.Include(a => a.Files).ThenInclude(f => f.Stored).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return new SkippedAsset(id, $"Asset {id}", "The asset no longer exists.");
        try
        {
            foreach (var file in asset.Files)
            {
                using var local = await resolver.ResolveAsync(file, ct);
                var size = new FileInfo(local.Path).Length;
                var fast = Fingerprint.ContentKey(local.Path);
                if (size == file.Size && fast == file.FastHash) continue;
                (file.Size, file.FastHash, file.ContentHash) = (size, fast, null);
            }
        }
        catch (OriginalUnavailableException e)
        {
            return new SkippedAsset(id, asset.Name, $"Original unavailable: {e.Missing}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SkippedAsset(id, asset.Name, e.Message);
        }
        await db.SaveChangesAsync(ct);
        return null;
    }
}

/// <summary>Starts re-imports; what the asset context menu and bulk actions call.</summary>
public sealed class ReimportService(IJobQueue jobs)
{
    public Task<long> StartAsync(IReadOnlyCollection<long> assetIds, bool reenrich, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) throw new ArgumentException("No assets selected.", nameof(assetIds));
        var title = assetIds.Count == 1 ? "Re-import 1 asset" : $"Re-import {assetIds.Count} assets";
        return jobs.EnqueueAsync(JobTypes.Reimport, title, new ReimportPayload([.. assetIds], reenrich), ct: ct);
    }
}
