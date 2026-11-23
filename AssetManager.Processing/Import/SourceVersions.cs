using AssetManager.Core.Domain;
using AssetManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Import;

/// <summary>Counts shown with a replacement proposal; the asset ids are the old catalog assets / staged assets.</summary>
public sealed record VersionDiff(IReadOnlyList<(long Staged, long Old)> Updated, IReadOnlyList<long> Added, IReadOnlyList<long> Removed);

/// <summary>
/// New package versions (design D8, spec "Update source from new version"). A staged source is compared with the
/// committed sources: score 1 for the same Asset Store id, otherwise the Jaccard overlap of the Unity GUID sets
/// (or of the relative-path sets when there are no GUIDs). A best score of at least <see cref="Threshold"/> proposes
/// replacing that source. On confirm, matched assets update in place (keeping their ids, user edits and user tags),
/// new ones are added and missing ones removed.
/// </summary>
public sealed class SourceVersions(CatalogStore catalog, ILogger<SourceVersions>? logger = null)
{
    public const double Threshold = 0.5;

    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>A staged or committed source's comparable keys: GUIDs when its files have them, else relative paths.</summary>
    private sealed record Keys(HashSet<string> Guids, HashSet<string> Paths)
    {
        public HashSet<string> Set => Guids.Count > 0 ? Guids : Paths;
    }

    private static string PathKey(string relativePath) => relativePath.Replace('\\', '/').ToLowerInvariant();

