using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>How many game assets of one type carry a candidate.</summary>
public sealed record ConsensusTypeShare(AssetType Type, int Count, int Total)
{
    public double Share => Total == 0 ? 0 : (double)Count / Total;
}

/// <summary>A tag or style that many of a source's game assets share (spec "Source tag consensus candidates").</summary>
/// <param name="Name">Normalized name; a style and a tag with the same name are one candidate.</param>
/// <param name="FromTag">Some assets carry it as a tag.</param>
/// <param name="FromStyle">Some assets have it as a style.</param>
/// <param name="Count">Game assets that carry it (as a tag or a style).</param>
/// <param name="Total">Game assets of the source.</param>
public sealed record ConsensusCandidate(string Name, bool FromTag, bool FromStyle, int Count, int Total, IReadOnlyList<ConsensusTypeShare> PerType)
{
    public double Share => Total == 0 ? 0 : (double)Count / Total;

    /// <summary>The game assets that carry it (for sample previews).</summary>
    public IReadOnlySet<long> Carriers { get; init; } = new HashSet<long>();
}

/// <summary>What the consensus step knows about a source: its game assets by type and the candidates.</summary>
public sealed record ConsensusInput(long SourceId, int GameAssets, IReadOnlyDictionary<AssetType, int> TypeCounts, IReadOnlyList<ConsensusCandidate> Candidates);

/// <summary>
/// Finds source tag candidates (design D2): tags and styles on at least <c>share</c> of a source's game assets, overall or
/// within one asset type, in sources with at least <c>minAssets</c> game assets, excluding existing source tags. Staged
/// sources count too, since the step runs right after the item pass of an import.
/// </summary>
public static class ConsensusCandidates
{
    public static async Task<ConsensusInput> FindAsync(CatalogDbContext db, long sourceId, double share, int minAssets, CancellationToken ct = default)
    {
        var assets = await db.AllAssets.AsNoTracking()
            .Where(a => a.SourceId == sourceId && a.IsGameAsset && a.Included)
            .Select(a => new { a.Id, a.Type, a.Style }).ToListAsync(ct);
        var typeCounts = assets.GroupBy(a => a.Type).ToDictionary(g => g.Key, g => g.Count());
        if (assets.Count == 0 || assets.Count < minAssets) return new ConsensusInput(sourceId, assets.Count, typeCounts, []);

        var tagRows = await db.AssetTags.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Asset.SourceId == sourceId && t.Asset.IsGameAsset && t.Asset.Included && t.Origin != TagOrigin.Source)
            .Select(t => new { t.AssetId, t.Tag.Name }).ToListAsync(ct);
        var existing = (await db.SourceTags.AsNoTracking().Where(t => t.SourceId == sourceId).Select(t => t.Tag.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // name → (assets with it as a tag, assets with it as a style)
        var carriers = new Dictionary<string, (HashSet<long> Tag, HashSet<long> Style)>();
        (HashSet<long> Tag, HashSet<long> Style) Entry(string name)
        {
            if (!carriers.TryGetValue(name, out var e)) carriers[name] = e = ([], []);
            return e;
        }
        foreach (var t in tagRows) Entry(TagService.Normalize(t.Name)).Tag.Add(t.AssetId);
        foreach (var a in assets)
            foreach (var style in a.Style.Select(TagService.Normalize).Where(s => s.Length > 0))
                Entry(style).Style.Add(a.Id);

        var typeOf = assets.ToDictionary(a => a.Id, a => a.Type);
        var candidates = new List<ConsensusCandidate>();
        foreach (var (name, (tag, style)) in carriers)
        {
            if (existing.Contains(name)) continue;
            var all = tag.Union(style).ToList();
            var perType = all.GroupBy(id => typeOf[id])
                .Select(g => new ConsensusTypeShare(g.Key, g.Count(), typeCounts[g.Key]))
                .OrderByDescending(t => t.Total).ToList();
            var candidate = new ConsensusCandidate(name, tag.Count > 0, style.Count > 0, all.Count, assets.Count, perType) { Carriers = all.ToHashSet() };
            var typeWide = perType.Any(t => t.Total >= minAssets && t.Share >= share);
            if (candidate.Share >= share || typeWide) candidates.Add(candidate);
        }
        return new ConsensusInput(sourceId, assets.Count, typeCounts,
            candidates.OrderByDescending(c => c.Share).ThenBy(c => c.Name, StringComparer.Ordinal).ToList());
    }
}
