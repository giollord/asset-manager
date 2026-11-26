using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Processing.Import;
using AssetManager.Processing.Sprites;
using AssetManager.Tests.Import;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Ai;

/// <summary>Spec "AI sprite sheet answers need confirmation" and ai-enrichment "Hint checked by the detector" (design D3).</summary>
public class SpriteSheetHintTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Seamless()
    {
        using var img = new MagickImage("xc:#808060", 256, 256);
        img.AddNoise(NoiseType.Gaussian, 2);
        img.Blur(0, 3);
        return img.ToByteArray(MagickFormat.Png);
    }

    /// <summary>Six touching 50×50 frames plus a 1 px border column: 301 px wide, so 50 is not a divisor the detector tries on its own.</summary>
    internal static byte[] Strip()
    {
        var rng = new Random(9);
        using var img = new MagickImage(MagickColors.Black, 301, 50);
        var d = new Drawables();
        for (var i = 0; i < 6; i++)
        {
            d.FillColor(new MagickColor((byte)rng.Next(60, 220), (byte)rng.Next(60, 220), (byte)rng.Next(60, 220))).Rectangle(i * 50, 0, i * 50 + 49, 49);
            d.FillColor(MagickColors.White).Circle(i * 50 + 15 + i * 3, 25, i * 50 + 23 + i * 3, 25);
        }
        d.Draw(img);
        return img.ToByteArray(MagickFormat.Png);
    }

    [Fact]
    public async Task Ai_guess_rejected_for_a_seamless_texture()
    {
        var client = new SchemaAwareChatClient();
        client.SpriteSheets["rock"] = new { likely = true, columns = 4, rows = 4 };
        await using var kit = await ImportKit.CreateAsync(client);
        var file = kit.Catalog.Dir.Write("Pack/Textures/rock.png", Seamless());

        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(Path.GetDirectoryName(file))!], Depth: AiDepth.Full));

        await using var db = kit.Catalog.Db();
        var rock = await db.Assets.SingleAsync(a => a.Name == "rock", Ct);
        Assert.Equal(AiStatus.Enriched, rock.AiStatus);
        Assert.Equal(AssetType.Texture, rock.Type);
        Assert.Null(rock.SpriteSheet);
        Assert.False(rock.GridUncertain); // a texture is not flagged on the AI's word alone
    }

    [Fact]
    public async Task Hint_checked_by_the_detector()
    {
        var client = new SchemaAwareChatClient();
        client.SpriteSheets["walk"] = new { likely = true, frames = 6 };
        await using var kit = await ImportKit.CreateAsync(client);
        var file = kit.Catalog.Dir.Write("Pack/walk.png", Strip());

        // Without the hint the detector finds nothing.
        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(file)!], Depth: AiDepth.None));
        await using (var db = kit.Catalog.Db())
            Assert.Equal((AssetType.Image, (SpriteSheetMetadata?)null), await db.Assets.Select(a => new ValueTuple<AssetType, SpriteSheetMetadata?>(a.Type, a.SpriteSheet)).SingleAsync(Ct));

        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(file)!], Depth: AiDepth.Full, IncludeDuplicates: true));

        await using var db2 = kit.Catalog.Db();
        var walk = await db2.Assets.Where(a => a.AiStatus == AiStatus.Enriched).SingleAsync(a => a.Name == "walk", Ct);
        Assert.Equal(AssetType.SpriteSheet, walk.Type);
        Assert.Equal((50, 50, 6, 1), (walk.SpriteSheet!.Grid!.CellW, walk.SpriteSheet.Grid.CellH, walk.SpriteSheet.Grid.Columns, walk.SpriteSheet.Grid.Rows));
        Assert.Equal(GridDetectedBy.Algorithm, walk.SpriteSheet.DetectedBy);
    }

    [Fact]
    public void Strip_needs_the_hint()
    {
        using var img = new MagickImage(Strip());
        Assert.Null(GridDetector.Detect(img).Sheet);
        var r = GridDetector.Detect(img, hint: new SpriteSheetHint(true, Frames: 6));
        Assert.Equal((50, 6, 1), (r.Sheet?.Grid?.CellW, r.Sheet?.Grid?.Columns, r.Sheet?.Grid?.Rows));
    }

    [Fact]
    public void Ai_type_alone_never_moves_an_image_to_sprite_sheet()
    {
        var asset = new Asset { Name = "hero", Type = AssetType.Image };
        var result = new ItemResult { Index = 1, Description = "Frames.", Type = AssetType.SpriteSheet, SpriteSheet = new SpriteSheetAnswer { Likely = true, Frames = 8 } };
        ItemPass.Apply(new ItemOutcome(asset, result, AiInputMode.Visual));

        Assert.Equal(AssetType.Image, asset.Type);
        Assert.Equal(new SpriteSheetHint(true, null, null, 8), ItemPass.SpriteHint(result));
        Assert.Equal(new SpriteSheetHint(true), ItemPass.SpriteHint(new ItemResult { Type = AssetType.SpriteSheet }));
        Assert.Null(ItemPass.SpriteHint(new ItemResult { Type = AssetType.Texture }));

        // A detected sheet does not go back to an image on the AI's word either.
        var sheet = new Asset { Name = "run", Type = AssetType.SpriteSheet };
        ItemPass.Apply(new ItemOutcome(sheet, new ItemResult { Index = 1, Description = "x", Type = AssetType.Image }, AiInputMode.Visual));
        Assert.Equal(AssetType.SpriteSheet, sheet.Type);
    }

    [Fact]
    public void User_type_and_grid_are_never_changed()
    {
        var typed = new Asset { Type = AssetType.Texture, UserEdited = EditedFields.Type };
        var grid = new GridResult(new SpriteSheetMetadata { FrameCount = 4, Frames = [new(null, 0, 0, 8, 8), new(null, 8, 0, 8, 8)] }, false, 1);
        SpriteSheetClassifier.Decide(typed, null, grid);
        Assert.Equal(AssetType.Texture, typed.Type);

        var kept = new SpriteSheetMetadata { FrameCount = 2, DetectedBy = GridDetectedBy.User };
        var edited = new Asset { Type = AssetType.SpriteSheet, UserEdited = EditedFields.Grid, SpriteSheet = kept };
        SpriteSheetClassifier.Decide(edited, null, GridResult.None);
        Assert.Same(kept, edited.SpriteSheet);
        Assert.Equal(AssetType.SpriteSheet, edited.Type);
    }
}
