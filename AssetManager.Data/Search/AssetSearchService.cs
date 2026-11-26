using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Search;

/// <summary>A result row: what a grid card needs.</summary>
public sealed record AssetSummary(
    long Id, string Name, AssetType Type, long SourceId, string SourceName, AiStatus AiStatus, bool IsStored,
    ScaleAnomaly ScaleAnomaly, bool OrientationUncertain, bool TexturesMissing, string? Description,
    bool Favorite = false, int Rating = 0);

public sealed record SearchPage(IReadOnlyList<AssetSummary> Items, int Total);

/// <summary>
/// Catalog search (spec "Combined filters", "Full-text search"): every filter of a <see cref="SearchQuery"/> is one
/// <c>Where</c> on the asset query, so the same translation serves Browse, collections and the dashboard counts.
/// Text goes through the FTS5 index (word prefixes, bm25 ranking).
/// </summary>
public sealed class AssetSearchService(CatalogStore catalog, IVolumeService? volumes = null)
{
    /// <summary>bm25 column weights: name, files, paths, description, tags, source, notes.</summary>
    private const string Bm25 = "bm25(asset_fts, 10.0, 4.0, 1.0, 2.0, 5.0, 1.0, 3.0)";

    private sealed class FtsHit
    {
        public long Id { get; set; }
        public double Rank { get; set; }
    }

    public async Task<SearchPage> SearchAsync(SearchQuery query, int skip, int take, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var filtered = await ApplyAsync(db, db.Assets.AsNoTracking(), query, ct);
        var match = SearchIndexer.BuildMatch(query.Text);

        IQueryable<Asset> ordered;
        if (match is not null)
        {
            var hits = db.Database.SqlQueryRaw<FtsHit>($"SELECT rowid AS Id, {Bm25} AS Rank FROM asset_fts WHERE asset_fts MATCH {{0}}", match);
            var joined = filtered.Join(hits, a => a.Id, h => h.Id, (a, h) => new { a, h.Rank });
            var total = await joined.CountAsync(ct);
            var page = query.Sort switch
            {
                SearchSort.Name => joined.OrderBy(x => x.a.Name).ThenBy(x => x.a.Id),
                SearchSort.Newest => joined.OrderByDescending(x => x.a.ImportedAt).ThenByDescending(x => x.a.Id),
                _ => joined.OrderBy(x => x.Rank).ThenBy(x => x.a.Id), // bm25: lower is better
            };
            return new SearchPage(await Project(page.Select(x => x.a).Skip(skip).Take(take)).ToListAsync(ct), total);
        }

        ordered = query.Sort == SearchSort.Name
            ? filtered.OrderBy(a => a.Name).ThenBy(a => a.Id)
            : filtered.OrderByDescending(a => a.ImportedAt).ThenByDescending(a => a.Id);
        var count = await filtered.CountAsync(ct);
        return new SearchPage(await Project(ordered.Skip(skip).Take(take)).ToListAsync(ct), count);
    }

    public async Task<int> CountAsync(SearchQuery query, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var filtered = await ApplyAsync(db, db.Assets.AsNoTracking(), query, ct);
        if (SearchIndexer.BuildMatch(query.Text) is { } match)
        {
            var hits = db.Database.SqlQueryRaw<FtsHit>($"SELECT rowid AS Id, 0.0 AS Rank FROM asset_fts WHERE asset_fts MATCH {{0}}", match);
            return await filtered.Join(hits, a => a.Id, h => h.Id, (a, _) => a.Id).CountAsync(ct);
        }
        return await filtered.CountAsync(ct);
    }

