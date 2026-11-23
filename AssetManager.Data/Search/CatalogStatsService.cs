using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Search;

/// <summary>A count on the dashboard with the search that lists exactly those assets (the drill-down).</summary>
public sealed record StatCount(string Label, int Count, SearchQuery Query);

/// <summary>Catalog file size by content, in bytes.</summary>
public sealed record CatalogSize(long Total, long Previews, long StoredOriginals, long Other);

public sealed record CatalogStats(
    int Assets, IReadOnlyList<StatCount> ByType, int Sources, int Collections, int Tags, CatalogSize Size,
    IReadOnlyList<StatCount> Enrichment, IReadOnlyList<StatCount> Availability, IReadOnlyList<StatCount> License,
    IReadOnlyList<StatCount> ModelIssues);

public sealed record CollectionCount(long Id, string Name, int Count, IReadOnlyList<StatCount> ByType, SearchQuery Query);

public sealed record SourceCount(long Id, string Name, SourceKind Kind, int Count, SearchQuery Query);

public sealed record TagCount(long Id, string Name, int Count, SearchQuery Query);

/// <summary>The dashboard's numbers (spec "Catalog overview dashboard", "Status breakdowns", "Collection, source and tag counts").</summary>
public interface ICatalogStatsService
{
    Task<CatalogStats> GetAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CollectionCount>> CollectionsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SourceCount>> SourcesAsync(CancellationToken ct = default);

    /// <summary>Tags with their usage, most used first.</summary>
    Task<IReadOnlyList<TagCount>> TagsAsync(CancellationToken ct = default);
}

/// <summary>
/// Aggregate counts over the committed catalog (design: Stats). Every drill-down count is computed through
/// <see cref="AssetSearchService"/> with the query it carries, so a count always equals what Browse lists for it.
/// Results are cached until the catalog reports a change.
/// </summary>
public sealed class CatalogStatsService : ICatalogStatsService
{
    private readonly CatalogStore _catalog;
    private readonly AssetSearchService _search;
    private readonly Lock _gate = new();
    private Task<CatalogStats>? _stats;
    private Task<IReadOnlyList<CollectionCount>>? _collections;
    private Task<IReadOnlyList<SourceCount>>? _sources;
    private Task<IReadOnlyList<TagCount>>? _tags;

    public CatalogStatsService(CatalogStore catalog, AssetSearchService search)
    {
        _catalog = catalog;
        _search = search;
        catalog.ContentChanged += Invalidate;
        catalog.Opened += Invalidate;
    }

    /// <summary>Drops the cached numbers (the catalog raises this once per commit or bulk operation).</summary>
    public void Invalidate()
    {
        lock (_gate) (_stats, _collections, _sources, _tags) = (null, null, null, null);
    }

    public Task<CatalogStats> GetAsync(CancellationToken ct = default) => Cached(ref _stats, () => ComputeAsync(CancellationToken.None));

    public Task<IReadOnlyList<CollectionCount>> CollectionsAsync(CancellationToken ct = default) => Cached(ref _collections, () => ComputeCollectionsAsync(CancellationToken.None));

    public Task<IReadOnlyList<SourceCount>> SourcesAsync(CancellationToken ct = default) => Cached(ref _sources, () => ComputeSourcesAsync(CancellationToken.None));

    public Task<IReadOnlyList<TagCount>> TagsAsync(CancellationToken ct = default) => Cached(ref _tags, () => ComputeTagsAsync(CancellationToken.None));

    private Task<T> Cached<T>(ref Task<T>? slot, Func<Task<T>> compute)
    {
        lock (_gate)
        {
            // A failed computation is not cached.
            if (slot is null || slot.IsFaulted || slot.IsCanceled) slot = compute();
            return slot;
        }
    }

    private Task<int> CountAsync(SearchQuery q) => _search.CountAsync(q);

    private async Task<StatCount> Stat(string label, SearchQuery q) => new(label, await CountAsync(q), q);

