using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetManager.Processing.Handlers;

public sealed record ProcessResult(IReadOnlyList<PreviewOutput> Previews, string? Error);

/// <summary>
/// Runs an asset through its handler: metadata, then previews, then the fallback chain when no preview could be made
/// (Unity's embedded preview.png → no blob, so the UI shows the type placeholder). Failures never stop the import;
/// the reason is recorded in <see cref="Asset.PreviewError"/>.
/// </summary>
public sealed class AssetProcessor(HandlerRegistry registry, ILogger<AssetProcessor>? logger = null)
{
    private readonly ILogger _log = logger ?? (ILogger)NullLogger.Instance;

    public async Task<ProcessResult> ProcessAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default)
    {
        var handler = registry.For(item.Asset.Type);
        var errors = new List<string>();
        try
        {
            await handler.ExtractMetadataAsync(item, options, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Metadata failed for {File}", item.FileName);
            errors.Add($"Metadata: {e.Message}");
        }

        // The type may have been refined (e.g. audio → music); use the matching handler for previews.
        handler = registry.For(item.Asset.Type);
        IReadOnlyList<PreviewOutput> previews = [];
        if (errors.Count == 0)
        {
            try
            {
                previews = await handler.GeneratePreviewsAsync(item, options, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "Preview failed for {File}", item.FileName);
                errors.Add($"Preview: {e.Message}");
            }
        }

        if (!previews.Any(p => p.Kind is PreviewKind.Thumbnail or PreviewKind.ModelView or PreviewKind.Waveform) &&
            item.UnityPreviewPath is { } unity && File.Exists(unity))
        {
            var bytes = await File.ReadAllBytesAsync(unity, ct);
            var (w, h) = PngSize(bytes);
            previews = [.. previews, new PreviewOutput(PreviewKind.UnityPreview, 0, "image/png", w, h, bytes)];
        }

        item.Asset.PreviewError = errors.Count == 0 ? null : string.Join(" ", errors);
        item.Asset.PreviewOptionsVersion = options.Version();
        return new ProcessResult(previews, item.Asset.PreviewError);
    }

    /// <summary>Replaces all stored previews of an asset with <paramref name="previews"/>.</summary>
    public static async Task SavePreviewsAsync(CatalogDbContext db, long assetId, IReadOnlyList<PreviewOutput> previews, int optionsVersion, CancellationToken ct = default)
    {
        await db.Previews.Where(p => p.AssetId == assetId).ExecuteDeleteAsync(ct);
        foreach (var p in previews)
            db.Previews.Add(new PreviewBlob
            {
                AssetId = assetId, Kind = p.Kind, Index = p.Index, Mime = p.Mime, Width = p.Width, Height = p.Height, Bytes = p.Bytes,
                OptionsVersion = optionsVersion,
            });
        await db.SaveChangesAsync(ct);
    }

    private static (int W, int H) PngSize(byte[] b) =>
        b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50
            ? ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23])
            : (0, 0);
}
