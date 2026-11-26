using AssetManager.Core.Domain;
using AssetManager.Core.Platform;
using AssetManager.Core.Search;
using Microsoft.AspNetCore.Components;
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
        Services.AddSingleton(new SourceTagService(c.Store));
        Services.AddSingleton<AssetManager.Core.Jobs.IJobQueue>(new AssetManager.Data.Jobs.JobQueue(c.Store, [], new AssetManager.Data.Jobs.ProgressHub()));
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

    [Fact]
    public async Task Adding_a_source_tag_updates_a_collection_requiring_it()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 7, s => s.DisplayName = "Pixel Pack");
        await TestData.SourceAsync(c, 2, s => s.DisplayName = "Other");
        var collections = new CollectionService(c.Store);
        var collection = await collections.SaveAsync(new Collection { Name = "Pixel", IncludeTags = ["pixel art"] }, Ct);
        Assert.Equal(0, await collections.CountAsync(collection, Ct));
        Use(c);

        var page = Render<Sources>(p => p.Add(x => x.Id, src.Id));
        page.WaitForAssertion(() => Assert.Equal("Pixel Pack", page.Find("#source-name").TextContent));
        var input = page.Find("#source-tags .tag-input input");
        input.Input("Pixel Art");
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        page.WaitForAssertion(() => Assert.Contains("pixel art", page.Find("#source-tags").TextContent));
        Assert.Equal(7, await collections.CountAsync(collection, Ct));

        // Removing it on the Sources page removes it from the assets again.
        page.Find("#source-tags .mud-chip-close-button").Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("pixel art", page.Find("#source-tags").TextContent));
        Assert.Equal(0, await collections.CountAsync(collection, Ct));
    }

    [Fact]
    public async Task License_audit_list_filters_and_drills_down()
    {
        await using var c = await TestCatalog.CreateAsync();
        var ids = new Dictionary<string, long>();
        foreach (var (name, n) in new[] { ("Old pack", 4), ("Freebies", 2), ("Misc", 1) })
            ids[name] = (await TestData.SourceAsync(c, n, s => { s.DisplayName = name; s.License = new LicenseInfo(); })).Id;
        ids["Credit me"] = (await TestData.SourceAsync(c, 2, s =>
        {
            s.DisplayName = "Credit me";
            s.License = new LicenseInfo { Kind = "CC-BY", Commercial = TriState.Yes, Attribution = TriState.Yes };
        })).Id;
        Use(c);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("sources?audit=UnknownLicense");

        var page = Render<Sources>();

        // Filtered to the three unknown-license sources, most assets first.
        page.WaitForAssertion(() => Assert.Equal(["Old pack", "Freebies", "Misc"], page.FindAll(".audit-row").Select(r => r.QuerySelector(".audit-name")!.TextContent)));
        Assert.Contains("Unknown license: 3 sources, 7 assets", page.Find(".audit-problem").TextContent);

        // Browse filtered to its affected assets.
        page.FindAll(".audit-row")[0].QuerySelector(".audit-browse")!.Click();
        var browse = new Uri(nav.Uri);
        Assert.Equal("/browse", browse.AbsolutePath);
        Assert.Equal(new SearchQuery { SourceId = ids["Old pack"], LicenseProblem = LicenseProblem.UnknownLicense },
            SearchQuery.FromToken(System.Web.HttpUtility.ParseQueryString(browse.Query)["q"]));

        // Edit the source: the license editor opens, the audit stays on the left.
        nav.NavigateTo("sources?audit=UnknownLicense");
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".audit-row").Count));
        page.FindAll(".audit-row")[1].QuerySelector(".audit-edit")!.Click();
        Assert.EndsWith($"sources/{ids["Freebies"]}?audit=UnknownLicense&edit=true", nav.Uri);
        var edited = Render<Sources>(p => p.Add(x => x.Id, ids["Freebies"]));
        edited.WaitForAssertion(() => Assert.NotNull(edited.Find("#source-notes-edit")));
        Assert.NotEmpty(edited.FindAll(".audit-row"));

        // All problems: the attribution problem is listed too.
        nav.NavigateTo("sources?audit=all");
        var all = Render<Sources>();
        all.WaitForAssertion(() => Assert.Contains(all.FindAll(".audit-row"), r => r.GetAttribute("data-problem") == "AttributionMissing" && r.TextContent.Contains("Credit me")));
    }
}
