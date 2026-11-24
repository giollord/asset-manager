using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using AssetManager.Shared.Pages;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using AssetManager.Tests.Processing;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class SourcesPageTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private void Use(TestCatalog c)
    {
        var search = new AssetSearchService(c.Store);
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, search));
        Services.AddSingleton(new SourceService(c.Store));
        Services.AddSingleton(new MetadataEditService(c.Store));
        Services.AddSingleton(new DeletionService(c.Store));
        Services.AddSingleton<IShellService>(new NoShell());
        Services.AddSingleton<IVolumeService>(new FakeVolumeService());
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class NoShell : IShellService
    {
        public Task OpenAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Only_the_chosen_source_is_selected()
    {
        await using var c = await TestCatalog.CreateAsync();
        var ids = new List<long>();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            ids.Add((await TestData.SourceAsync(c, 1, s => { s.DisplayName = name; s.Location = new(null, $@"C:\packs\{name}"); })).Id);
        Use(c);

        var page = Render<Sources>(p => p.Add(x => x.Id, ids[0]));
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".source-row").Count));
        Assert.Equal([ids[0].ToString()], Selected(page));

        page.FindAll(".source-row")[1].Click();
        page.Render(p => p.Add(x => x.Id, ids[1]));
        page.WaitForAssertion(() => Assert.Equal([ids[1].ToString()], Selected(page)));

        // Spec "Selectable text": the source's name and path can be copied.
        page.WaitForAssertion(() => Assert.Equal("Beta", page.Find("#source-name").TextContent));
        Assert.Contains("selectable", page.Find("#source-name").ClassList);
        Assert.Contains(@"C:\packs\Beta", page.Find("#source-location").TextContent);
        Assert.Contains("selectable", page.Find("#source-location").ClassList);
    }

    [Fact]
    public async Task Nested_sources_are_not_selected()
    {
        await using var c = await TestCatalog.CreateAsync();
        var parent = await TestData.SourceAsync(c, 1, s => s.DisplayName = "Parent");
        await TestData.SourceAsync(c, 1, s => { s.DisplayName = "Child A"; s.ParentSourceId = parent.Id; });
        await TestData.SourceAsync(c, 1, s => { s.DisplayName = "Child B"; s.ParentSourceId = parent.Id; });
        Use(c);

        var page = Render<Sources>(p => p.Add(x => x.Id, parent.Id));
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".source-child").Count));
        Assert.Empty(page.FindAll(".source-child.mud-selected-item"));
    }

    private static string[] Selected(IRenderedComponent<Sources> page) =>
        page.FindAll(".source-row.mud-selected-item").Select(e => e.GetAttribute("data-id")!).ToArray();
}
