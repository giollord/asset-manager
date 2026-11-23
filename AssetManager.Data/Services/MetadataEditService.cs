using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>Manual edits of assets and sources (marked user-edited so AI never overwrites them).</summary>
public sealed class MetadataEditService(CatalogStore catalog)
{
    public async Task EditAssetAsync(long assetId, AssetUpdate update, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var a = await db.AllAssets.FirstAsync(x => x.Id == assetId, ct);
        update.ApplyAsUser(a);
        a.EmbeddingDirty = true;
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Removes an asset's license/link override so it inherits from its source again.</summary>
    public async Task ResetLicenseToSourceAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var a = await db.AllAssets.FirstAsync(x => x.Id == assetId, ct);
        a.HasLicenseOverride = false;
        a.LicenseOverride = new LicenseInfo();
        a.LinksOverride = null;
        a.UserEdited &= ~(EditedFields.License | EditedFields.Links);
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
    }

    public async Task EditSourceAsync(long sourceId, SourceUpdate update, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var s = await db.AllSources.FirstAsync(x => x.Id == sourceId, ct);
        update.ApplyAsUser(s);
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
    }
}
