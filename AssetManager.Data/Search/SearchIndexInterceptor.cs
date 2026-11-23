using System.Runtime.CompilerServices;
using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AssetManager.Data.Search;

/// <summary>
/// Keeps <c>asset_fts</c> current for tracked changes: after SaveChanges it re-indexes every asset whose
/// row, files or tags changed, and every asset of a renamed source. Deletions are handled by a SQL trigger.
/// Bulk SQL (ExecuteUpdate/ExecuteDelete) must call <see cref="SearchIndexer.RefreshAsync"/> itself.
/// </summary>
internal sealed class SearchIndexInterceptor : SaveChangesInterceptor
{
    private sealed class Pending
    {
        public readonly List<EntityEntry> Entries = [];
        public readonly HashSet<long> AssetIds = [];
        public readonly HashSet<long> SourceIds = [];
    }

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
        var p = new Pending();
        foreach (var e in ctx.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Unchanged or EntityState.Detached) continue;
            switch (e.Entity)
            {
                case Asset when e.State != EntityState.Deleted:
                    p.Entries.Add(e);
                    break;
                case AssetFile f:
                    if (f.AssetId != 0) p.AssetIds.Add(f.AssetId); else p.Entries.Add(e);
                    break;
                case AssetTag t:
                    if (t.AssetId != 0) p.AssetIds.Add(t.AssetId); else p.Entries.Add(e);
                    break;
                case Source s when e.State == EntityState.Modified && e.Property(nameof(Source.DisplayName)).IsModified:
                    p.SourceIds.Add(s.Id);
                    break;
            }
        }
        if (p.Entries.Count + p.AssetIds.Count + p.SourceIds.Count > 0) _pending.AddOrUpdate(ctx, p);
    }

    private async Task FlushAsync(DbContext? ctx, CancellationToken ct)
    {
        if (ctx is not CatalogDbContext db || !_pending.TryGetValue(ctx, out var p)) return;
        _pending.Remove(ctx);
        foreach (var e in p.Entries)
        {
            switch (e.Entity)
            {
                case Asset a: p.AssetIds.Add(a.Id); break;
                case AssetFile f: p.AssetIds.Add(f.AssetId); break;
                case AssetTag t: p.AssetIds.Add(t.AssetId); break;
            }
        }
        if (p.SourceIds.Count > 0)
        {
            var ids = await db.AllAssets.Where(a => p.SourceIds.Contains(a.SourceId)).Select(a => a.Id).ToListAsync(ct);
            p.AssetIds.UnionWith(ids);
        }
        p.AssetIds.Remove(0);
        if (p.AssetIds.Count > 0) await SearchIndexer.RefreshAsync(db, p.AssetIds, ct);
    }
}
