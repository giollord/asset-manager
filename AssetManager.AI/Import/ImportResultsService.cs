using AssetManager.Core.Domain;
using AssetManager.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Import;

public sealed record ResultSkip(string Path, SkipKind Kind, string Reason);

/// <summary>
/// One source in the import results: what the commit put into it, or (<see cref="SourceId"/> 0) the "Not imported"
/// group of skips that belong to no source.
/// </summary>
public sealed record ResultSource(long SourceId, string Name, int Assets, bool Replaced, int Updated, int Added, int Removed, bool Exists,
    IReadOnlyList<ResultSkip> Skips);

public sealed record ImportResults(ImportSession Session, IReadOnlyList<ResultSource> Sources)
{
    public int Assets => Sources.Sum(s => s.Assets);

    public int Skipped => Sources.Sum(s => s.Skips.Count);

    public int Replaced => Sources.Count(s => s.Replaced);
}

/// <summary>
/// The import results (spec "Import results"): after the commit, the assets added per source, the sources replaced
/// by a new version with their counts, and the skipped items with their reasons, grouped by source.
/// </summary>
public sealed class ImportResultsService(CatalogStore catalog)
{
    public const string NotImported = "Not imported";

    /// <summary>The results of a committed session; null for an unknown session or one not committed yet.</summary>
    public async Task<ImportResults?> LoadAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var session = await db.ImportSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is not { State: ImportSessionState.Committed }) return null;
        var skips = (await db.StagedSkips.AsNoTracking().Where(s => s.SessionId == sessionId).OrderBy(s => s.Path).ToListAsync(ct))
            .ToLookup(s => s.SourceId);
        var results = session.Results ?? [];
        var ids = results.Select(r => r.SourceId).Concat(skips.Select(g => g.Key ?? 0)).Where(id => id != 0).Distinct().ToList();
        // Names as they are now (the user may have renamed or deleted a source since).
        var current = await db.Sources.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);

        static List<ResultSkip> Of(IEnumerable<StagedSkip> rows) => rows.Select(k => new ResultSkip(k.Path, k.Kind, k.Reason)).ToList();
        var list = new List<ResultSource>();
        foreach (var r in results)
            list.Add(new ResultSource(r.SourceId, current.GetValueOrDefault(r.SourceId, r.Name), r.Assets, r.Replaced, r.Updated, r.Added, r.Removed,
                current.ContainsKey(r.SourceId), Of(skips[r.SourceId])));
        // Skips under a source that got no assets of its own (e.g. a nested archive that only held scripts).
        foreach (var g in skips.Where(g => g.Key is { } id && results.All(r => r.SourceId != id)))
            list.Add(new ResultSource(g.Key!.Value, current.GetValueOrDefault(g.Key.Value, "Removed source"), 0, false, 0, 0, 0,
                current.ContainsKey(g.Key.Value), Of(g)));
        if (skips[null].Any())
            list.Add(new ResultSource(0, NotImported, 0, false, 0, 0, 0, false, Of(skips[null])));
        return new ImportResults(session, list);
    }
}
