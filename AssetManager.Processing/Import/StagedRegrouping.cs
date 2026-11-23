using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Import;

/// <summary>
/// Changes how staged files are grouped (spec "Edit and AI-correct staged items"): a companion file can be split off
/// into its own asset, and assets can be merged into another one as its companions. Metadata and previews of the
/// affected assets are generated again.
/// </summary>
public sealed class StagedRegrouping(CatalogStore catalog, ClassificationSettings classification, PreviewService previews, PreviewSettings previewSettings)
{
    /// <returns>The new asset's id.</returns>
    public async Task<long> SplitAsync(long fileId, CancellationToken ct = default)
    {
        long oldId, newId;
        await using (var db = catalog.CreateDbContext())
        {
            var file = await db.AssetFiles.IgnoreQueryFilters().Include(f => f.Asset).ThenInclude(a => a.Tags)
                .FirstOrDefaultAsync(f => f.Id == fileId, ct) ?? throw new InvalidOperationException("The file no longer exists.");
            if (file.Asset.ImportSessionId is null) throw new InvalidOperationException("Only staged items can be regrouped.");
            if (file.Role != FileRole.Companion) throw new InvalidOperationException("Only companion files can be split off.");
            var old = file.Asset;
            var c = new FileClassifier(await classification.GetAsync(ct)).Classify(file.RelativePath);
            var now = DateTime.UtcNow;
            var asset = new Asset
            {
                SourceId = old.SourceId,
                Type = c.Kind == FileKind.Asset ? c.Type : AssetType.Other,
                Name = Path.GetFileNameWithoutExtension(file.FileName),
                ImportSessionId = old.ImportSessionId,
                AiStatus = old.AiStatus == AiStatus.NotEnriched ? AiStatus.NotEnriched : AiStatus.Pending,
                ImportedAt = now,
                UpdatedAt = now,
            };
            // Import tags (shared tags, pack defaults) belong to every asset of the import.
            foreach (var tag in old.Tags.Where(t => t.Origin == TagOrigin.Import))
                asset.Tags.Add(new AssetTag { TagId = tag.TagId, Origin = TagOrigin.Import });
            db.Assets.Add(asset);
            file.Asset = asset;
            (file.Role, file.Material, file.Slot) = (FileRole.Primary, null, null);
            await db.SaveChangesAsync(ct);
            (oldId, newId) = (old.Id, asset.Id);
        }
        await ReprocessAsync([oldId, newId], ct);
        return newId;
    }

    /// <summary>Moves the files of <paramref name="assetIds"/> into <paramref name="targetId"/> as companions; those assets are removed.</summary>
    public async Task MergeAsync(long targetId, IReadOnlyCollection<long> assetIds, CancellationToken ct = default)
    {
        var others = assetIds.Where(id => id != targetId).Distinct().ToList();
        if (others.Count == 0) return;
        await using (var db = catalog.CreateDbContext())
        {
            var rows = await db.AllAssets.Where(a => a.Id == targetId || others.Contains(a.Id)).Select(a => new { a.Id, a.ImportSessionId }).ToListAsync(ct);
            var session = rows.FirstOrDefault(r => r.Id == targetId)?.ImportSessionId ?? throw new InvalidOperationException("Only staged items can be regrouped.");
            if (rows.Count != others.Count + 1 || rows.Any(r => r.ImportSessionId != session))
                throw new InvalidOperationException("Only items of the same import can be merged.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.AssetFiles.Where(f => others.Contains(f.AssetId)).ExecuteUpdateAsync(u => u
                .SetProperty(f => f.AssetId, targetId).SetProperty(f => f.Role, FileRole.Companion), ct);
            await db.AllAssets.Where(a => others.Contains(a.Id)).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        }
        await ReprocessAsync([targetId], ct);
    }

    private async Task ReprocessAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var options = await previewSettings.GetAsync(ct);
        foreach (var id in ids) await previews.ProcessOneAsync(id, options, ct);
        catalog.NotifyContentChanged();
    }
}
