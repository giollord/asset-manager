using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using AssetManager.Processing.Settings;

namespace AssetManager.Tests.Data;

/// <summary>Design D18: the stored model renders as a turntable, spec "Renders from older settings".</summary>
public class ModelTurntableTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> ModelWithViewsAsync(TestCatalog c, int count, Func<int, int>? index = null)
    {
        var src = await TestData.SourceAsync(c, 1, make: _ => new Asset { Name = "rock", Type = AssetType.Model3D });
        var id = src.Assets[0].Id;
        await using var db = c.Db();
        for (var i = 0; i < count; i++)
            db.Previews.Add(new PreviewBlob { AssetId = id, Kind = PreviewKind.ModelView, Index = index?.Invoke(i) ?? i, Mime = "image/webp", Bytes = [(byte)i] });
        db.Previews.Add(new PreviewBlob { AssetId = id, Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = [255] });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private static Task<TurntableViews?> TurntableAsync(TestCatalog c, long id, PreviewOptions o) =>
        new PreviewQueries(c.Store).ModelTurntableAsync(id, o.Elevations.Count, o.AzimuthsPerRing, o.PrimaryViewIndex, Ct);

    [Fact]
    public async Task Default_settings_give_three_rings_of_eight()
    {
        await using var c = await TestCatalog.CreateAsync();
        var id = await ModelWithViewsAsync(c, 24, i => 23 - i); // stored out of order
        var options = new PreviewOptions();

        var t = (await TurntableAsync(c, id, options))!;

        Assert.Equal((3, 8), (t.Rings, t.Azimuths));
        Assert.Equal(options.PrimaryViewIndex, t.Primary);
        Assert.Equal(9, t.Primary); // ring 1, azimuth 1
        Assert.Equal(Enumerable.Range(0, 24).Select(i => (byte)(23 - i)), t.Views.Select(v => v.Bytes[0])); // in index order, not insertion order
    }

    [Fact]
    public async Task Renders_from_older_settings_are_one_ring()
    {
        await using var c = await TestCatalog.CreateAsync();
        var id = await ModelWithViewsAsync(c, 16); // rendered as 2 × 8, settings now 3 × 8

        var t = (await TurntableAsync(c, id, new PreviewOptions()))!;

        Assert.Equal((1, 16), (t.Rings, t.Azimuths));
        Assert.Equal(16, t.Views.Count);
        Assert.InRange(t.Primary, 0, 15);
    }

    [Fact]
    public async Task Gaps_in_the_indices_also_fall_back_and_the_primary_is_clamped()
    {
        await using var c = await TestCatalog.CreateAsync();
        var id = await ModelWithViewsAsync(c, 4, i => i * 2);
        var options = new PreviewOptions { Elevations = [20], AzimuthsPerRing = 4, PrimaryRing = 0, PrimaryAzimuth = 3 };

        var t = (await new PreviewQueries(c.Store).ModelTurntableAsync(id, 1, 4, 30, Ct))!;

        Assert.Equal((1, 4, 3), (t.Rings, t.Azimuths, t.Primary));
        Assert.Equal(3, options.PrimaryViewIndex);
    }

    [Fact]
    public async Task No_renders_no_turntable()
    {
        await using var c = await TestCatalog.CreateAsync();
        var id = await ModelWithViewsAsync(c, 0);
        Assert.Null(await TurntableAsync(c, id, new PreviewOptions()));
    }
}
