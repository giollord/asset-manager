using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

public sealed record PreviewData(string Mime, byte[] Bytes)
{
    public string DataUrl => $"data:{Mime};base64,{Convert.ToBase64String(Bytes)}";
}

/// <summary>Reads stored previews for the UI: grid thumbnails, model angles and audio clips (no original needed).</summary>
public sealed class PreviewQueries(CatalogStore catalog)
{
    private static readonly PreviewKind[] ThumbnailOrder = [PreviewKind.Thumbnail, PreviewKind.ModelView, PreviewKind.Waveform, PreviewKind.UnityPreview];

    /// <summary>The preview a grid card shows: thumbnail, else the first model view, waveform or Unity's preview.</summary>
    public async Task<PreviewData?> ThumbnailAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var rows = await db.Previews.AsNoTracking()
            .Where(p => p.AssetId == assetId && ThumbnailOrder.Contains(p.Kind) && (p.Kind != PreviewKind.ModelView || p.Index == 0))
            .Select(p => new { p.Kind, p.Mime, p.Bytes }).ToListAsync(ct);
        var best = rows.OrderBy(p => Array.IndexOf(ThumbnailOrder, p.Kind)).FirstOrDefault();
        return best is null ? null : new PreviewData(best.Mime, best.Bytes);
    }

    /// <summary>All multi-angle renders of a model, in view order; for an animation clip, its animated preview.</summary>
    public async Task<IReadOnlyList<PreviewData>> ModelViewsAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var rows = await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId && (p.Kind == PreviewKind.ModelView || p.Kind == PreviewKind.Animation))
            .Select(p => new { p.Kind, p.Index, p.Mime, p.Bytes }).ToListAsync(ct);
        var views = rows.Any(r => r.Kind == PreviewKind.ModelView) ? rows.Where(r => r.Kind == PreviewKind.ModelView) : rows;
        return views.OrderBy(r => r.Index).Select(r => new PreviewData(r.Mime, r.Bytes)).ToList();
    }

    /// <summary>The stored audio preview clip.</summary>
    public async Task<PreviewData?> AudioClipAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId && p.Kind == PreviewKind.AudioClip)
            .Select(p => new PreviewData(p.Mime, p.Bytes)).FirstOrDefaultAsync(ct);
    }
}
