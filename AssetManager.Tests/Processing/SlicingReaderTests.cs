using System.Text;
using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Import;
using AssetManager.Processing.Settings;
using AssetManager.Processing.Sprites;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Spec "Slicing data first" (design D3 step 1).</summary>
public class SlicingReaderTests
{
    private static readonly AssetProcessor Processor = new(new HandlerRegistry([new ImageHandler(), new FallbackHandler()]));

    private static byte[] Sheet(int w, int h)
    {
        using var img = new MagickImage(MagickColors.Transparent, (uint)w, (uint)h);
        new ImageMagick.Drawing.Drawables().FillColor(MagickColors.OrangeRed).Rectangle(2, 2, w - 3, h - 3).Draw(img);
        return img.ToByteArray(MagickFormat.Png);
    }

    /// <summary>A Unity texture .meta with sprite mode Multiple; rectangles are bottom-up like Unity's.</summary>
    internal static string UnityMeta(string guid, IEnumerable<(string Name, int X, int Y, int W, int H)> sprites, int mode = 2)
    {
        var sb = new StringBuilder($"fileFormatVersion: 2\nguid: {guid}\nTextureImporter:\n  internalIDToNameTable: []\n  serializedVersion: 12\n  spriteMode: {mode}\n  spriteSheet:\n    serializedVersion: 2\n    sprites:\n");
        foreach (var s in sprites)
            sb.Append($"    - serializedVersion: 2\n      name: {s.Name}\n      rect:\n        serializedVersion: 2\n        x: {s.X}\n        y: {s.Y}\n        width: {s.W}\n        height: {s.H}\n      alignment: 0\n      pivot: {{x: 0.5, y: 0.5}}\n      border: {{x: 0, y: 0, z: 0, w: 0}}\n      outline: []\n      physicsShape: []\n      tessellationDetail: 0\n      bones: []\n      spriteID: abc\n");
        sb.Append("    outline: []\n  spritePackingTag: \n");
        return sb.ToString();
    }

    [Fact]
    public void Unity_meta_flips_y_and_keeps_names()
    {
        // 128×64 sheet: two 32×32 sprites on the top row (Unity y = 32), one on the bottom row (y = 0).
        var frames = SlicingReaders.ReadUnityMeta(UnityMeta("g", [("a", 0, 32, 32, 32), ("b", 32, 32, 32, 32), ("c", 0, 0, 32, 32)]), 64)!;
        Assert.Equal([new SpriteFrame("a", 0, 0, 32, 32), new SpriteFrame("b", 32, 0, 32, 32), new SpriteFrame("c", 0, 32, 32, 32)], frames);
        Assert.Null(SlicingReaders.ReadUnityMeta(UnityMeta("g", [("a", 0, 0, 8, 8)], mode: 1), 64)); // single sprite mode
    }

    [Fact]
    public void TexturePacker_json_hash_and_array_and_aseprite()
    {
        const string hash = """
            {"frames":{"run_0.png":{"frame":{"x":0,"y":0,"w":16,"h":24},"rotated":false,"trimmed":false},
                       "run_1.png":{"frame":{"x":16,"y":0,"w":24,"h":16},"rotated":true}},
             "meta":{"app":"https://www.codeandweb.com/texturepacker","image":"run.png","size":{"w":64,"h":32}}}
            """;
        Assert.Equal([new SpriteFrame("run_0", 0, 0, 16, 24), new SpriteFrame("run_1", 16, 0, 16, 24)], SlicingReaders.ReadJson(hash));

        const string array = """{"frames":[{"filename":"idle 0","frame":{"x":0,"y":0,"w":8,"h":8}},{"filename":"idle 1","frame":{"x":8,"y":0,"w":8,"h":8}}],"meta":{"size":{"w":16,"h":8}}}""";
        Assert.Equal([new SpriteFrame("idle 0", 0, 0, 8, 8), new SpriteFrame("idle 1", 8, 0, 8, 8)], SlicingReaders.ReadJson(array));

        // Aseprite's export: frames keyed "name 0.aseprite", with duration and frameTags.
        const string aseprite = """
            {"frames":{"hero 0.aseprite":{"frame":{"x":0,"y":0,"w":32,"h":32},"duration":100},"hero 1.aseprite":{"frame":{"x":32,"y":0,"w":32,"h":32},"duration":100}},
             "meta":{"app":"https://www.aseprite.org/","size":{"w":64,"h":32},"frameTags":[{"name":"walk","from":0,"to":1}]}}
            """;
        var frames = SlicingReaders.ReadJson(aseprite)!;
        Assert.Equal([0, 32], frames.Select(f => f.X));
        Assert.Equal(new SpriteGrid { CellW = 32, CellH = 32, Columns = 2, Rows = 1 }, SlicingReaders.GridOf(frames));
    }

