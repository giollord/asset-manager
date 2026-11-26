using AssetManager.Core.Domain;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Shared.Pages;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class DashboardTests : BunitContext
{
    [Fact]
    public async Task Drill_down_from_a_count()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 20, make: i => new Asset { Name = $"s{i}", Type = AssetType.Audio, AiStatus = i < 14 ? AiStatus.Failed : AiStatus.Enriched });
        var search = new AssetSearchService(c.Store);
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, search));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var page = Render<Dashboard>();
        page.WaitForAssertion(() => Assert.Contains("20", page.Find("#stat-assets").TextContent));

        var failed = page.FindAll(".stat-link").Single(e => e.GetAttribute("data-label") == "Failed enrichment");
        Assert.Contains("14", failed.TextContent);
        failed.Click();

        var uri = new Uri(Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("/browse", uri.AbsolutePath);
        var query = SearchQuery.FromToken(System.Web.HttpUtility.ParseQueryString(uri.Query)["q"]);
        Assert.Equal(new SearchQuery { AiStatus = AiStatus.Failed }, query);
        Assert.Equal(14, (await search.SearchAsync(query, 0, 100, Xunit.TestContext.Current.CancellationToken)).Total); // Browse lists those 14
    }

    /// <summary>Spec "Dashboard tile": 3 sources with 410 assets have an unknown license.</summary>
    [Fact]
    public async Task License_audit_tile_opens_the_filtered_audit_list()
    {
        await using var c = await TestCatalog.CreateAsync();
        foreach (var (name, n) in new[] { ("Old pack", 400), ("Freebies", 7), ("Misc", 3) })
            await TestData.SourceAsync(c, n, s => { s.DisplayName = name; s.License = new LicenseInfo(); });
        await TestData.SourceAsync(c, 5, s => { s.DisplayName = "Clean"; s.License = new LicenseInfo { Kind = "CC0", Commercial = TriState.Yes, Attribution = TriState.No }; });
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, new AssetSearchService(c.Store)));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var page = Render<Dashboard>();
        var tile = page.WaitForElement(".audit-link[data-problem='UnknownLicense']");

        Assert.Equal("Unknown license: 3 sources, 410 assets", tile.TextContent.Trim());
        Assert.Equal("Non-commercial: 0 sources, 0 assets", page.Find(".audit-link[data-problem='NonCommercial']").TextContent.Trim());
        tile.Click();

        var uri = new Uri(Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("/sources", uri.AbsolutePath);
        Assert.Equal("?audit=UnknownLicense", uri.Query);
    }
}