    /// <summary>Sets <see cref="Source.ReplacesSourceId"/> on the session's staged sources that look like new versions.</summary>
    /// <returns>The number of proposals.</returns>
    public async Task<int> ProposeAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var staged = await db.AllSources.Where(s => s.ImportSessionId == sessionId).ToListAsync(ct);
        var proposals = 0;
        foreach (var source in staged)
        {
            var keys = await KeysAsync(db, source.Id, ct);
            (long Id, double Score)? best = null;
            if (source.Store.StoreId is { Length: > 0 } storeId)
            {
                var same = await db.Sources.Where(s => s.Store.StoreId == storeId).OrderByDescending(s => s.ImportedAt).Select(s => s.Id).FirstOrDefaultAsync(ct);
                if (same != 0) best = (same, 1);
            }
            if (best is null && keys.Set.Count > 0)
            {
                foreach (var candidate in await CandidatesAsync(db, keys, ct))
                {
                    var other = await KeysAsync(db, candidate, ct);
                    var a = keys.Guids.Count > 0 && other.Guids.Count > 0 ? keys.Guids : keys.Paths;
                    var b = keys.Guids.Count > 0 && other.Guids.Count > 0 ? other.Guids : other.Paths;
                    var union = a.Count + b.Count - a.Count(b.Contains);
                    var score = union == 0 ? 0 : (double)a.Count(b.Contains) / union;
                    if (score >= Threshold && (best is null || score > best.Value.Score)) best = (candidate, score);
                }
            }
            if (best is not { } match) continue;
            // The same content again (a re-added folder) is not a new version: its files are skipped as duplicates.
            var oldIds = db.Assets.Where(o => o.SourceId == match.Id).Select(o => (long?)o.Id);
            var stagedAssets = await db.AllAssets.CountAsync(a => a.SourceId == source.Id, ct);
            var unchanged = await db.AllAssets.CountAsync(a => a.SourceId == source.Id && oldIds.Contains(a.DuplicateOfAssetId), ct);
            if (stagedAssets > 0 && unchanged == stagedAssets && await oldIds.CountAsync(ct) == stagedAssets) continue;
            source.ReplacesSourceId = match.Id;
            source.ReplaceScore = match.Score;
            // Unchanged files of the old version are updates, not skipped duplicates.
            await db.AllAssets.Where(a => a.SourceId == source.Id && a.DuplicateOfAssetId != null && oldIds.Contains(a.DuplicateOfAssetId))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Included, true), ct);
            proposals++;
        }
        await db.SaveChangesAsync(ct);
        if (proposals > 0) _log.LogInformation("Import {Session}: {Count} sources look like new versions of cataloged ones", sessionId, proposals);
        return proposals;
    }

    private static async Task<Keys> KeysAsync(CatalogDbContext db, long sourceId, CancellationToken ct)
    {
        var files = await db.AssetFiles.IgnoreQueryFilters().Where(f => f.Role == FileRole.Primary && f.Asset.SourceId == sourceId)
            .Select(f => new { f.UnityGuid, f.RelativePath }).ToListAsync(ct);
        return new Keys(files.Where(f => f.UnityGuid is { Length: > 0 }).Select(f => f.UnityGuid!).ToHashSet(),
            files.Select(f => PathKey(f.RelativePath)).ToHashSet());
    }

    /// <summary>Committed sources sharing at least one key.</summary>
    private static async Task<HashSet<long>> CandidatesAsync(CatalogDbContext db, Keys keys, CancellationToken ct)
    {
        var result = new HashSet<long>();
        var committed = db.AssetFiles.IgnoreQueryFilters().Where(f => f.Role == FileRole.Primary && f.Asset.ImportSessionId == null);
        if (keys.Guids.Count > 0)
        {
            foreach (var chunk in keys.Guids.Chunk(500))
                result.UnionWith(await committed.Where(f => chunk.Contains(f.UnityGuid)).Select(f => f.Asset.SourceId).Distinct().ToListAsync(ct));
        }
        // Paths are compared case-insensitively in memory; the lookup uses the file names, which are indexed by asset.
        var names = keys.Paths.Select(p => p[(p.LastIndexOf('/') + 1)..]).Distinct().ToList();
        foreach (var chunk in names.Chunk(500))
        {
            var rows = await committed.Where(f => chunk.Contains(f.FileName.ToLower())).Select(f => new { f.Asset.SourceId, f.RelativePath }).ToListAsync(ct);
            result.UnionWith(rows.Where(r => keys.Paths.Contains(PathKey(r.RelativePath))).Select(r => r.SourceId));
        }
        return result;
    }

    /// <summary>
    /// The replacement diff: included staged assets matched to an old one (by GUID, else relative path) are updates,
    /// unmatched included ones are added, and old assets no staged asset matches are removed. An excluded staged
    /// asset that matches leaves its old asset untouched.
    /// </summary>
    public static async Task<VersionDiff> DiffAsync(CatalogDbContext db, long stagedSourceId, long oldSourceId, CancellationToken ct = default)
    {
        var staged = await PrimariesAsync(db, stagedSourceId, ct);
        var old = await PrimariesAsync(db, oldSourceId, ct);
        var byGuid = old.Where(o => o.Guid is not null).GroupBy(o => o.Guid!).ToDictionary(g => g.Key, g => g.First().AssetId);
        var byPath = old.GroupBy(o => o.Path).ToDictionary(g => g.Key, g => g.First().AssetId);
        var updated = new List<(long, long)>();
        var added = new List<long>();
        var matchedOld = new HashSet<long>();
        foreach (var s in staged)
        {
            long? match = s.Guid is not null && byGuid.TryGetValue(s.Guid, out var g) ? g : byPath.TryGetValue(s.Path, out var p) ? p : null;
            if (match is { } m && !matchedOld.Add(m)) match = null; // each old asset matches once
            if (!s.Included) continue;
            if (match is { } o) updated.Add((s.AssetId, o));
            else added.Add(s.AssetId);
        }
        var removed = old.Select(o => o.AssetId).Distinct().Where(id => !matchedOld.Contains(id)).ToList();
        return new VersionDiff(updated, added, removed);
    }

    private sealed record Primary(long AssetId, string? Guid, string Path, bool Included);

    private static async Task<List<Primary>> PrimariesAsync(CatalogDbContext db, long sourceId, CancellationToken ct)
    {
        var rows = await db.AssetFiles.IgnoreQueryFilters().Where(f => f.Role == FileRole.Primary && f.Asset.SourceId == sourceId)
            .OrderBy(f => f.AssetId).Select(f => new { f.AssetId, f.UnityGuid, f.RelativePath, f.Asset.Included }).ToListAsync(ct);
        return rows.Select(r => new Primary(r.AssetId, r.UnityGuid is { Length: > 0 } ? r.UnityGuid : null, PathKey(r.RelativePath), r.Included)).ToList();
    }

    /// <summary>
    /// Replaces <paramref name="oldSourceId"/> with the staged source, inside the caller's transaction. The old source
    /// keeps its id and takes the new location and package info; matched assets keep their ids and user edits.
    /// </summary>
    /// <returns>Old assets that had stored originals (their files changed, so they need storing again).</returns>
    public async Task<List<long>> ReplaceAsync(CatalogDbContext db, long sessionId, long stagedSourceId, long oldSourceId, CancellationToken ct = default)
    {
        var diff = await DiffAsync(db, stagedSourceId, oldSourceId, ct);
        var restore = new List<long>();
        foreach (var (stagedId, oldId) in diff.Updated)
        {
            if (await UpdateInPlaceAsync(db, stagedId, oldId, ct)) restore.Add(oldId);
        }
        foreach (var chunk in diff.Removed.Chunk(500))
            await db.AllAssets.Where(a => chunk.Contains(a.Id)).ExecuteDeleteAsync(ct);
        // What is left of the staged source: added assets, excluded ones (dropped by the caller) and child sources.
        await db.AllAssets.Where(a => a.SourceId == stagedSourceId && a.Included)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.SourceId, oldSourceId).SetProperty(a => a.ImportSessionId, (long?)null), ct);
        await db.AllAssets.Where(a => a.SourceId == stagedSourceId).ExecuteDeleteAsync(ct);
        await db.AllSources.Where(s => s.ParentSourceId == stagedSourceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.ParentSourceId, oldSourceId), ct);
        await db.StagedSkips.Where(s => s.SourceId == stagedSourceId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SourceId, oldSourceId), ct);

        var staged = await db.AllSources.FirstAsync(s => s.Id == stagedSourceId, ct);
        var old = await db.AllSources.FirstAsync(s => s.Id == oldSourceId, ct);
        old.Kind = staged.Kind;
        old.Location = staged.Location;
        old.VolumeId = staged.VolumeId;
        old.Fingerprint = staged.Fingerprint;
        old.Store = staged.Store;
        old.Error = staged.Error;
        if (!old.UserEdited.HasFlag(EditedFields.Name)) old.DisplayName = staged.DisplayName;
        var packDone = staged.Description is not null;
        // Without a finished pack pass the description is cleared so the import's pack pass runs on the replaced source.
        if (!old.UserEdited.HasFlag(EditedFields.Description)) old.Description = staged.Description;
        if (!old.UserEdited.HasFlag(EditedFields.Notes) && (packDone || staged.Notes is not null)) old.Notes = staged.Notes;
        if (packDone && !old.UserEdited.HasFlag(EditedFields.License)) old.License = staged.License;
        if (packDone && !old.UserEdited.HasFlag(EditedFields.Links)) old.Links = staged.Links;
        old.CommittedFromSessionId = sessionId;
        await db.SaveChangesAsync(ct);
        db.Sources.Remove(staged);
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Source {Old} replaced by a new version: {Updated} updated, {Added} added, {Removed} removed",
            oldSourceId, diff.Updated.Count, diff.Added.Count, diff.Removed.Count);
        return restore;
    }

    /// <returns>Whether the old asset had stored originals.</returns>
    private static async Task<bool> UpdateInPlaceAsync(CatalogDbContext db, long stagedId, long oldId, CancellationToken ct)
    {
        var s = await db.AllAssets.Include(a => a.Tags).FirstAsync(a => a.Id == stagedId, ct);
        var o = await db.AllAssets.Include(a => a.Tags).FirstAsync(a => a.Id == oldId, ct);
        var edited = o.UserEdited;
        if (!edited.HasFlag(EditedFields.Name)) o.Name = s.Name;
        if (!edited.HasFlag(EditedFields.Type)) o.Type = s.Type;
        (o.Image, o.Audio, o.Model, o.Font, o.Video, o.Document) = (s.Image, s.Audio, s.Model, s.Font, s.Video, s.Document);
        (o.ScaleAnomaly, o.TexturesMissing, o.PreviewError, o.PreviewOptionsVersion) = (s.ScaleAnomaly, s.TexturesMissing, s.PreviewError, s.PreviewOptionsVersion);
        if (!edited.HasFlag(EditedFields.Orientation)) (o.OrientationUncertain, o.OrientationVerified) = (s.OrientationUncertain, s.OrientationVerified);
        if (s.AiStatus == AiStatus.Pending)
        {
            o.AiStatus = AiStatus.Pending; // enriched again by the import's item pass; user edits stay protected there
        }
        else
        {
            if (!edited.HasFlag(EditedFields.Description)) o.Description = s.Description;
            if (!edited.HasFlag(EditedFields.Style)) o.Style = s.Style;
            if (!edited.HasFlag(EditedFields.Colors)) o.Colors = s.Colors;
            (o.IsGameAsset, o.AiStatus, o.AiInputMode, o.AiError) = (s.IsGameAsset, s.AiStatus, s.AiInputMode, s.AiError);
        }
        // Tags: the user's stay; the rest come from the new version.
        o.Tags.RemoveAll(t => t.Origin != TagOrigin.User);
        foreach (var tag in s.Tags.Where(t => !o.Tags.Any(x => x.TagId == t.TagId)).ToList())
            o.Tags.Add(new AssetTag { TagId = tag.TagId, Origin = tag.Origin });
        var wasStored = o.IsStored;
        o.IsStored = false;
        o.EmbeddingDirty = true;
        o.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await db.AssetFiles.Where(f => f.AssetId == oldId).ExecuteDeleteAsync(ct);
        await db.AssetFiles.Where(f => f.AssetId == stagedId).ExecuteUpdateAsync(u => u.SetProperty(f => f.AssetId, oldId), ct);
        await db.Previews.Where(p => p.AssetId == oldId).ExecuteDeleteAsync(ct);
        await db.Previews.Where(p => p.AssetId == stagedId).ExecuteUpdateAsync(u => u.SetProperty(p => p.AssetId, oldId), ct);
        db.ChangeTracker.Clear();
        await db.AllAssets.Where(a => a.Id == stagedId).ExecuteDeleteAsync(ct);
        return wasStored;
    }
}
