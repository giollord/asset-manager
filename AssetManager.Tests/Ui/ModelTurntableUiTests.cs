using AssetManager.Core.Domain;
using AssetManager.Data.Services;
using AssetManager.Processing.Handlers;
using AssetManager.Processing.Settings;
using AssetManager.Shared.Components;
using AssetManager.Tests.Data;
using AssetManager.Tests.Processing;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The turntable of stored model renders (design D18) and the 3D viewer's fallback to it.</summary>
public sealed class ModelTurntableUiTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>A model whose original is on a drive that is not attached, with <paramref name="views"/> stored renders.</summary>
    public static async Task<long> DetachedModelAsync(TestCatalog c, int views = 24, string name = "rock")
    {
        var src = await TestData.SourceAsync(c, 1, s => s.Location = new Location("DEAD0001", @"Packs\Rocks"),
            _ => new Asset { Name = name, Type = AssetType.Model3D, Model = new ModelMetadata() });
        var id = src.Assets[0].Id;
        await using var db = c.Db();
        db.AssetFiles.Add(new AssetFile
        {
            AssetId = id, Role = FileRole.Primary, Location = new Location("DEAD0001", @"Packs\Rocks\rock.glb"),
            RelativePath = "rock.glb", FileName = "rock.glb", Extension = ".glb", Size = 1000,
        });
        for (var i = 0; i < views; i++)
            db.Previews.Add(new PreviewBlob { AssetId = id, Kind = PreviewKind.ModelView, Index = i, Mime = "image/webp", Bytes = [(byte)i, 1, 2] });
        await db.SaveChangesAsync(Ct);
        return id;
    }

    private void Use(TestCatalog c, ProcessingKit kit)
    {
        var settings = new PreviewSettings(c.Store);
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton(new PreviewQueries(c.Store));
        Services.AddSingleton(settings);
        Services.AddSingleton(new PreviewService(c.Store, kit.Resolver, new AssetProcessor(new HandlerRegistry([new FallbackHandler()])), settings));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Turntable_renders_every_view_and_starts_at_the_primary_one()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new ProcessingKit(c.Store);
        var id = await DetachedModelAsync(c);
        Use(c, kit);
        var js = JSInterop.SetupModule("./_content/AssetManager.Shared/js/turntable.js");

        var turntable = Render<ModelTurntable>(p => p.Add(x => x.AssetId, id));

        turntable.WaitForAssertion(() => Assert.Equal(24, turntable.FindAll(".turntable-view").Count));
        var root = turntable.Find("#model-turntable");
        Assert.Equal(("3", "8", "9"), (root.GetAttribute("data-rings"), root.GetAttribute("data-azimuths"), root.GetAttribute("data-primary")));
        var shown = Assert.Single(turntable.FindAll(".turntable-view:not([hidden])"));
        Assert.Equal("9", shown.GetAttribute("data-index")); // the primary view: ring 1, azimuth 1
        Assert.Equal("0", root.GetAttribute("tabindex")); // arrow keys step it once focused
        turntable.WaitForAssertion(() => Assert.Equal([3, 8, 9], js.Invocations["init"].Single().Arguments.Skip(1).Cast<int>()));
    }

    /// <summary>Scenario "View model with the drive detached": the 3D viewer shows the turntable with a note.</summary>
    [Fact]
    public async Task Viewer_falls_back_to_the_stored_renders()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new ProcessingKit(c.Store);
        var id = await DetachedModelAsync(c);
        Use(c, kit);

        var viewer = Render<ModelViewer>(p => p.Add(x => x.AssetId, id));

        viewer.WaitForAssertion(() => Assert.Contains("Original not available — showing stored renders", viewer.Find(".viewer-fallback-note").TextContent));
        viewer.WaitForAssertion(() => Assert.Equal(24, viewer.FindAll("#viewer-turntable .turntable-view").Count));
        Assert.Empty(viewer.FindAll(".viewer-error"));

        // While an unsaved rotation is shown, the renders do not match it: the message stays.
        viewer.Render(p => p.Add(x => x.FallbackToRenders, false));
        viewer.WaitForAssertion(() => Assert.Contains("The original is not available", viewer.Find(".viewer-error").TextContent));
        Assert.Empty(viewer.FindAll("#viewer-turntable"));
    }
}
