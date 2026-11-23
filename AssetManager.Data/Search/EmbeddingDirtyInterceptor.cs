using System.Runtime.CompilerServices;
using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AssetManager.Data.Search;

/// <summary>
/// Marks an asset's embedding stale when what it is computed from changes: name, description or type (set on the
/// tracked row before saving), its tags (added/removed links) or the name of one of its tags (bulk update after
/// saving). The embedding refresh picks up dirty assets.
/// </summary>
internal sealed class EmbeddingDirtyInterceptor : SaveChangesInterceptor
{
    private static readonly string[] TextProperties = [nameof(Asset.Name), nameof(Asset.Description), nameof(Asset.Type)];

    private sealed record Pending(HashSet<long> AssetIds, HashSet<long> RenamedTagIds);

    private readonly ConditionalWeakTable<DbContext, Pending> _pending = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
    {
        await FlushAsync(eventData.Context, ct);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        FlushAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private void Collect(DbContext? ctx)
    {
        if (ctx is null) return;
        var p = new Pending([], []);
        var tracked = new Dictionary<long, Asset>();
        foreach (var e in ctx.ChangeTracker.Entries())
        {
            switch (e.Entity)
            {
                case Asset a when e.State == EntityState.Modified:
                    tracked[a.Id] = a;
                    if (TextProperties.Any(n => e.Property(n).IsModified)) a.EmbeddingDirty = true;
                    break;
                case Asset a when e.State == EntityState.Unchanged:
                    tracked[a.Id] = a;
                    break;
                case AssetTag t when e.State is EntityState.Added or EntityState.Deleted && t.AssetId != 0:
                    p.AssetIds.Add(t.AssetId);
                    break;
                case Tag t when e.State == EntityState.Modified && e.Property(nameof(Tag.Name)).IsModified:
                    p.RenamedTagIds.Add(t.Id);
                    break;
            }
        }
        // Tracked assets are flagged in the same save; the rest are updated in bulk afterwards.
        foreach (var id in p.AssetIds.ToList())
        {
            if (!tracked.TryGetValue(id, out var asset)) continue;
            asset.EmbeddingDirty = true;
            p.AssetIds.Remove(id);
        }
        if (p.AssetIds.Count + p.RenamedTagIds.Count > 0) _pending.AddOrUpdate(ctx, p);
    }

    private async Task FlushAsync(DbContext? ctx, CancellationToken ct)
    {
        if (ctx is not CatalogDbContext db || !_pending.TryGetValue(ctx, out var p)) return;
        _pending.Remove(ctx);
        if (p.AssetIds.Count > 0) await EmbeddingStaleness.MarkAsync(db, p.AssetIds, ct);
        if (p.RenamedTagIds.Count > 0)
            await db.AllAssets.Where(a => !a.EmbeddingDirty && db.AssetTags.Any(t => t.AssetId == a.Id && p.RenamedTagIds.Contains(t.TagId)))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmbeddingDirty, true), ct);
    }
}

/// <summary>For bulk SQL that bypasses change tracking (like <see cref="SearchIndexer.RefreshAsync"/> for search).</summary>
public static class EmbeddingStaleness
{
    public static Task<int> MarkAsync(CatalogDbContext db, IReadOnlyCollection<long> assetIds, CancellationToken ct = default) =>
        db.AllAssets.Where(a => assetIds.Contains(a.Id) && !a.EmbeddingDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.EmbeddingDirty, true), ct);
}
