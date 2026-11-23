using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Enrichment;

/// <summary>
/// Bulk "Re-enrich": marks the assets pending and queues the AI item pass for them. It refuses to start without a usable
/// model, since the pass would otherwise turn the pending assets "not enriched" and lose their current AI status.
/// </summary>
public sealed class ReenrichService(CatalogStore catalog, IJobQueue jobs, AiRuntime runtime)
{
    public const string Unavailable = "No AI model is available. Install or start a model in Settings to re-enrich assets.";

    /// <summary>A chat model is configured and did not fail to start (it may still be loading).</summary>
    public bool Available => runtime.ChatConfigured && !runtime.ChatFailed;

    public async Task<long> StartAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) throw new ArgumentException("No assets selected.", nameof(assetIds));
        if (!Available) throw new InvalidOperationException(Unavailable);
        await using (var db = catalog.CreateDbContext())
            foreach (var chunk in assetIds.Chunk(500))
                await db.Assets.Where(a => chunk.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Pending), ct);
        catalog.NotifyContentChanged();
        var title = assetIds.Count == 1 ? "AI enrichment of 1 asset" : $"AI enrichment of {assetIds.Count} assets";
        return await jobs.EnqueueAsync(JobTypes.Enrich, title, new EnrichPayload([.. assetIds]), ct: ct);
    }
}
