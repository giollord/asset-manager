using AssetManager.Core.Domain;
using AssetManager.Processing.Settings;
using ImageMagick;

namespace AssetManager.Processing.Handlers;

/// <summary>
/// Images, textures and sprite sheets via Magick.NET (PNG, JPG, TGA, PSD, EXR, DDS, SVG, GIF…). Stores width, height,
/// alpha, format, pixel-art flag and a dominant palette; the preview is a lossy WebP with alpha, the original for
/// small web images, or a lossless WebP for small non-web images.
/// </summary>
public sealed class ImageHandler : AssetTypeHandlerBase
{
    private static readonly HashSet<string> WebFormats = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    public override string Name => "image";

    public override bool CanHandle(AssetType type) => type is AssetType.Image or AssetType.Texture or AssetType.SpriteSheet;

    public override Task ExtractMetadataAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            using var image = Load(item.PrimaryPath, item.Extension);
            var colors = CountColors(image);
            var hasAlpha = image.HasAlpha && !image.IsOpaque;
            var pixelArt = options.PixelArtDetection ? PixelArtAnalyzer.Analyze(image, options) : null;
            item.Asset.Image = new ImageMetadata
            {
                Width = (int)image.Width,
                Height = (int)image.Height,
                HasAlpha = hasAlpha,
                Format = FormatName(image, item.Extension),
                ColorCount = colors,
                IsPixelArt = pixelArt?.IsPixelArt ?? false,
                PixelScale = pixelArt?.Scale,
                Frames = item.Extension == ".gif" ? CountFrames(item.PrimaryPath) : null,
            };
            if (!item.Asset.UserEdited.HasFlag(EditedFields.Colors))
                item.Asset.Colors = Palette(image, options.PaletteColors);
            if (!item.Asset.UserEdited.HasFlag(EditedFields.Style))
                item.Asset.Style = PixelArtRules.Style(item.Asset, item.Asset.Style);
            // Sprite sheet (design D3): slicing data next to the file, else the grid detector. User edits are final.
            Sprites.SpriteSheetClassifier.Detect(item.Asset, item.PrimaryPath, image, options);
        }, ct);

    public override Task<IReadOnlyList<PreviewOutput>> GeneratePreviewsAsync(HandlerItem item, PreviewOptions options, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<PreviewOutput>>(() =>
        {
            var size = new FileInfo(item.PrimaryPath).Length;
            using var image = Load(item.PrimaryPath, item.Extension);
            var maxEdge = Math.Max(image.Width, image.Height);
            var small = maxEdge <= options.KeepAsIsMaxDimension && size <= options.KeepAsIsMaxKb * 1024L;

            if (small && WebFormats.Contains(item.Extension) && IsSingleFrame(item))
            {
                var bytes = File.ReadAllBytes(item.PrimaryPath);
                return [new PreviewOutput(PreviewKind.Thumbnail, 0, MimeOf(item.Extension), (int)image.Width, (int)image.Height, bytes)];
            }

            if (small)
            {
                // Not displayable in the WebView (TGA, PSD, DDS…): lossless re-encode.
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
                return [Encode(image, PreviewKind.Thumbnail, 100)];
            }

            var pixelArt = item.Asset.Image?.IsPixelArt ?? false;
            var sheet = SheetPreview(item.Asset, image, options, pixelArt);
            if (maxEdge > options.ImageMaxEdge)
            {
                var geometry = new MagickGeometry((uint)options.ImageMaxEdge, (uint)options.ImageMaxEdge);
                if (pixelArt) image.FilterType = FilterType.Point; // keep hard pixel edges
                image.Resize(geometry);
            }
            var thumbnail = Encode(image, PreviewKind.Thumbnail, options.ImageQuality);
            return sheet is null ? [thumbnail] : [thumbnail, sheet];
        }, ct);

    /// <summary>Smallest legible frame edge in the stored sprite-sheet preview (design D5).</summary>
    public const int MinCellPreviewPx = 64;

    /// <summary>
    /// A second, larger preview of a sprite sheet whose thumbnail would show fewer than 64 px per cell, so frames stay
    /// legible offline: at least 64 px per cell, capped at 4096 px and at the original size.
    /// </summary>
    internal static PreviewOutput? SheetPreview(Asset asset, MagickImage image, PreviewOptions options, bool pixelArt)
    {
        if (asset.SpriteSheet is not { Frames.Count: > 0 } sheet) return null;
        var maxEdge = (double)Math.Max(image.Width, image.Height);
        var cell = sheet.Frames.Min(f => Math.Min(f.W, f.H));
        var thumbScale = Math.Min(1, options.ImageMaxEdge / maxEdge);
        if (cell * thumbScale >= MinCellPreviewPx) return null;
        var scale = Math.Min(1, Math.Min((double)MinCellPreviewPx / Math.Max(1, cell), 4096 / maxEdge));
        if (scale <= thumbScale) return null; // no sharper than the thumbnail
        using var copy = (MagickImage)image.Clone();
        if (scale < 1)
        {
            if (pixelArt) copy.FilterType = FilterType.Point;
            copy.Resize((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale)));
        }
        if (pixelArt) copy.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
        return Encode(copy, PreviewKind.SpriteSheet, pixelArt ? 100 : options.ImageQuality);
    }

    /// <summary>Encodes as lossy WebP keeping the alpha channel.</summary>
    internal static PreviewOutput Encode(MagickImage image, PreviewKind kind, int quality, int index = 0)
    {
        image.Quality = (uint)quality;
        image.Settings.SetDefine(MagickFormat.WebP, "alpha-quality", 100);
        var bytes = image.ToByteArray(MagickFormat.WebP);
        return new PreviewOutput(kind, index, "image/webp", (int)image.Width, (int)image.Height, bytes);
    }

    internal static MagickImage Load(string path, string extension)
    {
        var settings = new MagickReadSettings { FrameIndex = 0, FrameCount = 1 };
        if (extension is ".tga") settings.Format = MagickFormat.Tga; // TGA has no magic number
        else if (extension is ".dds") settings.Format = MagickFormat.Dds;
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (extension is ".exr" or ".hdr")
        {
            // Simple tone mapping of linear HDR data for display.
            image.ColorSpace = ColorSpace.sRGB;
            image.AutoLevel();
            image.AutoGamma();
        }
        if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.Gray && extension is ".psd" or ".tif" or ".tiff" or ".jpg" or ".jpeg")
            image.TransformColorSpace(ColorProfiles.SRGB);
        return image;
    }

    private bool IsSingleFrame(HandlerItem item) => item.Extension != ".gif" || (item.Asset.Image?.Frames ?? 1) <= 1;

    private static int CountColors(MagickImage image)
    {
        if (Math.Max(image.Width, image.Height) <= 512) return (int)image.TotalColors;
        using var sample = (MagickImage)image.Clone();
        sample.Sample(new MagickGeometry(512, 512)); // nearest neighbour keeps the palette
        return (int)sample.TotalColors;
    }

    private static int? CountFrames(string path)
    {
        try { return MagickImageInfo.ReadCollection(path).Count(); }
        catch (MagickException) { return null; }
    }

    private static string FormatName(MagickImage image, string extension) =>
        image.Format is MagickFormat.Unknown ? extension.TrimStart('.').ToUpperInvariant() : image.Format.ToString().ToUpperInvariant();

    private static string MimeOf(string ext) => ext switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    /// <summary>Dominant colors by quantization, ignoring transparent pixels; weights sum to 1.</summary>
    public static List<ColorInfo> Palette(MagickImage image, int count)
    {
        using var small = (MagickImage)image.Clone();
        small.Sample(new MagickGeometry(128, 128)); // nearest neighbour: no blended edge colors
        small.Quantize(new QuantizeSettings { Colors = (uint)Math.Max(1, count), DitherMethod = DitherMethod.No });
        var histogram = small.Histogram()
            .Where(h => !small.HasAlpha || h.Key.A > 127)
            .GroupBy(h => $"#{h.Key.R:X2}{h.Key.G:X2}{h.Key.B:X2}")
            .Select(g => (Hex: g.Key, Count: g.Sum(x => (double)x.Value), Color: g.First().Key))
            .OrderByDescending(x => x.Count)
            .Take(count)
            .ToList();
        // Drop tiny clusters (anti-aliased edges) so the palette lists distinct dominant colors.
        var all = histogram.Sum(h => h.Count);
        histogram = histogram.Where((h, i) => i == 0 || h.Count / all >= 0.02).ToList();
        var total = histogram.Sum(h => h.Count);
        return histogram.Select(h => new ColorInfo(h.Hex, ColorNames.Nearest(h.Color.R, h.Color.G, h.Color.B), Math.Round(h.Count / total, 3))).ToList();
    }
}
