using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Fixtures;
using ImageMagick;

namespace AssetManager.Tests.Processing;

public class ImageHandlerTests
{
    private static readonly AssetProcessor Processor = new(new HandlerRegistry([new ImageHandler(), new FallbackHandler()]));

    internal static HandlerItem Item(string path, AssetType type = AssetType.Image) => new()
    {
        Asset = new Asset
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Type = type,
            Files = [new AssetFile { Role = FileRole.Primary, FileName = Path.GetFileName(path), Extension = Path.GetExtension(path) }],
        },
        PrimaryPath = path,
    };

    /// <summary>A gradient with a transparent-to-opaque alpha ramp (many colors, so not pixel art).</summary>
    private static string Gradient(TempDir dir, string name, uint size, MagickFormat format)
    {
        using var img = new MagickImage("gradient:red-blue", size, size);
        img.Alpha(AlphaAction.Set);
        using var alpha = new MagickImage("gradient:white-black", size, size);
        img.Composite(alpha, CompositeOperator.CopyAlpha);
        var path = Path.Combine(dir.Path, name);
        img.Write(path, format);
        return path;
    }

    private static MagickImage Decode(PreviewOutput p) => new(p.Bytes);

    [Fact]
    public async Task Large_texture_gets_512px_lossy_webp_with_alpha()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = Gradient(dir, "big.png", 4096, MagickFormat.Png);
        var item = Item(path, AssetType.Texture);

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Null(result.Error);
        var thumb = Assert.Single(result.Previews);
        Assert.Equal(PreviewKind.Thumbnail, thumb.Kind);
        Assert.Equal("image/webp", thumb.Mime);
        Assert.Equal(512, Math.Max(thumb.Width, thumb.Height));
        Assert.Contains("VP8 "u8.ToArray(), Chunks(thumb.Bytes)); // lossy bitstream
        Assert.Contains("ALPH"u8.ToArray(), Chunks(thumb.Bytes)); // separate alpha
        using var decoded = Decode(thumb);
        Assert.True(decoded.HasAlpha && !decoded.IsOpaque);
        Assert.Equal(4096, item.Asset.Image!.Width);
        Assert.True(item.Asset.Image.HasAlpha);
        Assert.False(item.Asset.Image.IsPixelArt);
        Assert.Equal("PNG", item.Asset.Image.Format);
        Assert.Equal(new PreviewOptions().Version(), item.Asset.PreviewOptionsVersion);
    }

    [Fact]
    public async Task Small_sprite_is_stored_unchanged()
    {
        using var dir = FixtureBuilder.TempDir();
        using (var img = new MagickImage(MagickColors.Transparent, 32, 32))
        {
            img.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Green).Rectangle(8, 8, 23, 23));
            img.Write(Path.Combine(dir.Path, "sprite.png"), MagickFormat.Png);
        }
        var path = Path.Combine(dir.Path, "sprite.png");

        var result = await Processor.ProcessAsync(Item(path), new PreviewOptions());

        var thumb = Assert.Single(result.Previews);
        Assert.Equal("image/png", thumb.Mime);
        Assert.Equal(await File.ReadAllBytesAsync(path), thumb.Bytes);
        Assert.Equal(32, thumb.Width);
    }

    [Theory]
    [InlineData("layers.psd", 64u)]
    [InlineData("old.tga", 64u)]
    [InlineData("big.tga", 2048u)]
    [InlineData("tex.dds", 256u)]
    [InlineData("sky.exr", 128u)]
    public async Task Non_web_formats_get_a_displayable_preview(string name, uint size)
    {
        using var dir = FixtureBuilder.TempDir();
        var format = Path.GetExtension(name) switch { ".psd" => MagickFormat.Psd, ".tga" => MagickFormat.Tga, ".dds" => MagickFormat.Dds, _ => MagickFormat.Exr };
        var path = Gradient(dir, name, size, format);

        var result = await Processor.ProcessAsync(Item(path), new PreviewOptions());

        Assert.Null(result.Error);
        var thumb = Assert.Single(result.Previews);
        Assert.Equal("image/webp", thumb.Mime);
        using var decoded = Decode(thumb);
        Assert.Equal(MagickFormat.WebP, decoded.Format);
        Assert.Equal((int)Math.Min(size, 512), (int)decoded.Width);
    }

    [Fact]
    public async Task Change_keep_as_is_threshold()
    {
        using var dir = FixtureBuilder.TempDir();
        using (var img = new MagickImage(MagickColors.Orange, 400, 400)) img.Write(Path.Combine(dir.Path, "p.png"), MagickFormat.Png);
        var path = Path.Combine(dir.Path, "p.png");

        var kept = await Processor.ProcessAsync(Item(path), new PreviewOptions());
        Assert.Equal("image/png", Assert.Single(kept.Previews).Mime);

        var lossy = await Processor.ProcessAsync(Item(path), new PreviewOptions { KeepAsIsMaxDimension = 256 });
        var thumb = Assert.Single(lossy.Previews);
        Assert.Equal("image/webp", thumb.Mime);
        Assert.Contains("VP8 "u8.ToArray(), Chunks(thumb.Bytes));
    }

    [Fact]
    public async Task Pixel_art_downscales_with_nearest_neighbour_and_palette_is_named()
    {
        using var dir = FixtureBuilder.TempDir();
        using (var img = new MagickImage(MagickColors.Red, 1024, 1024))
        {
            img.Draw(new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#2050D0")).Rectangle(512, 0, 1023, 1023));
            img.Write(Path.Combine(dir.Path, "sheet.png"), MagickFormat.Png);
        }
        var item = Item(Path.Combine(dir.Path, "sheet.png"));

        var result = await Processor.ProcessAsync(item, new PreviewOptions { ImageQuality = 100 });

        Assert.True(item.Asset.Image!.IsPixelArt);
        using var decoded = Decode(Assert.Single(result.Previews));
        Assert.Equal(512u, decoded.Width);
        Assert.True(decoded.TotalColors <= 8, $"nearest-neighbour keeps hard edges ({decoded.TotalColors} colors)");
        Assert.Equal(["red", "blue"], item.Asset.Colors.Select(c => c.Name).Order().Reverse());
        Assert.All(item.Asset.Colors, c => Assert.InRange(c.Weight, 0.45, 0.55));
    }

    [Fact]
    public async Task Unreadable_image_records_error_and_falls_back_to_unity_preview()
    {
        using var dir = FixtureBuilder.TempDir();
        var path = dir.Write("broken.png", "not an image");
        var unity = dir.Write("preview.png", FixtureBuilder.Zip()); // any bytes; stored as-is
        var item = new HandlerItem { Asset = Item(path).Asset, PrimaryPath = path, UnityPreviewPath = unity };

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.NotNull(result.Error);
        Assert.Equal(PreviewKind.UnityPreview, Assert.Single(result.Previews).Kind);
        Assert.Equal(result.Error, item.Asset.PreviewError);
    }

    /// <summary>RIFF chunk ids in a WebP file (including those nested in VP8X).</summary>
    private static List<byte[]> Chunks(byte[] webp)
    {
        var ids = new List<byte[]>();
        for (var i = 12; i + 8 <= webp.Length;)
        {
            ids.Add(webp[i..(i + 4)]);
            var len = BitConverter.ToInt32(webp, i + 4);
            i += 8 + len + (len & 1);
        }
        return ids;
    }

    // ---- measured pixel art (design D4, spec "Pixel-art detection") ----

    /// <summary>Random <paramref name="art"/>-pixel art from <paramref name="colors"/> palette colours, with transparent pixels, upscaled k× with nearest neighbour.</summary>
    internal static MagickImage PixelArt(int art, int k, int colors, int seed = 1)
    {
        var rng = new Random(seed);
        var palette = Enumerable.Range(0, colors).Select(_ => new MagickColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))).ToArray();
        var img = new MagickImage(MagickColors.Transparent, (uint)art, (uint)art);
        var draw = new ImageMagick.Drawing.Drawables();
        for (var y = 0; y < art; y++)
            for (var x = 0; x < art; x++)
                if (rng.Next(5) > 0) draw.FillColor(palette[rng.Next(colors)]).Point(x, y);
        draw.Draw(img);
        if (k > 1)
        {
            img.FilterType = FilterType.Point;
            img.Resize((uint)(art * k), (uint)(art * k));
        }
        return img;
    }

    private static async Task<ImageMetadata> MeasureAsync(TempDir dir, MagickImage image, string name)
    {
        var path = Path.Combine(dir.Path, name);
        image.Write(path, MagickFormat.Png);
        var item = Item(path);
        await Processor.ProcessAsync(item, new PreviewOptions());
        return item.Asset.Image!;
    }

    [Fact]
    public async Task Upscaled_16px_art_is_pixel_art_with_scale_4()
    {
        using var dir = FixtureBuilder.TempDir();
        using var img = PixelArt(16, 4, 12);
        var path = Path.Combine(dir.Path, "hero.png");
        img.Write(path, MagickFormat.Png);
        var item = Item(path);

        await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Equal((true, 4), (item.Asset.Image!.IsPixelArt, item.Asset.Image.PixelScale));
        Assert.Contains("pixel art", item.Asset.Style); // style set from the measurement
    }

    [Fact]
    public async Task Smooth_2048px_gradient_is_not_pixel_art()
    {
        using var dir = FixtureBuilder.TempDir();
        using var img = new MagickImage("gradient:#203060-#e0c080", 2048, 2048);
        img.Blur(0, 4);
        var meta = await MeasureAsync(dir, img, "painting.png");
        Assert.False(meta.IsPixelArt);
        Assert.Equal(1, meta.PixelScale);
    }

    [Fact]
    public async Task Native_32_colour_icon_is_pixel_art()
    {
        using var dir = FixtureBuilder.TempDir();
        using var img = PixelArt(32, 1, 32, seed: 7);
        var meta = await MeasureAsync(dir, img, "icon.png");
        Assert.Equal((true, 1), (meta.IsPixelArt, meta.PixelScale));
    }

    [Fact]
    public async Task Anti_aliased_upscale_is_not_pixel_art()
    {
        using var dir = FixtureBuilder.TempDir();
        using var img = PixelArt(16, 1, 12);
        img.FilterType = FilterType.Lanczos; // smooth resampling blends colours at every edge
        img.Resize(256, 256);
        var meta = await MeasureAsync(dir, img, "blurry.png");
        Assert.False(meta.IsPixelArt);
    }

    [Fact]
    public async Task User_edited_style_is_not_changed()
    {
        using var dir = FixtureBuilder.TempDir();
        using var img = PixelArt(16, 2, 8);
        var path = Path.Combine(dir.Path, "kept.png");
        img.Write(path, MagickFormat.Png);
        var item = Item(path);
        item.Asset.Style = ["retro"];
        item.Asset.UserEdited = EditedFields.Style;

        await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.True(item.Asset.Image!.IsPixelArt);
        Assert.Equal(["retro"], item.Asset.Style);
    }

    [Fact]
    public async Task Sheet_with_small_cells_gets_a_sharper_sprite_sheet_preview()
    {
        // 2048×128: 64×4 cells of 32 px; the 512 px thumbnail would show 8 px per cell.
        using var dir = FixtureBuilder.TempDir();
        using (var img = new MagickImage(MagickColors.Transparent, 2048, 128))
        {
            var d = new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Teal);
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 64; x++) d.Rectangle(x * 32 + 4, y * 32 + 4, x * 32 + 27, y * 32 + 27 - x % 5);
            d.Draw(img);
            img.Write(Path.Combine(dir.Path, "tiles.png"), MagickFormat.Png);
        }
        var item = Item(Path.Combine(dir.Path, "tiles.png"));

        var result = await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Equal(AssetType.SpriteSheet, item.Asset.Type);
        Assert.Equal((32, 64, 4), (item.Asset.SpriteSheet!.Grid!.CellW, item.Asset.SpriteSheet.Grid.Columns, item.Asset.SpriteSheet.Grid.Rows));
        Assert.Equal(512, result.Previews.Single(p => p.Kind == PreviewKind.Thumbnail).Width);
        var sheet = result.Previews.Single(p => p.Kind == PreviewKind.SpriteSheet);
        Assert.Equal((2048, 128), (sheet.Width, sheet.Height)); // ≥ 64 px per cell would upscale: kept at the original size

        // A thumbnail at the original size is already as sharp as it gets: no extra preview.
        Assert.DoesNotContain((await Processor.ProcessAsync(item, new PreviewOptions { ImageMaxEdge = 4096 })).Previews, p => p.Kind == PreviewKind.SpriteSheet);
    }
}
