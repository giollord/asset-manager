using AssetManager.AI.Import;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Processing.Import;
using AssetManager.Processing.Settings;
using AssetManager.Tests.Import;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The import wizard without a review step (spec "Imports commit without confirmation", "Import results").</summary>
public class ImportPageTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private sealed class NoPicker : IFolderPicker, IFilePicker
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private void Use(ImportKit kit)
    {
        Services.AddMudServices();
        Services.AddSingleton(kit.Store);
        Services.AddSingleton<IProgressHub>(kit.Hub);
        Services.AddSingleton(kit.Imports);
        Services.AddSingleton(kit.Results);
        Services.AddSingleton(new FileDropBroker());
        Services.AddSingleton<IFilePicker>(new NoPicker());
        Services.AddSingleton<IFolderPicker>(new NoPicker());
        Services.AddSingleton<IAppSettingsStore>(kit.SettingsStore);
        Services.AddSingleton(new ClassificationSettings(kit.Store));
        Services.AddSingleton<IJobQueue>(kit.Jobs);
        Services.AddScoped<AssetManager.Shared.Services.UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task A_finished_import_shows_its_results_without_a_confirm_step()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));
        Use(kit);
        Render<MudBlazor.MudPopoverProvider>();

        var page = Render<AssetManager.Shared.Pages.Import>();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("#import-results .result-source")));
        Assert.Empty(page.FindAll("#confirm-import"));
        Assert.Contains("The import is in the catalog.", page.Find("#import-committed").TextContent);
        Assert.Contains("3 assets in 2 sources", page.Find("#import-results-summary").TextContent);
        var forest = page.FindAll(".result-source").Single(e => e.TextContent.Contains("Forest Pack"));
        Assert.Contains("2 assets", forest.QuerySelector(".result-added")!.TextContent);
        Assert.StartsWith("browse?q=", forest.QuerySelector("a.result-source-name")!.GetAttribute("href"));
        // The skipped items with their reasons (the panel's rows are rendered when it is expanded).
        page.FindAll(".result-skips .mud-expand-panel-header").First().Click();
        page.WaitForAssertion(() => Assert.Contains(page.FindAll(".result-skip"), r => r.TextContent.Contains("script (noted)")));

        page.Find("#dismiss-results").Click();

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#import-results")));
        Assert.NotNull(page.Find("#start-import"));
        Assert.Empty(await kit.Imports.OpenSessionsAsync(Ct));
    }

    [Fact]
    public async Task Starting_an_import_ends_in_the_results_step()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        kit.Catalog.Dir.Write("Pack/rock.png", ImportStagingTests.Png(ImageMagick.MagickColors.Gray));
        kit.SettingsStore.Update(s => s.DefaultAiDepth = AiDepth.None);
        var drops = new FileDropBroker();
        Use(kit);
        Services.AddSingleton(drops);
        drops.Publish([folder]);
        Render<MudBlazor.MudPopoverProvider>();
        var page = Render<AssetManager.Shared.Pages.Import>();
        page.WaitForAssertion(() => Assert.Contains("Pack", page.Find("#import-inputs").TextContent));

        page.Find("#start-import").Click();

        page.WaitForAssertion(() => Assert.Contains("1 asset in 1 source", page.Find("#import-results-summary").TextContent), TimeSpan.FromSeconds(30));
        Assert.Empty(page.FindAll("#confirm-import"));
        Assert.Empty(page.FindAll("#stop-ai"));
    }
}
