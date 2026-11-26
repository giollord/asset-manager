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

    // ---- user-owned fields (design D10): favourite, rating and notes; no AI or import code writes them ----

    public async Task SetFavoriteAsync(IReadOnlyCollection<long> assetIds, bool favorite, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        foreach (var chunk in assetIds.Chunk(500))
            await db.Assets.Where(a => chunk.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.Favorite, favorite), ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Sets the star rating (0 = unrated, up to 5).</summary>
    public async Task SetRatingAsync(IReadOnlyCollection<long> assetIds, int rating, CancellationToken ct = default)
    {
        rating = Math.Clamp(rating, 0, 5);
        await using var db = catalog.CreateDbContext();
        foreach (var chunk in assetIds.Chunk(500))
            await db.Assets.Where(a => chunk.Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.Rating, rating), ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>Sets the free-text notes (empty clears them); they are full-text searchable.</summary>
    public async Task SetNotesAsync(long assetId, string? notes, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var a = await db.AllAssets.FirstAsync(x => x.Id == assetId, ct);
        a.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
    }

    /// <summary>
    /// The user's sprite sheet decision (spec "Grid review and correction"): with a grid, the asset becomes a sprite sheet
    /// with the first <paramref name="frameCount"/> cells; without, it becomes an image. Recorded as a user edit, so
    /// re-detection and AI enrichment keep it.
    /// </summary>
    public async Task SetSpriteSheetAsync(long assetId, SpriteGrid? grid, int? frameCount = null, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var a = await db.AllAssets.FirstAsync(x => x.Id == assetId, ct);
        if (a.Type is not (AssetType.Image or AssetType.Texture or AssetType.SpriteSheet))
            throw new InvalidOperationException("Only images can be sprite sheets.");
        if (grid is not null)
        {
            if (a.Image is { } img && !grid.FitsIn(img.Width, img.Height))
                throw new InvalidOperationException($"The grid does not fit the {img.Width} × {img.Height} image.");
            a.SpriteSheet = SpriteSheetMetadata.FromGrid(grid, frameCount, GridDetectedBy.User);
            a.Type = AssetType.SpriteSheet;
        }
        else
        {
            a.SpriteSheet = null;
            if (a.Type == AssetType.SpriteSheet) a.Type = AssetType.Image;
        }
        a.GridUncertain = false;
        a.UserEdited |= EditedFields.Grid;
        a.UpdatedAt = DateTime.UtcNow;
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
