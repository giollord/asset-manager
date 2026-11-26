using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

public sealed record PreviewData(string Mime, byte[] Bytes)
{
    public string DataUrl => $"data:{Mime};base64,{Convert.ToBase64String(Bytes)}";
}

/// <summary>
/// A model's stored renders laid out as a turntable (design D18): <see cref="Views"/>[ring * Azimuths + azimuth], ring 0
/// the lowest elevation, starting at <see cref="Primary"/>.
/// </summary>
public sealed record TurntableViews(int Rings, int Azimuths, int Primary, IReadOnlyList<PreviewData> Views);

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

    /// <summary>
    /// The model's renders as a turntable, or null when it has none (animations keep their animated preview). The
    /// layout is <paramref name="rings"/> × <paramref name="azimuths"/> from the current preview settings when the stored
    /// views match it (that many, with indices 0..n-1); otherwise, e.g. after the settings changed, one ring of all views
    /// in stored order (spec "Renders from older settings").
    /// </summary>
    /// <param name="primary">The primary view index of the current settings; clamped to the stored views.</param>
    public async Task<TurntableViews?> ModelTurntableAsync(long assetId, int rings, int azimuths, int primary, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var rows = await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId && p.Kind == PreviewKind.ModelView)
            .OrderBy(p => p.Index).Select(p => new { p.Index, p.Mime, p.Bytes }).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var dense = rows.Select((r, i) => r.Index == i).All(x => x);
        var matches = dense && rings > 0 && azimuths > 0 && rows.Count == rings * azimuths;
        var views = rows.Select(r => new PreviewData(r.Mime, r.Bytes)).ToList();
        return matches
            ? new TurntableViews(rings, azimuths, Math.Clamp(primary, 0, views.Count - 1), views)
            : new TurntableViews(1, views.Count, Math.Clamp(primary, 0, views.Count - 1), views);
    }

    /// <summary>The stored audio preview clip.</summary>
    public async Task<PreviewData?> AudioClipAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        return await db.Previews.AsNoTracking().Where(p => p.AssetId == assetId && p.Kind == PreviewKind.AudioClip)
            .Select(p => new PreviewData(p.Mime, p.Bytes)).FirstOrDefaultAsync(ct);
    }
}
