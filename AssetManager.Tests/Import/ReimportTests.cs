using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

public class ReimportTests
{
    /// <summary>Catalogs a coin image, then gives it a user tag and a user description and changes the file on disk.</summary>
    private static async Task<(long Id, string OldHash, byte[] OldPreview)> PrepareAsync(ImportKit kit)
    {
        var ct = TestContext.Current.CancellationToken;
        var file = kit.Catalog.Dir.Write("loose/Coin.png", ImportStagingTests.Png(MagickColors.Gold, 32));
        await kit.CatalogAsync(new ImportRequest([file], Depth: AiDepth.None));
        await using var db = kit.Catalog.Db();
        var coin = await db.Assets.Include(a => a.Tags).Include(a => a.Files).SingleAsync(ct);
        coin.Tags.Add(new AssetTag { Tag = (await TagService.GetOrCreateAsync(db, ["favorite"], ct)).Single(), Origin = TagOrigin.User });
        coin.Description = "My lucky coin.";
        coin.UserEdited |= EditedFields.Description;
        (coin.Rating, coin.Notes, coin.Favorite) = (4, "use for level 2 rocks", true);
        await db.SaveChangesAsync(ct);
        var preview = await db.Previews.Where(p => p.AssetId == coin.Id && p.Kind == PreviewKind.Thumbnail).Select(p => p.Bytes).SingleAsync(ct);

        await File.WriteAllBytesAsync(file, ImportStagingTests.Png(MagickColors.Silver, 64), ct); // a new revision of the file
        return (coin.Id, coin.PrimaryFile!.FastHash!, preview);
    }

    [Fact]
    public async Task Reimport_keeps_user_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var (id, oldHash, oldPreview) = await PrepareAsync(kit);

        var job = await kit.Jobs.WaitAsync(await kit.Reimports.StartAsync([id], reenrich: false, ct), ct);

        Assert.True(job.State == JobState.Completed && job.Errors == 0, job.Error);
        await using var db = kit.Catalog.Db();
        var coin = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Files).SingleAsync(ct);
        Assert.Equal(64, coin.Image!.Width);                                    // technical metadata refreshed
        Assert.NotEqual(oldHash, coin.PrimaryFile!.FastHash);
        var preview = await db.Previews.Where(p => p.AssetId == id && p.Kind == PreviewKind.Thumbnail).Select(p => p.Bytes).SingleAsync(ct);
        Assert.NotEqual(oldPreview, preview);                                   // previews refreshed
        Assert.Contains(coin.Tags, t => t.Tag.Name == "favorite" && t.Origin == TagOrigin.User);
        Assert.Equal("My lucky coin.", coin.Description);
        Assert.Equal(AiStatus.NotEnriched, coin.AiStatus);
    }

    [Fact]
    public async Task Reimport_with_reenrichment_keeps_user_edits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient());
        var (id, _, _) = await PrepareAsync(kit);

        var reimport = await kit.Jobs.WaitAsync(await kit.Reimports.StartAsync([id], reenrich: true, ct), ct);

        Assert.True(reimport.State == JobState.Completed, reimport.Error);
        await using (var db = kit.Catalog.Db())
        {
            var enrichJob = await db.Jobs.SingleAsync(j => j.Type == "ai.enrich", ct);
            var enrich = await kit.Jobs.WaitAsync(enrichJob.Id, ct);
            Assert.True(enrich.State == JobState.Completed && enrich.Errors == 0, enrich.Error);
        }
        await using var check = kit.Catalog.Db();
        var coin = await check.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).SingleAsync(ct);
        Assert.Equal(AiStatus.Enriched, coin.AiStatus);
        Assert.Equal("My lucky coin.", coin.Description);                       // user edit kept
        Assert.Contains(coin.Tags, t => t.Tag.Name == "favorite" && t.Origin == TagOrigin.User);
        Assert.Contains(coin.Tags, t => t.Tag.Name == "ai-tag" && t.Origin == TagOrigin.AI);
        // Spec "Rating survives re-import".
        Assert.Equal((4, "use for level 2 rocks", true), (coin.Rating, coin.Notes, coin.Favorite));
    }
}
