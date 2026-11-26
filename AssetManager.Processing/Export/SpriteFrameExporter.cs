using AssetManager.Core.Domain;
using AssetManager.Data;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Models;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Processing.Export;

/// <summary>
/// Sprite frame export (spec "Frame export", design D5): frames are cut from the original at their full size. Single
/// frames become PNG files named after the sheet plus the frame number (or the slicing data's name); a row or range
/// becomes one looping animated WebP.
/// </summary>
public sealed class SpriteFrameExporter(CatalogStore catalog, AssetExporter exporter)
{
    /// <summary>Writes the given frames (0-based indexes into the sheet's frames) as PNG files; returns their paths.</summary>
    public async Task<IReadOnlyList<string>> ExportPngsAsync(long assetId, IReadOnlyList<int> frames, string folder, CancellationToken ct = default)
    {
        var (asset, sheet) = await LoadAsync(assetId, ct);
        var picked = Pick(sheet, frames);
        Directory.CreateDirectory(folder);
        using var file = await exporter.ResolvePrimaryAsync(assetId, ct);
        return await Task.Run(() =>
        {
            using var image = ImageHandler.Load(file.Path, Path.GetExtension(asset.PrimaryFile?.FileName ?? file.Path).ToLowerInvariant());
            var digits = Math.Max(2, sheet.Frames.Count.ToString().Length);
            var written = new List<string>();
            foreach (var (frame, index) in picked)
            {
                ct.ThrowIfCancellationRequested();
                using var cut = Crop(image, frame);
                var name = frame.Name is { Length: > 0 } n ? Safe(n) : $"{Safe(asset.Name)}_{(index + 1).ToString().PadLeft(digits, '0')}";
                var path = Free(Path.Combine(folder, name + ".png"));
                cut.Write(path, MagickFormat.Png);
                written.Add(path);
            }
            return (IReadOnlyList<string>)written;
        }, ct);
    }

    /// <summary>Writes the given frames as one looping animated WebP at <paramref name="fps"/>; returns its path.</summary>
    public async Task<string> ExportAnimationAsync(long assetId, IReadOnlyList<int> frames, int fps, string path, CancellationToken ct = default)
    {
        var (asset, sheet) = await LoadAsync(assetId, ct);
        var picked = Pick(sheet, frames);
        using var file = await exporter.ResolvePrimaryAsync(assetId, ct);
        return await Task.Run(() =>
        {
            using var image = ImageHandler.Load(file.Path, Path.GetExtension(asset.PrimaryFile?.FileName ?? file.Path).ToLowerInvariant());
            // Atlas frames may differ in size: each is centred on a canvas of the largest one.
            var w = picked.Max(p => p.Frame.W);
            var h = picked.Max(p => p.Frame.H);
            var bytes = picked.Select(p =>
            {
                using var cut = Crop(image, p.Frame);
                using var canvas = new MagickImage(MagickColors.Transparent, (uint)w, (uint)h);
                canvas.Composite(cut, (w - p.Frame.W) / 2, (h - p.Frame.H) / 2, CompositeOperator.Over);
                return canvas.ToByteArray(MagickFormat.Png);
            }).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var target = Free(path);
            File.WriteAllBytes(target, AnimatedWebp.Encode(new AnimationFrames(w, h, Math.Clamp(fps, 1, 30), bytes), 90));
            return target;
        }, ct);
    }

    private async Task<(Asset Asset, SpriteSheetMetadata Sheet)> LoadAsync(long assetId, CancellationToken ct)
    {
        await using var db = catalog.CreateDbContext();
        var asset = await db.Assets.AsNoTracking().Include(a => a.Files).FirstOrDefaultAsync(a => a.Id == assetId, ct)
                    ?? throw new InvalidOperationException("The asset is no longer in the catalog.");
        if (asset.SpriteSheet is not { Frames.Count: > 0 } sheet) throw new InvalidOperationException($"{asset.Name} has no sprite frames.");
        return (asset, sheet);
    }

    private static List<(SpriteFrame Frame, int Index)> Pick(SpriteSheetMetadata sheet, IReadOnlyList<int> frames)
    {
        var picked = frames.Distinct().Where(i => i >= 0 && i < sheet.Frames.Count).Select(i => (sheet.Frames[i], i)).ToList();
        if (picked.Count == 0) throw new InvalidOperationException("No frames selected.");
        return picked;
    }

    private static MagickImage Crop(MagickImage image, SpriteFrame f)
    {
        var cut = (MagickImage)image.Clone();
        cut.Crop(new MagickGeometry(f.X, f.Y, (uint)f.W, (uint)f.H));
        cut.ResetPage();
        return cut;
    }

    /// <summary>The path itself when free, else the first free "name (n)" variant.</summary>
    private static string Free(string path) => File.Exists(path) ? AssetExporter.FreeName(path) : path;

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "frame" : s;
    }
}
