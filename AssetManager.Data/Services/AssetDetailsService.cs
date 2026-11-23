using AssetManager.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Data.Services;

/// <summary>Everything the details drawer shows about one asset.</summary>
/// <param name="Asset">The asset with its files and source (read-only snapshot).</param>
/// <param name="SourceChain">Provenance, outermost source first (e.g. folder → package → archive inside it).</param>
/// <param name="Views">Previews to show: model angles, an animation clip's animated WebP, else the thumbnail / waveform / Unity preview.</param>
public sealed record AssetDetails(
    Asset Asset, IReadOnlyList<Source> SourceChain, IReadOnlyList<TagView> Tags, IReadOnlyList<PreviewData> Views, bool HasAudioClip);

/// <summary>Reads the details of one asset and applies the edits made there.</summary>
public sealed class AssetDetailsService(CatalogStore catalog)
{
    public async Task<AssetDetails?> LoadAsync(long assetId, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.Assets.AsNoTracking().Include(a => a.Source).Include(a => a.Files).ThenInclude(f => f.Stored)
            .FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return null;

        var chain = new List<Source> { asset.Source };
        for (var parent = asset.Source.ParentSourceId; parent is { } id && chain.Count < 32;)
        {
            var s = await db.Sources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) break;
            chain.Insert(0, s);
            parent = s.ParentSourceId;
        }

        var tags = await db.Set<AssetTag>().AsNoTracking().Where(t => t.AssetId == assetId)
            .OrderBy(t => t.Tag.Name).Select(t => new TagView(t.Tag.Name, t.Origin)).ToListAsync(ct);

        var previews = await db.Previews.AsNoTracking()
            .Where(p => p.AssetId == assetId && p.Kind != PreviewKind.OrientationProbe)
            .Select(p => new { p.Kind, p.Index, p.Mime, Bytes = p.Kind == PreviewKind.AudioClip ? Array.Empty<byte>() : p.Bytes })
            .ToListAsync(ct);
        var views = previews.Where(p => p.Kind == PreviewKind.ModelView).OrderBy(p => p.Index).ToList();
        if (views.Count == 0) views = previews.Where(p => p.Kind == PreviewKind.Animation).ToList();
        if (views.Count == 0)
            views = previews.Where(p => p.Kind is PreviewKind.Thumbnail or PreviewKind.Waveform or PreviewKind.UnityPreview)
                .OrderBy(p => p.Kind == PreviewKind.Thumbnail ? 0 : p.Kind == PreviewKind.Waveform ? 1 : 2).ToList();

        return new AssetDetails(asset, chain, tags, views.Select(p => new PreviewData(p.Mime, p.Bytes)).ToList(),
            previews.Any(p => p.Kind == PreviewKind.AudioClip));
    }

    /// <summary>
    /// The user's upright rotation (multiples of 90° about X, Y, Z). Recorded as decided by the user, so later AI runs
    /// and re-imports keep it; the previews must be re-rendered afterwards.
    /// </summary>
    public async Task SetOrientationAsync(long assetId, int rotX, int rotY, int rotZ, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var a = await db.AllAssets.FirstAsync(x => x.Id == assetId, ct);
        if (a.Model is null) throw new InvalidOperationException("Only 3D models have an orientation.");
        a.Model = a.Model with
        {
            Orientation = new ModelOrientation
            {
                RotX = Normalize(rotX), RotY = Normalize(rotY), RotZ = Normalize(rotZ),
                DecidedBy = OrientationDecidedBy.User, Confidence = 1, Verified = true,
            },
        };
        a.OrientationUncertain = false;
        a.OrientationVerified = true;
        a.UserEdited |= EditedFields.Orientation;
        a.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        catalog.NotifyContentChanged();
    }

    private static int Normalize(int degrees) => ((degrees / 90 * 90) % 360 + 360) % 360;
}
