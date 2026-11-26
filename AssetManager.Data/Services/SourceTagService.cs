using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>A source tag as the Sources page shows it.</summary>
public sealed record SourceTagView(string Name, TagOrigin Origin);

/// <summary>
/// Source-level tags (spec "Source tags", design D1). A <see cref="SourceTag"/> is materialized on every committed asset of
/// its source as an <see cref="AssetTag"/> with <see cref="TagOrigin.Source"/>. <c>INSERT OR IGNORE</c> keeps an existing
/// row of another origin (the user's copy), removing a source tag deletes only <c>Source</c>-origin rows, and
/// <see cref="AssetTagOptOut"/>s keep the tag off single assets.
/// </summary>
public sealed class SourceTagService(CatalogStore catalog)
{
    /// <summary>Adds source tags and gives them to every asset of the source.</summary>
    /// <param name="origin"><see cref="TagOrigin.User"/> (Sources page) or <see cref="TagOrigin.AI"/> (consensus).</param>
    public async Task AddAsync(long sourceId, IEnumerable<string> tags, TagOrigin origin = TagOrigin.User, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        await AddAsync(db, sourceId, tags, origin, ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Adds source tags inside the caller's context (AI consensus) and applies them.</summary>
    public static async Task AddAsync(CatalogDbContext db, long sourceId, IEnumerable<string> tags, TagOrigin origin, CancellationToken ct = default)
    {
        var rows = await TagService.GetOrCreateAsync(db, tags, ct);
        if (rows.Count == 0) return;
        await db.SaveChangesAsync(ct);
        foreach (var tag in rows)
            await db.Database.ExecuteSqlRawAsync("INSERT OR IGNORE INTO SourceTags (SourceId, TagId, Origin) VALUES ({0}, {1}, {2})",
                [sourceId, tag.Id, origin.ToString()], ct);
        await ApplyAsync(db, sourceId, ct);
    }

    /// <summary>
    /// Removes source tags: their <c>Source</c>-origin rows go, while copies the assets got from the user, the import or the
    /// AI stay (spec "Remove source tag keeps user copy").
    /// </summary>
    public async Task RemoveAsync(long sourceId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        var names = TagService.NormalizeAll(tags);
        await using var db = catalog.CreateDbContext();
        var tagIds = await db.Tags.Where(t => names.Contains(t.Name)).Select(t => t.Id).ToListAsync(ct);
        if (tagIds.Count == 0) return;
        await db.SourceTags.Where(t => t.SourceId == sourceId && tagIds.Contains(t.TagId)).ExecuteDeleteAsync(ct);
        var affected = await db.AssetTags
            .Where(t => t.Origin == TagOrigin.Source && tagIds.Contains(t.TagId) && t.Asset.SourceId == sourceId)
            .Select(t => t.AssetId).Distinct().ToListAsync(ct);
        foreach (var chunk in affected.Chunk(500))
            await db.AssetTags.Where(t => t.Origin == TagOrigin.Source && tagIds.Contains(t.TagId) && chunk.Contains(t.AssetId)).ExecuteDeleteAsync(ct);
        await SearchIndexer.RefreshAsync(db, affected, ct);
        await EmbeddingStaleness.MarkAsync(db, affected, ct);
        await TagService.DeleteUnusedAsync(db, ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>The source tags of every source (Sources page).</summary>
    public async Task<Dictionary<long, List<SourceTagView>>> AllAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var rows = await db.SourceTags.AsNoTracking().OrderBy(t => t.Tag.Name)
            .Select(t => new { t.SourceId, t.Tag.Name, t.Origin }).ToListAsync(ct);
        return rows.GroupBy(r => r.SourceId).ToDictionary(g => g.Key, g => g.Select(r => new SourceTagView(r.Name, r.Origin)).ToList());
    }

    public async Task<List<SourceTagView>> OfAsync(long sourceId, CancellationToken ct = default) =>
        (await AllAsync(ct)).GetValueOrDefault(sourceId) ?? [];

    /// <summary>
    /// Gives every committed asset of the source the source's tags with origin <c>Source</c>, except where the asset already
    /// has the tag or opted out of it. Called after imports, re-imports, version updates, rescans and AI tag replacement.
    /// </summary>
    /// <returns>The assets that got a tag.</returns>
    public static async Task<List<long>> ApplyAsync(CatalogDbContext db, long sourceId, CancellationToken ct = default)
    {
        var missing = await db.Database.SqlQueryRaw<long>("""
            SELECT DISTINCT a.Id AS Value FROM Assets a
            JOIN SourceTags st ON st.SourceId = a.SourceId
            WHERE a.SourceId = {0} AND a.ImportSessionId IS NULL
              AND NOT EXISTS (SELECT 1 FROM AssetTags at WHERE at.AssetId = a.Id AND at.TagId = st.TagId)
              AND NOT EXISTS (SELECT 1 FROM AssetTagOptOuts o WHERE o.AssetId = a.Id AND o.TagId = st.TagId)
            """, sourceId).ToListAsync(ct);
        if (missing.Count == 0) return missing;
        await db.Database.ExecuteSqlRawAsync("""
            INSERT OR IGNORE INTO AssetTags (AssetId, TagId, Origin)
            SELECT a.Id, st.TagId, 'Source' FROM Assets a
            JOIN SourceTags st ON st.SourceId = a.SourceId
            WHERE a.SourceId = {0} AND a.ImportSessionId IS NULL
              AND NOT EXISTS (SELECT 1 FROM AssetTagOptOuts o WHERE o.AssetId = a.Id AND o.TagId = st.TagId)
            """, [sourceId], ct);
        await SearchIndexer.RefreshAsync(db, missing, ct);
        await EmbeddingStaleness.MarkAsync(db, missing, ct);
        return missing;
    }

    /// <summary><see cref="ApplyAsync(CatalogDbContext, long, CancellationToken)"/> for several sources.</summary>
    public async Task ApplyAsync(IEnumerable<long> sourceIds, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var changed = false;
        foreach (var id in sourceIds.Distinct())
            changed |= (await ApplyAsync(db, id, ct)).Count > 0;
        if (changed) catalog.NotifyContentChanged();
    }

    /// <summary>Applies the source tags of the sources the given assets belong to.</summary>
    public static async Task ApplyForAssetsAsync(CatalogDbContext db, IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        var sources = new HashSet<long>();
        foreach (var chunk in assetIds.Chunk(500))
            sources.UnionWith(await db.AllAssets.Where(a => chunk.Contains(a.Id)).Select(a => a.SourceId).Distinct().ToListAsync(ct));
        if (sources.Count == 0) return;
        var tagged = await db.SourceTags.Where(t => sources.Contains(t.SourceId)).Select(t => t.SourceId).Distinct().ToListAsync(ct);
        foreach (var id in tagged) await ApplyAsync(db, id, ct);
    }
}