    private async Task<CatalogStats> ComputeAsync(CancellationToken ct)
    {
        await using var db = _catalog.CreateDbContext();
        var byType = await db.Assets.GroupBy(a => a.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var byAi = (await db.Assets.GroupBy(a => a.AiStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var sources = await db.Sources.CountAsync(ct);
        var collections = await db.Collections.CountAsync(ct);
        var tags = await db.Tags.CountAsync(t => t.Assets.Any(a => a.Asset.ImportSessionId == null), ct);

        var enrichment = new[]
        {
            (AiStatus.Enriched, "Enriched"), (AiStatus.Pending, "Pending"), (AiStatus.Failed, "Failed enrichment"), (AiStatus.NotEnriched, "Not enriched"),
        }.Select(x => new StatCount(x.Item2, byAi.GetValueOrDefault(x.Item1), new SearchQuery { AiStatus = x.Item1 })).ToList();

        // License and availability in one pass over a covering index, combined with the sources in memory. The
        // arithmetic mirrors the search filters (effective license, availability per volume); a test checks that
        // every count equals the Browse total of its query.
        var groups = await db.Assets
            .GroupBy(a => new { a.SourceId, a.HasLicenseOverride, C = a.LicenseOverride.Commercial, A = a.LicenseOverride.Attribution, a.IsStored })
            .Select(g => new { g.Key.SourceId, g.Key.HasLicenseOverride, g.Key.C, g.Key.A, g.Key.IsStored, Count = g.Count() })
            .ToListAsync(ct);
        var sourceInfo = await db.Sources.AsNoTracking()
            .Select(s => new { s.Id, s.VolumeId, C = s.License.Commercial, A = s.License.Attribution }).ToDictionaryAsync(s => s.Id, ct);
        var mounted = _search.MountedVolumeIds().ToHashSet();
        int Sum(Func<TriState, TriState, bool, string?, bool> predicate) => groups.Sum(g =>
        {
            var src = sourceInfo.GetValueOrDefault(g.SourceId);
            var commercial = g.HasLicenseOverride ? g.C : src?.C ?? TriState.Unknown;
            var attribution = g.HasLicenseOverride ? g.A : src?.A ?? TriState.Unknown;
            return predicate(commercial, attribution, g.IsStored, src?.VolumeId) ? g.Count : 0;
        });
        bool Reachable(bool stored, string? volume) => stored || volume is null || mounted.Contains(volume);

        var availability = new List<StatCount>
        {
            new("Original available", Sum((_, _, stored, volume) => Reachable(stored, volume)), new SearchQuery { Availability = AvailabilityFilter.Available }),
            new("Unavailable", Sum((_, _, stored, volume) => !Reachable(stored, volume)), new SearchQuery { Availability = AvailabilityFilter.Unavailable }),
            new("Stored in catalog", Sum((_, _, stored, _) => stored), new SearchQuery { Availability = AvailabilityFilter.Stored }),
        };
        var license = new List<StatCount>
        {
            new("Commercial use allowed", Sum((c, _, _, _) => c == TriState.Yes), new SearchQuery { Commercial = TriState.Yes }),
            new("No commercial use", Sum((c, _, _, _) => c == TriState.No), new SearchQuery { Commercial = TriState.No }),
            new("License unknown", Sum((c, _, _, _) => c == TriState.Unknown), new SearchQuery { Commercial = TriState.Unknown }),
            new("Attribution required", Sum((_, a, _, _) => a == TriState.Yes), new SearchQuery { Attribution = TriState.Yes }),
        };
        var issues = new List<StatCount>
        {
            await Stat("Scale anomaly", new SearchQuery { ScaleAnomaly = true }),
            await Stat("Orientation uncertain", new SearchQuery { OrientationUncertain = true }),
            await Stat("Orientation not verified", new SearchQuery { OrientationNotVerified = true }),
            await Stat("Textures missing", new SearchQuery { TexturesMissing = true }),
        };

        return new CatalogStats(
            byType.Sum(t => t.Count),
            byType.OrderByDescending(t => t.Count).Select(t => new StatCount(t.Key.ToString(), t.Count, new SearchQuery { Types = [t.Key] })).ToList(),
            sources, collections, tags, await SizeAsync(db, ct), enrichment, availability, license, issues);
    }

    /// <summary>The catalog file split by content, from SQLite's <c>dbstat</c> (or blob lengths when it is not compiled in).</summary>
    private static async Task<CatalogSize> SizeAsync(CatalogDbContext db, CancellationToken ct)
    {
        var pageCount = await Scalar(db, "PRAGMA page_count", ct);
        var pageSize = await Scalar(db, "PRAGMA page_size", ct);
        var total = pageCount * pageSize;
        long previews, stored;
        try
        {
            previews = await Scalar(db, "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name = 'Previews' OR name LIKE 'IX_Previews%'", ct);
            stored = await Scalar(db, "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name IN ('StoredFileChunks', 'StoredFiles') OR name LIKE 'IX_StoredFile%' OR name LIKE 'PK_StoredFile%'", ct);
        }
        catch (SqliteException)
        {
            previews = await Scalar(db, "SELECT COALESCE(SUM(length(Bytes)), 0) FROM Previews", ct);
            stored = await Scalar(db, "SELECT COALESCE(SUM(length(Bytes)), 0) FROM StoredFileChunks", ct);
        }
        return new CatalogSize(total, previews, stored, Math.Max(0, total - previews - stored));
    }

    private static async Task<long> Scalar(CatalogDbContext db, string sql, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<IReadOnlyList<CollectionCount>> ComputeCollectionsAsync(CancellationToken ct)
    {
        await using var db = _catalog.CreateDbContext();
        var result = new List<CollectionCount>();
        foreach (var c in await db.Collections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
        {
            var query = new SearchQuery { CollectionId = c.Id };
            // The same rule translation Browse applies, grouped by type.
            var members = await _search.ApplyAsync(db, db.Assets.AsNoTracking(), query, ct);
            var byType = await members.GroupBy(a => a.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            result.Add(new CollectionCount(c.Id, c.Name, byType.Sum(t => t.Count),
                byType.OrderByDescending(t => t.Count).Select(t => new StatCount(t.Key.ToString(), t.Count, query with { Types = [t.Key] })).ToList(), query));
        }
        return result;
    }

    private async Task<IReadOnlyList<SourceCount>> ComputeSourcesAsync(CancellationToken ct)
    {
        await using var db = _catalog.CreateDbContext();
        var sources = await db.Sources.AsNoTracking().Select(s => new { s.Id, s.DisplayName, s.Kind, s.ParentSourceId }).ToListAsync(ct);
        var own = (await db.Assets.GroupBy(a => a.SourceId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var children = sources.Where(s => s.ParentSourceId is not null).ToLookup(s => s.ParentSourceId!.Value, s => s.Id);
        // A source's count includes its nested sources, like its Browse filter.
        int Total(long id) => own.GetValueOrDefault(id) + children[id].Sum(Total);
        return sources.OrderBy(s => s.DisplayName)
            .Select(s => new SourceCount(s.Id, s.DisplayName, s.Kind, Total(s.Id), new SearchQuery { SourceId = s.Id })).ToList();
    }

    private async Task<IReadOnlyList<TagCount>> ComputeTagsAsync(CancellationToken ct)
    {
        await using var db = _catalog.CreateDbContext();
        var rows = await db.Tags
            .Select(t => new { t.Id, t.Name, Count = t.Assets.Count(a => a.Asset.ImportSessionId == null) })
            .Where(t => t.Count > 0)
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name)
            .ToListAsync(ct);
        return rows.Select(t => new TagCount(t.Id, t.Name, t.Count, new SearchQuery { IncludeTags = [t.Name] })).ToList();
    }
}
