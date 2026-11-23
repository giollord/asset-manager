using AssetManager.Core.Domain;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Media;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

public class PreviewServiceTests
{
    private static async Task<(TestCatalog c, ProcessingKit kit, PreviewService svc, PreviewSettings settings, long png, long wav)> SeedAsync()
    {
        var c = await TestCatalog.CreateAsync();
        var kit = new ProcessingKit(c.Store);
        using (var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 400, 400))
            img.Write(Path.Combine(kit.Dir.Path, "p.png"));
        var wav = await AudioHandlerTests.ToneAsync(kit.Dir, "rain.wav", 30);
        var src = await TestData.SourceAsync(c, 2, make: i => i == 0
            ? new Asset { Name = "p", Type = AssetType.Image, Files = [new AssetFile { Location = new(null, Path.Combine(kit.Dir.Path, "p.png")), FileName = "p.png", Extension = ".png" }] }
            : new Asset { Name = "rain", Type = AssetType.Audio, Files = [new AssetFile { Location = new(null, wav), FileName = "rain.wav", Extension = ".wav" }] });
        var settings = new PreviewSettings(c.Store);
        var processor = new AssetProcessor(new HandlerRegistry([new ImageHandler(), new AudioHandler(), new FallbackHandler()]));
        var svc = new PreviewService(c.Store, kit.Resolver, processor, settings);
        return (c, kit, svc, settings, src.Assets[0].Id, src.Assets[1].Id);
    }

    private static async Task<PreviewBlob> BlobAsync(TestCatalog c, long id, PreviewKind kind)
    {
        await using var db = c.Db();
        return await db.Previews.SingleAsync(p => p.AssetId == id && p.Kind == kind);
    }

    [Fact]
    public async Task Previews_replaced_after_settings_change_only_when_regenerated()
    {
        var (c, kit, svc, settings, png, wav) = await SeedAsync();
        await using var _ = c;
        using var __ = kit;

        Assert.Equal([png, wav], await svc.OutdatedAsync()); // never generated
        var first = await svc.RegenerateAsync([png, wav]);
        Assert.Equal(2, first.Regenerated);
        Assert.Empty(await svc.OutdatedAsync());
        Assert.Equal("image/png", (await BlobAsync(c, png, PreviewKind.Thumbnail)).Mime);
        await using (var db = c.Db())
        {
            var a = await db.Assets.SingleAsync(x => x.Id == png);
            Assert.Equal(400, a.Image!.Width);
            Assert.Equal(new PreviewOptions().Version(), a.PreviewOptionsVersion);
        }

        // Change keep-as-is threshold: existing previews untouched until regenerated.
        await settings.SaveAsync(new PreviewOptions { KeepAsIsMaxDimension = 256, AudioClipSeconds = 5 });
        Assert.Equal("image/png", (await BlobAsync(c, png, PreviewKind.Thumbnail)).Mime);
        Assert.Equal([png, wav], await svc.OutdatedAsync());

        var second = await svc.RegenerateOutdatedAsync();
        Assert.Equal(2, second.Regenerated);
        var thumb = await BlobAsync(c, png, PreviewKind.Thumbnail);
        Assert.Equal("image/webp", thumb.Mime);
        Assert.Equal((await settings.GetAsync()).Version(), thumb.OptionsVersion);
        Assert.Empty(await svc.OutdatedAsync());

        // Custom audio clip length.
        var clip = await BlobAsync(c, wav, PreviewKind.AudioClip);
        var path = kit.Dir.Write("clip.ogg", clip.Bytes);
        var probe = await Ffmpeg.ProbeAsync(path, CancellationToken.None);
        Assert.InRange(probe.Duration.TotalSeconds, 4.5, 5.05);
        await using (var db = c.Db()) Assert.Equal(1, await db.Previews.CountAsync(p => p.AssetId == wav && p.Kind == PreviewKind.AudioClip));
    }

    [Fact]
    public async Task Unavailable_originals_are_skipped()
    {
        var (c, kit, svc, _, png, wav) = await SeedAsync();
        await using var _ = c;
        using var __ = kit;
        File.Delete(Path.Combine(kit.Dir.Path, "p.png"));

        var result = await svc.RegenerateAsync([png, wav]);

        Assert.Equal(1, result.Regenerated);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(png, skipped.AssetId);
        Assert.Contains("p.png", skipped.Reason);
    }
}
