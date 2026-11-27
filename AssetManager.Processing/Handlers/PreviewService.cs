using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Containers;
using AssetManager.Processing.Models;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Handlers;

public sealed record RegenerationResult(int Regenerated, IReadOnlyList<SkippedAsset> Skipped);

/// <summary>
/// Regenerates metadata and previews of catalogued assets with the current preview settings (originals or stored
/// copies must be reachable), and finds assets whose previews were made with older settings.
/// </summary>
public sealed class PreviewService(
    CatalogStore catalog, ILocationResolver resolver, AssetProcessor processor, PreviewSettings settings, ILogger<PreviewService>? logger = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Assets whose previews were generated with settings other than the current ones.</summary>
    public async Task<IReadOnlyList<long>> OutdatedAsync(CancellationToken ct = default)
    {
        var version = (await settings.GetAsync(ct)).Version();
        await using var db = catalog.CreateDbContext();
        return await db.Assets.Where(a => a.PreviewOptionsVersion != version).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
    }

    public async Task<RegenerationResult> RegenerateOutdatedAsync(IProgress<int>? progress = null, CancellationToken ct = default) =>
        await RegenerateAsync(await OutdatedAsync(ct), progress, ct);

    public async Task<RegenerationResult> RegenerateAsync(IReadOnlyCollection<long> assetIds, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var options = await settings.GetAsync(ct);
        var skipped = new List<SkippedAsset>();
        int done = 0, regenerated = 0;
        foreach (var id in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await ProcessOneAsync(id, options, ct);
            if (outcome.Skipped is { } s) skipped.Add(s);
            else if (outcome.Processed) regenerated++;
            progress?.Report(++done);
        }
        if (regenerated > 0) catalog.NotifyContentChanged();
        _log.LogInformation("Regenerated previews of {Count} assets; {Skipped} skipped", regenerated, skipped.Count);
        return new RegenerationResult(regenerated, skipped);
    }

    /// <summary>
    /// The GLB and textures of a model for the interactive viewer in the asset details, read from the original or
    /// the stored copy. Throws <see cref="OriginalUnavailableException"/> when neither can be reached.
    /// </summary>
    public async Task<ModelRenderRequest> ViewerRequestAsync(long assetId, CancellationToken ct = default)
    {
        var options = await settings.GetAsync(ct);
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.AsNoTracking().Include(a => a.Files).ThenInclude(f => f.Stored).FirstAsync(a => a.Id == assetId, ct);
        if (asset.Type is not (AssetType.Model3D or AssetType.Animation) || asset.PrimaryFile is null) throw new InvalidOperationException("Not a 3D model.");
        var leases = new List<ResolvedFile>();
        try
        {
            var primary = await resolver.ResolveAsync(asset.PrimaryFile, ct);
            leases.Add(primary);
            var companions = new List<CompanionInput>();
            foreach (var f in asset.Files.Where(f => f.Role == FileRole.Companion))
            {
                var local = await resolver.ResolveAsync(f, ct);
                leases.Add(local);
                companions.Add(new CompanionInput(f, local.Path, f.Material, f.Slot));
            }
            var item = new HandlerItem { Asset = asset, PrimaryPath = primary.Path, Companions = companions };
            return await Task.Run(() => ModelHandler.BuildRenderRequest(item, options, animated: asset.Type == AssetType.Animation), ct);
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }
    }

    public sealed record ProcessOutcome(bool Processed, SkippedAsset? Skipped, string? Name, string? PreviewError);

    /// <summary>
    /// The "pixel art" tag with origin AI follows the measurement (design D4): added for detected pixel art unless the user
    /// edited the style, and an AI-origin copy removed when the image measures as not pixel art.
    /// </summary>
    internal static async Task MarkPixelArtAsync(CatalogDbContext db, Asset asset, CancellationToken ct)
    {
        if (!PixelArtRules.Measured(asset)) return;
        if (asset.Image!.IsPixelArt)
        {
            if (asset.UserEdited.HasFlag(EditedFields.Style)) return;
            var tag = (await AssetManager.Data.Services.TagService.GetOrCreateAsync(db, [PixelArtRules.Name], ct)).Single();
            await db.SaveChangesAsync(ct);
            // Not on an asset the user took the tag off (per-asset opt-out, spec "Opt-out survives re-import").
            await db.Database.ExecuteSqlRawAsync("""
                INSERT OR IGNORE INTO AssetTags (AssetId, TagId, Origin) SELECT {0}, {1}, {2}
                WHERE NOT EXISTS (SELECT 1 FROM AssetTagOptOuts WHERE AssetId = {0} AND TagId = {1})
                """, [asset.Id, tag.Id, nameof(TagOrigin.AI)], ct);
        }
        else
            await db.AssetTags.Where(t => t.AssetId == asset.Id && t.Origin == TagOrigin.AI && t.Tag.Name == PixelArtRules.Name).ExecuteDeleteAsync(ct);
        await AssetManager.Data.Search.SearchIndexer.RefreshAsync(db, [asset.Id], ct);
    }

    /// <summary>
    /// Reads one asset's files (live, from a container or a stored copy), extracts metadata, renders previews and
    /// saves both in one transaction. Works for staged and committed assets. Does not raise catalog change events.
    /// </summary>
    public async Task<ProcessOutcome> ProcessOneAsync(long id, PreviewOptions options, CancellationToken ct = default)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.AllAssets.Include(a => a.Files).ThenInclude(f => f.Stored).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset?.PrimaryFile is null) return new ProcessOutcome(false, null, asset?.Name, null);

        var leases = new List<ResolvedFile>();
        try
        {
            var primary = await resolver.ResolveAsync(asset.PrimaryFile, ct);
            leases.Add(primary);
            var companions = new List<CompanionInput>();
            foreach (var f in asset.Files.Where(f => f.Role == FileRole.Companion))
            {
                var local = await resolver.ResolveAsync(f, ct);
                leases.Add(local);
                companions.Add(new CompanionInput(f, local.Path, f.Material, f.Slot));
            }

            var item = new HandlerItem { Asset = asset, PrimaryPath = primary.Path, Companions = companions };
            var result = await processor.ProcessAsync(item, options, ct);
            var previews = result.Previews.ToList();
            if (!previews.Any(p => p.Kind is PreviewKind.Thumbnail or PreviewKind.ModelView or PreviewKind.Waveform or PreviewKind.UnityPreview))
            {
                // Keep Unity's embedded preview from the import; the package itself is not re-read here.
                var unity = await db.Previews.AsNoTracking().Where(p => p.AssetId == id && p.Kind == PreviewKind.UnityPreview).ToListAsync(ct);
                previews.AddRange(unity.Select(u => new PreviewOutput(u.Kind, u.Index, u.Mime, u.Width, u.Height, u.Bytes)));
            }

            asset.UpdatedAt = DateTime.UtcNow;
            // Import runs this on every core at once: the save takes its turn (see CatalogStore.AcquireWriteAsync).
            using var turn = await catalog.AcquireWriteAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(ct);
            await AssetProcessor.SavePreviewsAsync(db, id, previews, options.Version(), ct);
            await MarkPixelArtAsync(db, asset, ct);
            await tx.CommitAsync(ct);
            return new ProcessOutcome(true, null, asset.Name, asset.PreviewError);
        }
        catch (OriginalUnavailableException e)
        {
            return new ProcessOutcome(false, new SkippedAsset(id, asset.Name, $"Original unavailable: {e.Missing}"), asset.Name, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ContainerReadException)
        {
            return new ProcessOutcome(false, new SkippedAsset(id, asset.Name, e.Message), asset.Name, null);
        }
        finally
        {
            foreach (var l in leases) l.Dispose();
        }
    }
}
