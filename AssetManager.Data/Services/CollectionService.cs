using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>Collections are tag rules; membership is always computed by query (spec: Tag-based collections).</summary>
public sealed class CollectionService(CatalogStore catalog)
{
    public async Task<List<Collection>> ListAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Collections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Collection?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>Creates or updates a collection; tag names are normalized.</summary>
    public async Task<Collection> SaveAsync(Collection collection, CancellationToken ct = default)
    {
        collection.IncludeTags = TagService.NormalizeAll(collection.IncludeTags).ToList();
        collection.ExcludeTags = TagService.NormalizeAll(collection.ExcludeTags).ToList();
        if (collection.IncludeTags.Count == 0) throw new ArgumentException("A collection needs at least one required tag.");
        await using var db = catalog.CreateDbContext();
        if (collection.Id == 0)
        {
            collection.CreatedAt = DateTime.UtcNow;
            db.Collections.Add(collection);
        }
        else
        {
            db.Collections.Update(collection);
        }
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
        return collection;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        await db.Collections.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Member asset ids of a collection (committed assets only).</summary>
    public async Task<List<long>> MemberIdsAsync(Collection c, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Assets.InCollection(c).Select(a => a.Id).ToListAsync(ct);
    }

    public async Task<int> CountAsync(Collection c, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Assets.InCollection(c).CountAsync(ct);
    }

    /// <summary>Collections the asset currently belongs to.</summary>
    public async Task<List<Collection>> ForAssetAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var tags = await db.AssetTags.Where(t => t.AssetId == assetId).Select(t => t.Tag.Name).ToListAsync(ct);
        var all = await db.Collections.AsNoTracking().ToListAsync(ct);
        return all.Where(c => c.IncludeTags.All(tags.Contains) && !c.ExcludeTags.Any(tags.Contains)).OrderBy(c => c.Name).ToList();
    }
}
