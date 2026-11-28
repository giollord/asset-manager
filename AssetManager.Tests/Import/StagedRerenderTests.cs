using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Processing.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

public class StagedRerenderTests
{
    /// <summary>The orientation judge re-renders models while they are still staged in an import (regression).</summary>
    [Fact]
    public async Task Rerender_works_on_a_staged_asset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        kit.Catalog.Dir.Write("Pack/rock.png", ImportStagingTests.Png(MagickColors.Gray));
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], Depth: AiDepth.None), ct);
        await kit.Stager.ScanAndGroupAsync(session.Id, new NullProgress(), 6, ct);
        await kit.Stager.ProcessAsync(session.Id, new NullProgress(), 6, ct);
        Asset staged;
        await using (var db = kit.Catalog.Db())
            staged = await db.AllAssets.AsNoTracking().SingleAsync(a => a.ImportSessionId == session.Id, ct);

        var previews = await new CatalogPreviewRerenderer(kit.Store, kit.Previews).RerenderAsync(staged, ct);

        Assert.NotEmpty(previews);
    }
}