    [Fact]
    public void Starling_and_generic_xml()
    {
        const string starling = """<TextureAtlas imagePath="ui.png"><SubTexture name="button" x="0" y="0" width="40" height="20"/><SubTexture name="icon.png" x="40" y="0" width="16" height="16"/></TextureAtlas>""";
        Assert.Equal([new SpriteFrame("button", 0, 0, 40, 20), new SpriteFrame("icon", 40, 0, 16, 16)], SlicingReaders.ReadXml(starling));
        Assert.Null(SlicingReaders.GridOf(SlicingReaders.ReadXml(starling)!)); // atlas: irregular frames, no grid

        const string generic = """<?xml version="1.0"?><TextureAtlas imagePath="s.png" width="32" height="16"><sprite n="a" x="0" y="0" w="16" h="16"/><sprite n="b" x="16" y="0" w="16" h="16"/></TextureAtlas>""";
        Assert.Equal([new SpriteFrame("a", 0, 0, 16, 16), new SpriteFrame("b", 16, 0, 16, 16)], SlicingReaders.ReadXml(generic));
    }

    [Fact]
    public async Task Json_atlas_next_to_the_image_makes_a_sprite_sheet()
    {
        using var dir = FixtureBuilder.TempDir();
        var png = dir.Write("ui.png", Sheet(64, 32));
        dir.Write("ui.json", """{"frames":{"a.png":{"frame":{"x":0,"y":0,"w":20,"h":32}},"b.png":{"frame":{"x":20,"y":0,"w":44,"h":16}}}}""");
        dir.Write("other.xml", """<TextureAtlas><SubTexture name="x" x="0" y="0" width="1" height="1"/></TextureAtlas>"""); // other base name: ignored
        var item = ImageHandlerTests.Item(png);

        await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Equal(AssetType.SpriteSheet, item.Asset.Type);
        var sheet = item.Asset.SpriteSheet!;
        Assert.Equal((2, GridDetectedBy.SlicingData, (SpriteGrid?)null), (sheet.FrameCount, sheet.DetectedBy, sheet.Grid));
        Assert.Equal(["a", "b"], sheet.Frames.Select(f => f.Name));
    }

    [Fact]
    public async Task Slicing_data_outside_the_image_is_ignored()
    {
        using var dir = FixtureBuilder.TempDir();
        var png = dir.Write("small.png", Sheet(32, 32));
        dir.Write("small.json", """{"frames":{"a":{"frame":{"x":0,"y":0,"w":32,"h":32}},"b":{"frame":{"x":32,"y":0,"w":32,"h":32}}}}""");
        var item = ImageHandlerTests.Item(png);

        await Processor.ProcessAsync(item, new PreviewOptions());

        Assert.Null(item.Asset.SpriteSheet);
        Assert.Equal(AssetType.Image, item.Asset.Type);
    }

    [Fact]
    public async Task Unity_sliced_sprite_in_a_unitypackage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        // A 128×96 sheet with 12 sprites of 32×32 named hero_0..hero_11, rows bottom-up in Unity's coordinates.
        var sprites = Enumerable.Range(0, 12).Select(i => ($"hero_{i}", i % 4 * 32, 64 - i / 4 * 32, 32, 32)).ToList();
        var guid = FixtureBuilder.Guid("Assets/Chars/hero.png");
        var package = kit.Catalog.Dir.Write("Chars.unitypackage", FixtureBuilder.UnityPackage(
            [new UnityPackageEntry(guid, "Assets/Chars/hero.png", Sheet(128, 96), UnityMeta(guid, sprites))]));

        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));

        await using var db = kit.Catalog.Db();
        var hero = await db.Assets.SingleAsync(a => a.Name == "hero", ct);
        Assert.Equal(AssetType.SpriteSheet, hero.Type);
        var sheet = hero.SpriteSheet!;
        Assert.Equal((12, GridDetectedBy.SlicingData), (sheet.FrameCount, sheet.DetectedBy));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"hero_{i}"), sheet.Frames.Select(f => f.Name));
        Assert.Equal(new SpriteFrame("hero_0", 0, 0, 32, 32), sheet.Frames[0]); // top-left after the y flip
        Assert.Equal(new SpriteFrame("hero_11", 96, 64, 32, 32), sheet.Frames[11]);
        Assert.Equal((4, 3), (sheet.Grid!.Columns, sheet.Grid.Rows));
    }
}
