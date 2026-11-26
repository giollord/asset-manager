using AssetManager.Core.Domain;
using AssetManager.Processing.Sprites;
using ImageMagick;
using ImageMagick.Drawing;

namespace AssetManager.Tests.Processing;

/// <summary>Spec "Algorithmic grid detection" and "AI hints and uncertain grids" (design D3 step 2).</summary>
public class GridDetectorTests
{
    /// <summary>A 512×64 run cycle: eight different poses, each inside its 64×64 cell with transparent columns between.</summary>
    internal static MagickImage RunCycle()
    {
        var img = new MagickImage(MagickColors.Transparent, 512, 64);
        var d = new Drawables();
        for (var i = 0; i < 8; i++)
        {
            var x = i * 64;
            d.FillColor(MagickColors.SteelBlue).Ellipse(x + 32, 20, 10, 12, 0, 360); // head and body
            d.FillColor(MagickColors.SaddleBrown).Rectangle(x + 24 + i % 3, 30, x + 40 - i % 2, 46);
            d.FillColor(MagickColors.Black).Line(x + 30, 46, x + 18 + i * 3, 60).Line(x + 34, 46, x + 46 - i * 3, 60); // legs move
        }
        d.StrokeWidth(3).Draw(img);
        return img;
    }

    /// <summary>A 256×256 sheet of 4×4 frames of 64×64 that touch: each frame has its own background and a moving disc.</summary>
    internal static MagickImage Packed()
    {
        var rng = new Random(3);
        var img = new MagickImage(MagickColors.Black, 256, 256);
        var d = new Drawables();
        for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                int x = c * 64, y = r * 64, k = r * 4 + c;
                d.FillColor(new MagickColor((byte)rng.Next(40, 220), (byte)rng.Next(40, 220), (byte)rng.Next(40, 220))).Rectangle(x, y, x + 63, y + 63);
                d.FillColor(MagickColors.White).Circle(x + 20 + k * 1.5, y + 32, x + 20 + k * 1.5 + 9, y + 32);
            }
        d.Draw(img);
        return img;
    }

    [Fact]
    public void Run_cycle_with_gutters_has_eight_64px_frames()
    {
        using var img = RunCycle();
        var result = GridDetector.Detect(img);

        var sheet = Assert.IsType<SpriteSheetMetadata>(result.Sheet);
        Assert.Equal(new SpriteGrid { CellW = 64, CellH = 64, Columns = 8, Rows = 1 }, sheet.Grid);
        Assert.Equal(8, sheet.FrameCount);
        Assert.Equal(GridDetectedBy.Algorithm, sheet.DetectedBy);
        Assert.False(result.Uncertain);
        Assert.True(sheet.Confidence >= 0.6, $"confidence {sheet.Confidence}");
    }

    [Fact]
    public void Packed_sheet_without_gutters_has_4_by_4_cells_of_64px()
    {
        using var img = Packed();
        var sheet = GridDetector.Detect(img).Sheet!;
        Assert.Equal((64, 64, 4, 4), (sheet.Grid!.CellW, sheet.Grid.CellH, sheet.Grid.Columns, sheet.Grid.Rows));
        Assert.Equal(16, sheet.FrameCount);
    }

    [Fact]
    public void Empty_cells_are_skipped_and_the_offset_found()
    {
        // 3×2 grid of 40×40 cells starting at (4, 4) on a solid background; the last cell is empty.
        using var img = new MagickImage(new MagickColor("#204020"), 124, 84);
        var d = new Drawables().FillColor(MagickColors.Gold);
        for (var i = 0; i < 5; i++) d.Rectangle(4 + i % 3 * 40 + 6, 4 + i / 3 * 40 + 6, 4 + i % 3 * 40 + 30 + i, 4 + i / 3 * 40 + 34);
        d.Draw(img);

        var sheet = GridDetector.Detect(img).Sheet!;
        Assert.Equal((40, 40, 3, 2), (sheet.Grid!.CellW, sheet.Grid.CellH, sheet.Grid.Columns, sheet.Grid.Rows));
        Assert.Equal(5, sheet.FrameCount);
        Assert.Equal(new SpriteFrame(null, sheet.Grid.OffsetX, sheet.Grid.OffsetY, 40, 40), sheet.Frames[0]);
        Assert.InRange(sheet.Grid.OffsetX, 0, 9); // borders inside the background around the sprites
    }

    [Fact]
    public void Irregular_atlas_is_uncertain_with_no_grid()
    {
        using var img = new MagickImage(MagickColors.Transparent, 256, 64);
        new Drawables().FillColor(MagickColors.Crimson)
            .Rectangle(2, 10, 21, 50).Rectangle(26, 4, 85, 60).Rectangle(90, 20, 109, 40).Rectangle(114, 2, 249, 62).Draw(img);

        var result = GridDetector.Detect(img);

        Assert.Null(result.Sheet);
        Assert.True(result.Uncertain);
    }

    [Fact]
    public void Seamless_texture_has_no_grid_and_is_not_uncertain()
    {
        using var img = new MagickImage("xc:#808060", 256, 256);
        img.AddNoise(NoiseType.Gaussian, 2);
        img.Blur(0, 3);

        Assert.Equal(GridResult.None, GridDetector.Detect(img));
    }

    [Fact]
    public void Pixel_art_sheet_at_4x_has_64px_cells()
    {
        // 1024×256: 16×4 sprites of 16×16 art pixels (1 px transparent margin), drawn at 4× with nearest neighbour.
        var rng = new Random(5);
        var palette = Enumerable.Range(0, 24).Select(_ => new MagickColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))).ToArray();
        using var art = new MagickImage(MagickColors.Transparent, 256, 64);
        var d = new Drawables();
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 256; x++)
                if (x % 16 is > 0 and < 15 && y % 16 is > 0 and < 15) d.FillColor(palette[rng.Next(24)]).Point(x, y);
        d.Draw(art);
        art.FilterType = FilterType.Point;
        art.Resize(1024, 256);

        var sheet = GridDetector.Detect(art).Sheet!;
        Assert.Equal((64, 64, 16, 4), (sheet.Grid!.CellW, sheet.Grid.CellH, sheet.Grid.Columns, sheet.Grid.Rows));
    }

    [Fact]
    public void Hint_alone_does_not_make_a_grid()
    {
        using var img = new MagickImage("xc:#808060", 256, 256);
        img.AddNoise(NoiseType.Gaussian, 2);
        img.Blur(0, 3);

        var result = GridDetector.Detect(img, hint: new SpriteSheetHint(true, Columns: 4, Rows: 4));

        Assert.Null(result.Sheet); // the hint is only a candidate; nothing in the pixels confirms it
        Assert.True(result.Uncertain);
    }

    [Fact]
    public void Large_sheets_are_analysed_downsampled_and_scaled_back()
    {
        using var small = RunCycle();
        small.FilterType = FilterType.Point;
        small.Resize(8192, 1024); // 16× : cells of 1024×1024
        var sheet = GridDetector.Detect(small).Sheet!;
        Assert.Equal((1024, 1024, 8, 1), (sheet.Grid!.CellW, sheet.Grid.CellH, sheet.Grid.Columns, sheet.Grid.Rows));
    }

    /// <summary>A single 64 px top-down wall tile with three regular mortar lines (Castle Kit): not a 1×4 sheet.</summary>
    [Fact]
    public void Lines_inside_a_single_tile_are_not_a_packed_grid()
    {
        using var img = new MagickImage("gradient:#e8e4d0-#b8b4a0", 64, 64); // shaded, so the corners differ
        var d = new Drawables().FillColor(new MagickColor("#5a5648"));
        foreach (var y in new[] { 16, 32, 48 }) d.Rectangle(0, y, 63, y);
        d.Draw(img);

        Assert.Equal(GridResult.None, GridDetector.Detect(img));
        // The AI naming the same counts confirms it.
        Assert.Equal((1, 4), (GridDetector.Detect(img, hint: new SpriteSheetHint(true, Rows: 4)).Sheet?.Grid?.Columns,
            GridDetector.Detect(img, hint: new SpriteSheetHint(true, Rows: 4)).Sheet?.Grid?.Rows));
    }

    [Fact]
    public void A_16px_tile_is_never_a_sheet_or_uncertain()
    {
        // Matching corners read as a background colour, and the tile's details as separate sprites.
        using var img = new MagickImage(new MagickColor("#6a3020"), 16, 16);
        new Drawables().FillColor(MagickColors.LightSteelBlue).Rectangle(2, 2, 6, 6).Rectangle(9, 9, 13, 13).Draw(img);

        Assert.Equal(GridResult.None, GridDetector.Detect(img));
        Assert.Equal(GridResult.None, GridDetector.Detect(img, hint: new SpriteSheetHint(true, Frames: 4)));
    }
}
