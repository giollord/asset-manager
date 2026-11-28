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

    /// <summary>Spec "Update changed package": both update buttons name the source the import replaces.</summary>
    [Fact]
    public async Task Update_buttons_replace_the_rescanned_source()
    {
        await using var kit = await ImportKit.CreateAsync();
        Fixtures.UnityPackageEntry Tex(string n, int i) => new(Fixtures.FixtureBuilder.Guid(n), $"Assets/{n}.png", ImportStagingTests.Png(new MagickColor((byte)(i * 40), 20, 30)));
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", Fixtures.FixtureBuilder.UnityPackage([Tex("a", 0)]));
        kit.Catalog.Dir.Write("Folder/inner.unitypackage", Fixtures.FixtureBuilder.UnityPackage([Tex("b", 1)]));
        kit.Catalog.Dir.Write("Folder/c.png", ImportStagingTests.Png(new MagickColor(200, 20, 30)));
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Folder")], Depth: AiDepth.None));
        kit.Catalog.Dir.Write("Kit.unitypackage", Fixtures.FixtureBuilder.UnityPackage([Tex("a", 0), Tex("a2", 3)]));
        kit.Catalog.Dir.Write("Folder/inner.unitypackage", Fixtures.FixtureBuilder.UnityPackage([Tex("b", 4)]));
        await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(RescanJob.JobType, "Rescan sources", new RescanPayload(null), ct: Ct), Ct);
        long kitId, innerId;
        await using (var db = kit.Catalog.Db())
        {
            kitId = await db.Sources.Where(s => s.ParentSourceId == null && s.Kind == SourceKind.UnityPackage).Select(s => s.Id).SingleAsync(Ct);
            innerId = await db.Sources.Where(s => s.ParentSourceId != null).Select(s => s.Id).SingleAsync(Ct);
        }
        Use(kit);
        Render<MudBlazor.MudPopoverProvider>();
        var view = Render<SyncResults>();
        view.WaitForAssertion(() => Assert.Single(view.FindAll(".rescan-update")));

        async Task<ImportSession> ClickAsync(string button)
        {
            var before = (await kit.Imports.OpenSessionsAsync(Ct)).Select(s => s.Id).DefaultIfEmpty().Max();
            view.Find(button).Click();
            ImportSession? started = null;
            // The session is created before its job is queued: wait for both, or JobId can still be null (seen on CI).
            view.WaitForAssertion(() => Assert.NotNull(started = kit.Imports.OpenSessionsAsync(Ct).Result.FirstOrDefault(s => s.Id > before && s.JobId is not null)));
            await kit.Jobs.WaitAsync(started!.JobId!.Value, Ct);
            return started;
        }

        Assert.Equal(kitId, (await ClickAsync(".rescan-update")).ReplaceSourceId);
        Assert.Equal(innerId, (await ClickAsync(".rescan-update-nested")).ReplaceSourceId);
        await using var check = kit.Catalog.Db();
        Assert.Equal(3, await check.Sources.CountAsync(Ct)); // both replaced in place, none added
    }
}