    /// <summary>
    /// The ids of all matches (unordered), text included: "Select all" in Browse, and filtering semantic hits (whose
    /// caller clears the text, since a semantic query's words rank instead of filter).
    /// </summary>
    public async Task<HashSet<long>> MatchingIdsAsync(SearchQuery query, IReadOnlyCollection<long>? among = null, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var filtered = await ApplyAsync(db, db.Assets.AsNoTracking(), query, ct);
        if (SearchIndexer.BuildMatch(query.Text) is { } match)
        {
            var hits = db.Database.SqlQueryRaw<FtsHit>($"SELECT rowid AS Id, 0.0 AS Rank FROM asset_fts WHERE asset_fts MATCH {{0}}", match);
            filtered = filtered.Join(hits, a => a.Id, h => h.Id, (a, _) => a);
        }
        if (among is not null)
        {
            var result = new HashSet<long>();
            foreach (var chunk in among.Chunk(500))
                result.UnionWith(await filtered.Where(a => chunk.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct));
            return result;
        }
        return (await filtered.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
    }

    public static IQueryable<AssetSummary> Project(IQueryable<Asset> q) => q.Select(a => new AssetSummary(
        a.Id, a.Name, a.Type, a.SourceId, a.Source.DisplayName, a.AiStatus, a.IsStored, a.ScaleAnomaly, a.OrientationUncertain,
        a.TexturesMissing, a.Description, a.Favorite, a.Rating));

    /// <summary>Applies every filter except the text (which needs the FTS join) to <paramref name="q"/>.</summary>
    public async Task<IQueryable<Asset>> ApplyAsync(CatalogDbContext db, IQueryable<Asset> q, SearchQuery query, CancellationToken ct = default)
    {
        if (query.Types.Count > 0)
        {
            var types = query.Types.ToList();
            q = q.Where(a => types.Contains(a.Type));
        }
        q = q.WithTagRule(query.IncludeTags, query.ExcludeTags);
        if (query.CollectionId is { } collectionId)
        {
            var collection = await db.Collections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == collectionId, ct);
            q = collection is null ? q.Where(_ => false) : q.InCollection(collection);
        }
        if (query.SourceId is { } sourceId)
        {
            var ids = await SourceTreeAsync(db, sourceId, ct);
            q = q.Where(a => ids.Contains(a.SourceId));
        }
        if (query.Commercial is { } commercial) q = q.WhereCommercial(commercial);
        if (query.Attribution is { } attribution) q = q.WhereAttribution(attribution);
        if (query.LicenseProblem is { } problem) q = q.WhereLicenseProblem(problem);
        if (query.Availability is { } availability)
        {
            // Availability is per volume (is the drive mounted?); a stored copy always counts as available.
            var mounted = MountedVolumeIds();
            q = availability switch
            {
                AvailabilityFilter.Stored => q.Where(a => a.IsStored),
                AvailabilityFilter.Available => q.Where(a => a.IsStored || a.Source.VolumeId == null || mounted.Contains(a.Source.VolumeId)),
                _ => q.Where(a => !a.IsStored && a.Source.VolumeId != null && !mounted.Contains(a.Source.VolumeId)),
            };
        }
        if (query.ScaleAnomaly) q = q.Where(a => a.ScaleAnomaly != ScaleAnomaly.None);
        if (query.OrientationUncertain) q = q.Where(a => a.OrientationUncertain);
        if (query.OrientationNotVerified) q = q.Where(a => a.Type == AssetType.Model3D && !a.OrientationVerified);
        if (query.TexturesMissing) q = q.Where(a => a.TexturesMissing);
        if (query.TexturesMatchedByAi) q = q.Where(a => a.Files.Any(f => f.Origin == CompanionOrigin.Ai));
        if (query.AiStatus is { } status) q = q.Where(a => a.AiStatus == status);
        if (query.FavoritesOnly) q = q.Where(a => a.Favorite);
        if (query.MinRating > 0)
        {
            var min = query.MinRating;
            q = q.Where(a => a.Rating >= min);
        }
        if (query.HasNotes) q = q.Where(a => a.Notes != null && a.Notes != "");
        if (query.GridUncertain) q = q.Where(a => a.GridUncertain);
        return q;
    }

    /// <summary>Volumes whose originals can be read now (availability is per volume).</summary>
    public List<string> MountedVolumeIds() => volumes?.GetVolumes().Select(v => v.Id).ToList() ?? [];

    /// <summary>A source and every source nested in it (packages inside a folder, archives inside archives).</summary>
    public static async Task<List<long>> SourceTreeAsync(CatalogDbContext db, long sourceId, CancellationToken ct = default)
    {
        var parents = await db.Sources.AsNoTracking().Where(s => s.ParentSourceId != null)
            .Select(s => new { s.Id, Parent = s.ParentSourceId!.Value }).ToListAsync(ct);
        var children = parents.ToLookup(p => p.Parent, p => p.Id);
        var result = new List<long> { sourceId };
        for (var i = 0; i < result.Count; i++) result.AddRange(children[result[i]]);
        return result;
    }
}
