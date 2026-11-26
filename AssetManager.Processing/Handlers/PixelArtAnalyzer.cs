using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;
using ImageMagick;

namespace AssetManager.Processing.Handlers;

/// <summary>The measured pixel-art properties of an image (design D4).</summary>
/// <param name="Scale">Nearest-neighbour upscale factor (1 = native pixels).</param>
/// <param name="Colors">Distinct colours among opaque pixels of the analysed area.</param>
/// <param name="PaletteShare">Share of opaque pixels whose colour is one of the most common <c>PixelArtPaletteSize</c> colours.</param>
public sealed record PixelArtResult(int Scale, int Colors, double PaletteShare, bool IsPixelArt);

/// <summary>
/// Measured pixel-art detection (design D4, spec "Pixel-art detection"). Works on the full-resolution image, limited to a
/// centred area of at most 1024 × 1024 so whole pixel blocks stay intact:
/// <list type="bullet">
/// <item>Pixel scale k: art upscaled k× with nearest neighbour only changes colour every k pixels, so k is the largest
/// value in 1..16 for which nearly all colour changes between neighbours (≥ <c>PixelArtGridShare</c>) lie on one k-pixel
/// grid, in both directions.</item>
/// <item>Hard edges and a limited palette: anti-aliased or painted art has many rare blend colours, so the share of opaque
/// pixels in the top-N palette is low.</item>
/// </list>
/// Pixel art = (k ≥ 2 and palette-bounded) or (k = 1, at most N colours, palette-bounded and max edge ≤ <c>PixelArtNativeMaxEdge</c>).
/// </summary>
public static class PixelArtAnalyzer
{
    public const int MaxScale = 16;
    private const int Window = 1024;
    private const int MinEdges = 8;

    public static PixelArtResult Analyze(MagickImage image, PreviewOptions options)
    {
        int w = (int)image.Width, h = (int)image.Height;
        int cw = Math.Min(w, Window), ch = Math.Min(h, Window);
        using var area = (MagickImage)image.Clone();
        if (cw < w || ch < h)
        {
            area.Crop(new MagickGeometry((w - cw) / 2, (h - ch) / 2, (uint)cw, (uint)ch));
            area.ResetPage();
        }
        var rgba = area.GetPixelsUnsafe().ToByteArray(PixelMapping.RGBA) ?? [];
        var px = new uint[cw * ch];
        for (var i = 0; i < px.Length; i++)
        {
            var a = rgba[i * 4 + 3];
            px[i] = a == 0 ? 0u : (uint)(rgba[i * 4] << 24 | rgba[i * 4 + 1] << 16 | rgba[i * 4 + 2] << 8 | a);
        }

        // Colour changes between horizontal and vertical neighbours, by boundary position.
        var dx = new long[cw];
        var dy = new long[ch];
        for (var y = 0; y < ch; y++)
        {
            var row = y * cw;
            for (var x = 1; x < cw; x++)
                if (px[row + x] != px[row + x - 1]) dx[x]++;
            if (y > 0)
                for (var x = 0; x < cw; x++)
                    if (px[row + x] != px[row - cw + x]) dy[y]++;
        }
        var scale = Scale(dx, dy, options.PixelArtGridShare);
        var measurable = dx.Sum() + dy.Sum() >= MinEdges; // a flat fill shows no pixels to judge

        // Palette: exact counts over opaque pixels.
        var counts = new Dictionary<uint, int>();
        var opaque = 0;
        foreach (var p in px)
        {
            if (p == 0) continue;
            opaque++;
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        var top = counts.Values.OrderByDescending(c => c).Take(options.PixelArtPaletteSize).Sum();
        var share = opaque == 0 ? 0 : (double)top / opaque;
        var bounded = opaque > 0 && share >= options.PixelArtPaletteShare;
        var pixelArt = options.PixelArtDetection && bounded && measurable &&
                       (scale >= 2 || (counts.Count <= options.PixelArtPaletteSize && Math.Max(w, h) <= options.PixelArtNativeMaxEdge));
        return new PixelArtResult(scale, counts.Count, share, pixelArt);
    }

    /// <summary>The largest k whose grid (at some offset) holds at least <paramref name="share"/> of the colour changes on both axes.</summary>
    internal static int Scale(long[] dx, long[] dy, double share)
    {
        long tx = dx.Sum(), ty = dy.Sum();
        if (tx + ty < MinEdges) return 1; // flat image: nothing to measure
        for (var k = MaxScale; k >= 2; k--)
            if (OnGrid(dx, tx, k, share) && OnGrid(dy, ty, k, share)) return k;
        return 1;
    }

    private static bool OnGrid(long[] d, long total, int k, double share)
    {
        if (total == 0) return true;
        if (d.Length < 2 * k) return false; // too small to show a k-pixel grid
        for (var offset = 0; offset < k; offset++)
        {
            long on = 0;
            for (var i = offset; i < d.Length; i += k) on += d[i];
            if (on >= share * total) return true;
        }
        return false;
    }
}

/// <summary>
/// Keeps the "pixel art" style and tag in line with the measurement (design D4): a measured positive adds them (unless
/// the user edited the style), a measured negative removes them even when the AI says otherwise. Assets without image
/// metadata (models, audio) are left to the AI.
/// </summary>
public static class PixelArtRules
{
    public const string Name = "pixel art";

    public static bool Measured(Asset asset) => asset.Image is not null && asset.Type is AssetType.Image or AssetType.Texture or AssetType.SpriteSheet;

    /// <summary>The style list with "pixel art" added or removed by the measurement.</summary>
    public static List<string> Style(Asset asset, IEnumerable<string> style) => Reconcile(asset, style);

    /// <summary>The tag list with "pixel art" added or removed by the measurement.</summary>
    public static List<string> Tags(Asset asset, IEnumerable<string> tags) => Reconcile(asset, tags);

    private static List<string> Reconcile(Asset asset, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (!Measured(asset)) return list;
        list.RemoveAll(v => string.Equals(v, Name, StringComparison.OrdinalIgnoreCase));
        if (asset.Image!.IsPixelArt && !asset.UserEdited.HasFlag(EditedFields.Style)) list.Insert(0, Name);
        return list;
    }
}
