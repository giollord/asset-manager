using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Search;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Maintenance "Re-detect sprite sheets and pixel art" and the "grid uncertain" filter (task 9.6).</summary>
public class RedetectSpritesJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Atlas()
    {
        using var img = new MagickImage(MagickColors.Transparent, 256, 64);
        new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Crimson)
            .Rectangle(2, 10, 21, 50).Rectangle(26, 4, 85, 60).Rectangle(90, 20, 109, 40).Rectangle(114, 2, 249, 62).Draw(img);
        return img.ToByteArray(MagickFormat.Png);
    }

    [Fact]
    public async Task Redetects_images_keeps_user_edits_and_skips_missing_originals()
    {
        await using var kit = await ImportKit.CreateAsync();
        using (var run = GridDetectorTests.RunCycle()) kit.Catalog.Dir.Write("Pack/run.png", run.ToByteArray(MagickFormat.Png));
        using (var art = ImageHandlerTests.PixelArt(16, 4, 10)) kit.Catalog.Dir.Write("Pack/hero.png", art.ToByteArray(MagickFormat.Png));
        kit.Catalog.Dir.Write("Pack/mine.png", Atlas());
        kit.Catalog.Dir.Write("Pack/atlas.png", Atlas());
        var gone = kit.Catalog.Dir.Write("Pack/gone.png", ImportStagingTests.Png(MagickColors.Blue, 64));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Pack")], Depth: AiDepth.None));

        // Simulate a catalog from before the measured detection, plus one user decision.
        await using (var db = kit.Catalog.Db())
        {
            foreach (var a in await db.Assets.ToListAsync(Ct))
            {
                a.SpriteSheet = null;
                a.GridUncertain = false;
                a.Type = AssetType.Image;
                a.Style = [];
                if (a.Image is { } img) a.Image = img with { IsPixelArt = false, PixelScale = null };
                if (a.Name == "mine") a.UserEdited |= EditedFields.Grid; // "not a sprite sheet", decided by the user
            }
            await db.AssetTags.ExecuteDeleteAsync(Ct);
            await db.SaveChangesAsync(Ct);
        }
        File.Delete(gone);

        var job = await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(JobTypes.RedetectSprites, "Re-detect", new AssetsPayload(null), ct: Ct), Ct);

        Assert.True(job.State is not JobState.Failed and not JobState.Cancelled, $"{job.State}: {job.Error}");
        await using var check = kit.Catalog.Db();
        var assets = await check.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).ToDictionaryAsync(a => a.Name, Ct);
        Assert.Equal((AssetType.SpriteSheet, 8), (assets["run"].Type, assets["run"].SpriteSheet!.FrameCount));
        Assert.Equal((true, 4), (assets["hero"].Image!.IsPixelArt, assets["hero"].Image!.PixelScale));
        Assert.Contains("pixel art", assets["hero"].Style);
        Assert.Contains(assets["hero"].Tags, t => t.Tag.Name == "pixel art" && t.Origin == TagOrigin.AI);
        Assert.True(assets["atlas"].GridUncertain);
        Assert.False(assets["mine"].GridUncertain); // user edit kept
        Assert.Null(assets["mine"].SpriteSheet);
        var errors = await ((AssetManager.Data.Jobs.JobQueue)kit.Jobs).ItemErrorsAsync(job.Id, Ct);
        Assert.Equal("gone", Assert.Single(errors).ItemName);
        Assert.Contains("unavailable", errors[0].Reason, StringComparison.OrdinalIgnoreCase);

        // The "grid uncertain" filter finds the atlas.
        var found = await new AssetManager.Data.Search.AssetSearchService(kit.Catalog.Store).SearchAsync(new SearchQuery { GridUncertain = true }, 0, 50, Ct);
        Assert.Equal(["atlas"], found.Items.Select(i => i.Name));
    }
}
