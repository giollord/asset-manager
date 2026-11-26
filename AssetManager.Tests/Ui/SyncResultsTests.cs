using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Processing.Sync;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Import;
using Bunit;
using ImageMagick;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The rescan results on the Sources page (spec "Acting on rescan results").</summary>
public class SyncResultsTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private void Use(ImportKit kit)
    {
        Services.AddMudServices();
        Services.AddSingleton(kit.Store);
        Services.AddSingleton<IProgressHub>(kit.Hub);
        Services.AddSingleton<IJobQueue>(kit.Jobs);
        Services.AddSingleton(kit.Imports);
        Services.AddSingleton(kit.Reimports);
        Services.AddSingleton(new DeletionService(kit.Store));
        Services.AddSingleton<IAppSettingsStore>(kit.SettingsStore);
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task Import_added_files_opens_a_staged_import_into_the_source()
    {
        await using var kit = await ImportKit.CreateAsync();
        var root = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        kit.Catalog.Dir.Write("Pack/a.png", ImportStagingTests.Png(new MagickColor(10, 20, 30)));
        kit.Catalog.Dir.Write("Pack/b.png", ImportStagingTests.Png(new MagickColor(40, 20, 30)));
        await kit.CatalogAsync(new ImportRequest([root], Depth: AiDepth.None));
        for (var i = 0; i < 3; i++) kit.Catalog.Dir.Write($"Pack/New/n{i}.png", ImportStagingTests.Png(new MagickColor((byte)(90 + i * 20), 20, 30)));
        File.Delete(Path.Combine(root, "b.png"));
        await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(RescanJob.JobType, "Rescan sources", new RescanPayload(null), ct: Ct), Ct);
        Use(kit);
        Render<MudBlazor.MudPopoverProvider>();

        var view = Render<SyncResults>();

        view.WaitForAssertion(() => Assert.Single(view.FindAll(".rescan-source")));
        Assert.Contains("Added (3)", view.Find(".rescan-added").TextContent);
        Assert.Contains("b.png", view.Find(".rescan-removed").TextContent);
        Assert.Contains("Remove 1 missing asset", view.Find(".rescan-remove").TextContent);
        Assert.Contains("No library scan yet", view.Find("#library-none").TextContent);

        view.Find(".rescan-import-added").Click();

        view.WaitForAssertion(() => Assert.EndsWith("/import", Services.GetRequiredService<NavigationManager>().Uri));
        var session = Assert.Single(await kit.Imports.OpenSessionsAsync(Ct));
        long sourceId;
        await using (var db = kit.Catalog.Db()) sourceId = await db.Sources.Select(s => s.Id).SingleAsync(Ct);
        Assert.Equal(sourceId, session.IntoSourceId);
        Assert.Equal(3, session.OnlyPaths!.Count);
        await kit.Jobs.WaitAsync(session.JobId!.Value, Ct);
    }
}
