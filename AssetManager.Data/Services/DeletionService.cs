using AssetManager.Core.Search;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>
/// Removes assets or whole sources from the catalog only. Foreign-key cascades remove files, stored copies,
/// previews, embeddings and tag links; a trigger removes FTS rows; the vector index is updated explicitly.
/// Original files on disk are never touched.
/// </summary>
public sealed class DeletionService(CatalogStore catalog, IVectorIndex? vectors = null)
{
    /// <summary>Number of assets that deleting these sources (and their nested sources) would remove.</summary>
    public async Task<int> CountSourceAssetsAsync(IReadOnlyCollection<long> sourceIds, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var all = await WithNestedAsync(db, sourceIds, ct);
        return await db.AllAssets.CountAsync(a => all.Contains(a.SourceId), ct);
    }

    public async Task<int> DeleteAssetsAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return 0;
        await using var db = catalog.CreateDbContext();
        var removed = 0;
        foreach (var chunk in assetIds.Chunk(500))
            removed += await db.AllAssets.Where(a => chunk.Contains(a.Id)).ExecuteDeleteAsync(ct);
        await FinishAsync(db, assetIds, ct);
        return removed;
    }

    /// <summary>Deletes sources, the sources nested in them, and all their assets.</summary>
    public async Task<int> DeleteSourcesAsync(IReadOnlyCollection<long> sourceIds, CancellationToken ct = default)
    {
        if (sourceIds.Count == 0) return 0;
        await using var db = catalog.CreateDbContext();
        var all = await WithNestedAsync(db, sourceIds, ct);
        var assetIds = await db.AllAssets.Where(a => all.Contains(a.SourceId)).Select(a => a.Id).ToListAsync(ct);
        var removed = 0;
        foreach (var chunk in assetIds.Chunk(500))
            removed += await db.AllAssets.Where(a => chunk.Contains(a.Id)).ExecuteDeleteAsync(ct);
        await db.AllSources.Where(s => all.Contains(s.Id)).ExecuteDeleteAsync(ct);
        await FinishAsync(db, assetIds, ct);
        return removed;
    }

    private async Task FinishAsync(CatalogDbContext db, IReadOnlyCollection<long> assetIds, CancellationToken ct)
    {
        if (vectors is not null) await vectors.RemoveAsync(assetIds, ct);
        await TagService.DeleteUnusedAsync(db, ct);
        // Return the freed pages (previews, stored chunks) to the OS.
        await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", ct);
        catalog.NotifyContentChanged();
    }

    private static async Task<List<long>> WithNestedAsync(CatalogDbContext db, IReadOnlyCollection<long> roots, CancellationToken ct)
    {
        var all = new HashSet<long>(roots);
        var frontier = roots.ToList();
        while (frontier.Count > 0)
        {
            var children = await db.AllSources
                .Where(s => s.ParentSourceId != null && frontier.Contains(s.ParentSourceId.Value))
                .Select(s => s.Id).ToListAsync(ct);
            frontier = children.Where(all.Add).ToList();
        }
        return all.ToList();
    }
}
