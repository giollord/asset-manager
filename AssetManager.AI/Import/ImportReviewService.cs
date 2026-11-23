using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.AI.Import;

public sealed record ReviewFile(long Id, FileRole Role, string RelativePath, long Size);

/// <summary>A staged asset as the review step shows it.</summary>
public sealed record ReviewItem(
    long Id, long SourceId, string Name, AssetType Type, string? Description, IReadOnlyList<TagView> Tags, AiStatus AiStatus,
    string? AiError, bool Included, bool Duplicate, IReadOnlyList<ReviewFile> Files, string? PreviewError, EditedFields UserEdited,
    bool Unchanged = false)
{
    public int Companions => Files.Count(f => f.Role == FileRole.Companion);
}

/// <summary>A replacement proposal: the staged source as a new version of a cataloged one, with the diff counts.</summary>
public sealed record ReviewReplacement(long OldSourceId, string OldName, double Score, bool? Accepted, int Updated, int Added, int Removed);

public sealed record ReviewSkip(string Path, SkipKind Kind, string Reason);

public sealed record ReviewSource(
    long Id, long? ParentId, string Name, SourceKind Kind, string? Description, LicenseInfo License, string? Notes, string? Error,
    ReviewReplacement? Replacement, IReadOnlyList<ReviewItem> Items, IReadOnlyList<ReviewSkip> Skips);

public sealed record ReviewModel(ImportSession Session, IReadOnlyList<ReviewSource> Sources)
{
    public int Assets => Sources.Sum(s => s.Items.Count);

    public int Included => Sources.Sum(s => s.Items.Count(i => i.Included));
}

