using System.Text.Json;
using System.Text.RegularExpressions;
using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

public sealed record TagUsage(long Id, string Name, int Count);

/// <summary>Tag identity, normalization and bulk tagging (spec: Tags).</summary>
public sealed partial class TagService(CatalogStore catalog)
{
    /// <summary>Lower-case, trimmed, single-spaced: " Sci-Fi  Props " → "sci-fi props".</summary>
    public static string Normalize(string tag) => Spaces().Replace(tag.Trim().ToLowerInvariant(), " ");

    public static IReadOnlyList<string> NormalizeAll(IEnumerable<string> tags) =>
        tags.Select(Normalize).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>Returns tags for the names, creating missing ones (case-insensitive identity).</summary>
    public static async Task<List<Tag>> GetOrCreateAsync(CatalogDbContext db, IEnumerable<string> names, CancellationToken ct = default)
    {
        var normalized = NormalizeAll(names);
        if (normalized.Count == 0) return [];
        var existing = await db.Tags.Where(t => normalized.Contains(t.Name)).ToListAsync(ct);
        var tracked = db.ChangeTracker.Entries<Tag>().Select(e => e.Entity).Where(t => t.Id == 0);
        var result = new List<Tag>();
        foreach (var n in normalized)
        {
            var tag = existing.FirstOrDefault(t => t.Name == n) ?? tracked.FirstOrDefault(t => t.Name == n);
            if (tag is null) db.Tags.Add(tag = new Tag { Name = n });
            result.Add(tag);
        }
        return result;
    }

    /// <summary>
    /// Adds tags to many assets. Assets that already carry a tag keep it unchanged (including its origin).
    /// Uses set-based SQL so selections of any size are one round trip per tag.
    /// </summary>
    public async Task AddTagsAsync(IReadOnlyCollection<long> assetIds, IEnumerable<string> tags, TagOrigin origin, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;
        await using var db = catalog.CreateDbContext();
        var tagRows = await GetOrCreateAsync(db, tags, ct);
        await db.SaveChangesAsync(ct);
        var idsJson = JsonSerializer.Serialize(assetIds);
        foreach (var tag in tagRows)
            await db.Database.ExecuteSqlRawAsync(
                "INSERT OR IGNORE INTO AssetTags (AssetId, TagId, Origin) SELECT value, {0}, {1} FROM json_each({2})",
                [tag.Id, origin.ToString(), idsJson], ct);
        await SearchIndexer.RefreshAsync(db, assetIds, ct);
        await EmbeddingStaleness.MarkAsync(db, assetIds, ct);
        catalog.NotifyContentChanged();
    }

    public async Task RemoveTagsAsync(IReadOnlyCollection<long> assetIds, IEnumerable<string> tags, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return;
        var names = NormalizeAll(tags);
        await using var db = catalog.CreateDbContext();
        await db.AssetTags.Where(t => assetIds.Contains(t.AssetId) && names.Contains(t.Tag.Name)).ExecuteDeleteAsync(ct);
        await SearchIndexer.RefreshAsync(db, assetIds, ct);
        await EmbeddingStaleness.MarkAsync(db, assetIds, ct);
        await DeleteUnusedAsync(db, ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Replaces an asset's tags of one origin (e.g. AI tags after re-enrichment); tags from other origins stay.</summary>
    public static async Task ReplaceOriginTagsAsync(CatalogDbContext db, Asset asset, IEnumerable<string> tags, TagOrigin origin, CancellationToken ct = default)
    {
        var wanted = await GetOrCreateAsync(db, tags, ct);
        asset.Tags.RemoveAll(t => t.Origin == origin && !wanted.Any(w => ReferenceEquals(w, t.Tag) || (w.Id != 0 && w.Id == t.TagId)));
        foreach (var w in wanted)
            if (!asset.Tags.Any(t => ReferenceEquals(t.Tag, w) || (w.Id != 0 && t.TagId == w.Id)))
                asset.Tags.Add(new AssetTag { Tag = w, Origin = origin });
    }

    /// <summary>Tags with their committed-asset usage count, most used first.</summary>
    public async Task<List<TagUsage>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Tags
            .Select(t => new { t.Id, t.Name, Count = t.Assets.Count(a => a.Asset.ImportSessionId == null) })
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name)
            .Select(t => new TagUsage(t.Id, t.Name, t.Count))
            .ToListAsync(ct);
    }

    /// <summary>The tags carried by any of the assets, with how many of them carry each, most common first (bulk untag).</summary>
    public async Task<List<TagUsage>> TagsOfAsync(IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return [];
        await using var db = catalog.CreateDbContext();
        var counts = new Dictionary<(long Id, string Name), int>();
        foreach (var chunk in assetIds.Chunk(500))
            foreach (var row in await db.AssetTags.Where(t => chunk.Contains(t.AssetId)).GroupBy(t => new { t.TagId, t.Tag.Name })
                         .Select(g => new { g.Key.TagId, g.Key.Name, Count = g.Count() }).ToListAsync(ct))
                counts[(row.TagId, row.Name)] = counts.GetValueOrDefault((row.TagId, row.Name)) + row.Count;
        return counts.Select(kv => new TagUsage(kv.Key.Id, kv.Key.Name, kv.Value))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name).ToList();
    }

    /// <summary>Deletes tags no asset (committed or staged) uses.</summary>
    public static Task<int> DeleteUnusedAsync(CatalogDbContext db, CancellationToken ct = default) =>
        db.Tags.Where(t => !t.Assets.Any()).ExecuteDeleteAsync(ct);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
