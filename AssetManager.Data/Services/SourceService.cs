using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>A source with its provenance and nesting, as the Sources page shows it.</summary>
/// <param name="Chain">The sources it is nested in, outermost first, ending with the source itself.</param>
/// <param name="Children">Sources nested directly in it, with their asset counts (nested ones included).</param>
public sealed record SourceDetails(Source Source, IReadOnlyList<Source> Chain, IReadOnlyList<(Source Source, int Assets)> Children, int OwnAssets);

/// <summary>Reads committed sources for the Sources page; edits go through <see cref="MetadataEditService"/>.</summary>
public sealed class SourceService(CatalogStore catalog)
{
    public async Task<SourceDetails?> LoadAsync(long sourceId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null) return null;

        var chain = new List<Source> { source };
        for (var parentId = source.ParentSourceId; parentId is not null;)
        {
            var parent = await db.Sources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == parentId, ct);
            if (parent is null || chain.Any(s => s.Id == parent.Id)) break;
            chain.Insert(0, parent);
            parentId = parent.ParentSourceId;
        }

        var children = await db.Sources.AsNoTracking().Where(s => s.ParentSourceId == sourceId).OrderBy(s => s.DisplayName).ToListAsync(ct);
        var own = await db.Assets.CountAsync(a => a.SourceId == sourceId, ct);
        var counted = new List<(Source, int)>();
        foreach (var child in children)
            counted.Add((child, await CountWithNestedAsync(db, child.Id, ct)));
        return new SourceDetails(source, chain, counted, own);
    }

    private static async Task<int> CountWithNestedAsync(CatalogDbContext db, long sourceId, CancellationToken ct)
    {
        var ids = new HashSet<long> { sourceId };
        var frontier = new List<long> { sourceId };
        while (frontier.Count > 0)
        {
            var next = await db.Sources.Where(s => s.ParentSourceId != null && frontier.Contains(s.ParentSourceId.Value)).Select(s => s.Id).ToListAsync(ct);
            frontier = next.Where(ids.Add).ToList();
        }
        return await db.Assets.CountAsync(a => ids.Contains(a.SourceId), ct);
    }
}
