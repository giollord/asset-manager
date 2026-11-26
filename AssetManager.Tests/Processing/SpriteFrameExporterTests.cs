using AssetManager.Core.Domain;
using AssetManager.Processing.Export;
using AssetManager.Processing.Import;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Spec "Frame export" (design D5): frames cut from the original at full size.</summary>
public class SpriteFrameExporterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exports_eight_pngs_at_the_original_cell_size_and_a_row_as_animated_webp()
    {
        await using var kit = await ImportKit.CreateAsync();
        using (var sheet = GridDetectorTests.RunCycle())
            kit.Catalog.Dir.Write("Chars/hero_run.png", sheet.ToByteArray(MagickFormat.Png));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Chars")], Depth: AiDepth.None));
        long id;
        await using (var db = kit.Catalog.Db())
        {
            var a = await db.Assets.SingleAsync(Ct);
            Assert.Equal((AssetType.SpriteSheet, 8), (a.Type, a.SpriteSheet!.FrameCount));
            id = a.Id;
        }
        var exporter = new SpriteFrameExporter(kit.Catalog.Store, new AssetExporter(kit.Catalog.Store, kit.Processing.Resolver));
        var target = Path.Combine(kit.Catalog.Dir.Path, "out");

        var pngs = await exporter.ExportPngsAsync(id, Enumerable.Range(0, 8).ToList(), target, Ct);

        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"hero_run_{i:D2}.png"), pngs.Select(Path.GetFileName));
        foreach (var (path, i) in pngs.Select((p, i) => (p, i)))
        {
            using var frame = new MagickImage(path);
            Assert.Equal((64u, 64u), (frame.Width, frame.Height));
            using var expected = GridDetectorTests.RunCycle();
            expected.Crop(new MagickGeometry(i * 64, 0, 64, 64));
            expected.ResetPage();
            Assert.Equal(0, frame.Compare(expected, ErrorMetric.Absolute)); // the exact pixels of that cell
        }

        // Exporting again does not overwrite.
        var again = await exporter.ExportPngsAsync(id, [0], target, Ct);
        Assert.Equal("hero_run_01 (2).png", Path.GetFileName(again.Single()));

        var webp = await exporter.ExportAnimationAsync(id, Enumerable.Range(0, 8).ToList(), 12, Path.Combine(target, "hero_run.webp"), Ct);
        using var animation = new MagickImageCollection(webp);
        Assert.Equal(8, animation.Count);
        animation.Coalesce(); // WebP stores later frames as changed rectangles
        Assert.All(animation, f => Assert.Equal((64u, 64u), (f.Width, f.Height)));
    }

    [Fact]
    public async Task Slicing_names_name_the_files()
    {
        await using var kit = await ImportKit.CreateAsync();
        using (var img = new MagickImage(MagickColors.Purple, 64, 32))
            kit.Catalog.Dir.Write("Ui/ui.png", img.ToByteArray(MagickFormat.Png));
        kit.Catalog.Dir.Write("Ui/ui.json", """{"frames":{"button.png":{"frame":{"x":0,"y":0,"w":40,"h":20}},"icon/star.png":{"frame":{"x":40,"y":0,"w":16,"h":16}}}}""");
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Ui")], Depth: AiDepth.None));
        long id;
        await using (var db = kit.Catalog.Db()) id = await db.Assets.Select(a => a.Id).SingleAsync(Ct);
        var exporter = new SpriteFrameExporter(kit.Catalog.Store, new AssetExporter(kit.Catalog.Store, kit.Processing.Resolver));

        var pngs = await exporter.ExportPngsAsync(id, [0, 1], Path.Combine(kit.Catalog.Dir.Path, "out"), Ct);

        Assert.Equal(["button.png", "icon_star.png"], pngs.Select(Path.GetFileName));
        using var star = new MagickImage(pngs[1]);
        Assert.Equal((16u, 16u), (star.Width, star.Height));
    }
}
