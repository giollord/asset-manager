using AssetManager.Core.Domain;
using AssetManager.Processing.Export;
using AssetManager.Processing.Import;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

/// <summary>Drag-out file lists (design D7, spec "Drag textures into Unity" and "Unavailable item in selection").</summary>
public class DragOutPlannerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_selected_card_drags_the_selection_and_another_card_only_itself()
    {
        Assert.Equal([1L, 2, 3], DragOutPlanner.Targets(2, [1, 2, 3]));
        Assert.Equal([7L], DragOutPlanner.Targets(7, [1, 2, 3]));
        Assert.Equal([7L], DragOutPlanner.Targets(7, []));
    }

    [Fact]
    public async Task Resolves_files_into_a_new_folder_per_drag_and_reports_unavailable_assets()
    {
        await using var kit = await ImportKit.CreateAsync();
        var paths = new[] { "Tex/grass.png", "Tex/rock.png", "Tex/sand.png" }
            .Select((p, i) => kit.Catalog.Dir.Write(p, ImportStagingTests.Png(new MagickColor((byte)(40 + i * 60), 120, 60)))).ToList();
        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(paths[0])!], Depth: AiDepth.None));
        Dictionary<string, long> ids;
        await using (var db = kit.Catalog.Db()) ids = await db.Assets.ToDictionaryAsync(a => a.Name, a => a.Id, Ct);
        var planner = new DragOutPlanner(new AssetExporter(kit.Catalog.Store, kit.Processing.Resolver));

        var all = await planner.ResolveAsync(ids.Values.ToList(), Ct);
        Assert.Equal(["grass.png", "rock.png", "sand.png"], all.Files.Select(Path.GetFileName).Order());
        Assert.All(all.Files, f => Assert.True(File.Exists(f)));
        Assert.Null(all.Problem);

        // One original is gone: the others are still dragged, and the user is told which one is missing.
        File.Delete(paths[1]);
        var again = await planner.ResolveAsync(ids.Values.ToList(), Ct);
        Assert.Equal(["grass.png", "sand.png"], again.Files.Select(Path.GetFileName).Order());
        Assert.Equal("rock", Assert.Single(again.Unavailable).Name);
        Assert.Equal("1 of the dragged assets is unavailable and left out: rock.", again.Problem);
        // Each drag gets its own folder: a drop target may still hold the previous drag's files open.
        Assert.NotEqual(Path.GetDirectoryName(all.Files[0]), Path.GetDirectoryName(again.Files[0]));
    }
}