/// <summary>
/// The import wizard's Preview/Review step (spec "Staged import preview", "Edit and AI-correct staged items",
/// "Update source from new version"): staged items grouped by source with their skips, inline edits, include/exclude,
/// regrouping, a selection-scoped AI correction and the replacement decision.
/// </summary>
public sealed class ImportReviewService(
    CatalogStore catalog, IJobQueue jobs, MetadataEditService edits, TagService tags, StagedRegrouping regrouping)
{
    public async Task<ReviewModel?> LoadAsync(long sessionId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var session = await db.ImportSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;
        var sources = await db.AllSources.AsNoTracking().Where(s => s.ImportSessionId == sessionId).OrderBy(s => s.Id).ToListAsync(ct);
        var sourceIds = sources.Select(s => s.Id).ToList();
        var assets = await db.AllAssets.AsNoTracking().Include(a => a.Files).Include(a => a.Tags).ThenInclude(t => t.Tag)
            .Where(a => a.ImportSessionId == sessionId).OrderBy(a => a.Id).ToListAsync(ct);
        var skips = (await db.StagedSkips.AsNoTracking().Where(s => s.SessionId == sessionId).OrderBy(s => s.Path).ToListAsync(ct))
            .ToLookup(s => s.SourceId);
        var oldNames = await db.Sources.Where(s => sources.Select(x => x.ReplacesSourceId).Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.DisplayName, ct);
        var bySource = assets.ToLookup(a => a.SourceId);

        var result = new List<ReviewSource>();
        foreach (var s in sources)
        {
            ReviewReplacement? replacement = null;
            if (s.ReplacesSourceId is { } old && oldNames.TryGetValue(old, out var oldName))
            {
                var diff = await SourceVersions.DiffAsync(db, s.Id, old, ct);
                replacement = new ReviewReplacement(old, oldName, s.ReplaceScore, s.ReplaceAccepted, diff.Updated.Count, diff.Added.Count, diff.Removed.Count);
            }
            // Files identical to the version being replaced are unchanged updates, not duplicates to skip.
            var replaced = s.ReplacesSourceId is { } r && s.ReplaceAccepted != false
                ? (await db.Assets.Where(a => a.SourceId == r).Select(a => a.Id).ToListAsync(ct)).ToHashSet()
                : [];
            var items = bySource[s.Id].Select(a => new ReviewItem(a.Id, a.SourceId, a.Name, a.Type, a.Description,
                a.Tags.OrderBy(t => t.Origin).ThenBy(t => t.Tag.Name).Select(t => new TagView(t.Tag.Name, t.Origin)).ToList(),
                a.AiStatus, a.AiError, a.Included, a.DuplicateOfAssetId is { } d && !replaced.Contains(d),
                a.Files.OrderBy(f => f.Role).ThenBy(f => f.RelativePath).Select(f => new ReviewFile(f.Id, f.Role, f.RelativePath, f.Size)).ToList(),
                a.PreviewError, a.UserEdited, a.DuplicateOfAssetId is { } u && replaced.Contains(u))).ToList();
            var sourceSkips = skips[s.Id].Select(k => new ReviewSkip(k.Path, k.Kind, k.Reason)).ToList();
            result.Add(new ReviewSource(s.Id, s.ParentSourceId, s.DisplayName, s.Kind, s.Description, s.License, s.Notes, s.Error,
                replacement, items, sourceSkips));
        }
        // Skips without a source (e.g. an unreadable input) are listed under a pseudo source.
        if (skips[null].Any())
            result.Add(new ReviewSource(0, null, "Not imported", SourceKind.Folder, null, new LicenseInfo(), null, null, null, [],
                skips[null].Select(k => new ReviewSkip(k.Path, k.Kind, k.Reason)).ToList()));
        return new ReviewModel(session, result);
    }

    /// <summary>The preview the review shows for an item (thumbnail, model view, waveform or Unity's preview).</summary>
    public async Task<(string Mime, byte[] Bytes)?> PreviewAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var previews = await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId &&
                (p.Kind == PreviewKind.Thumbnail || p.Kind == PreviewKind.ModelView || p.Kind == PreviewKind.Waveform || p.Kind == PreviewKind.UnityPreview))
            .Select(p => new { p.Kind, p.Index, p.Mime, p.Bytes }).ToListAsync(ct);
        var best = previews.OrderBy(p => p.Kind switch
            {
                PreviewKind.Thumbnail => 0,
                PreviewKind.ModelView => 1,
                PreviewKind.Waveform => 2,
                _ => 3,
            }).ThenBy(p => p.Index).FirstOrDefault();
        return best is null ? null : (best.Mime, best.Bytes);
    }

    public async Task SetIncludedAsync(IReadOnlyCollection<long> assetIds, bool included, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        foreach (var chunk in assetIds.Chunk(500))
            await db.AllAssets.Where(a => chunk.Contains(a.Id) && a.ImportSessionId != null)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Included, included), ct);
    }

    /// <summary>An inline edit; the changed fields become user-edited, so AI never overwrites them.</summary>
    public Task EditAsync(long assetId, AssetUpdate update, CancellationToken ct = default) => edits.EditAssetAsync(assetId, update, ct);

    public Task AddTagAsync(IReadOnlyCollection<long> assetIds, string tag, CancellationToken ct = default) =>
        tags.AddTagsAsync(assetIds, [tag], TagOrigin.User, ct);

    public Task RemoveTagAsync(IReadOnlyCollection<long> assetIds, string tag, CancellationToken ct = default) =>
        tags.RemoveTagsAsync(assetIds, [tag], ct);

    /// <summary>
    /// Re-runs the AI item pass for the selected items only, with the user's message added to the import instruction
    /// (spec "AI correction on selection").
    /// </summary>
    /// <returns>The AI job's id.</returns>
    public async Task<long> CorrectAsync(long sessionId, IReadOnlyCollection<long> assetIds, string correction, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) throw new ArgumentException("Select the items to correct.", nameof(assetIds));
        if (string.IsNullOrWhiteSpace(correction)) throw new ArgumentException("Enter what the AI should change.", nameof(correction));
        await using var db = catalog.CreateDbContext();
        var instruction = await db.ImportSessions.Where(s => s.Id == sessionId).Select(s => s.Instruction).FirstAsync(ct);
        var title = assetIds.Count == 1 ? "AI correction of 1 item" : $"AI correction of {assetIds.Count} items";
        return await jobs.EnqueueAsync(JobTypes.Enrich, title, new EnrichPayload([.. assetIds], instruction, correction.Trim()), ct: ct);
    }

    /// <summary>Accepts or declines the proposed replacement, or picks the cataloged source to replace manually.</summary>
    public async Task SetReplacementAsync(long stagedSourceId, bool accepted, long? oldSourceId = null, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var source = await db.AllSources.FirstAsync(s => s.Id == stagedSourceId && s.ImportSessionId != null, ct);
        if (oldSourceId is { } manual)
        {
            if (!await db.Sources.AnyAsync(s => s.Id == manual, ct)) throw new InvalidOperationException("That source is no longer in the catalog.");
            (source.ReplacesSourceId, source.ReplaceScore) = (manual, 0);
        }
        source.ReplaceAccepted = accepted;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Cataloged sources whose name contains <paramref name="text"/>, for picking a replacement manually.</summary>
    public async Task<List<(long Id, string Name)>> FindSourcesAsync(string? text, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var q = db.Sources.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(text)) q = q.Where(s => EF.Functions.Like(s.DisplayName, $"%{text.Trim()}%"));
        var rows = await q.OrderBy(s => s.DisplayName).Take(20).Select(s => new { s.Id, s.DisplayName }).ToListAsync(ct);
        return rows.Select(r => (r.Id, r.DisplayName)).ToList();
    }

    public Task<long> SplitAsync(long fileId, CancellationToken ct = default) => regrouping.SplitAsync(fileId, ct);

    public Task MergeAsync(long targetId, IReadOnlyCollection<long> assetIds, CancellationToken ct = default) => regrouping.MergeAsync(targetId, assetIds, ct);
}
