using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Data.Jobs;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using AssetManager.Shared.Pages;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class CollectionsPageTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private void Use(TestCatalog c)
    {
        var search = new AssetSearchService(c.Store);
        var hub = new ProgressHub();
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton(search);
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, search));
        Services.AddSingleton(new CollectionService(c.Store));
        Services.AddSingleton(new PreviewQueries(c.Store));
        Services.AddScoped<PreviewCache>();
        Services.AddSingleton(new AssetDetailsService(c.Store));
        Services.AddSingleton(new MetadataEditService(c.Store));
        Services.AddSingleton(new TagService(c.Store));
        Services.AddSingleton<IProgressHub>(hub);
        Services.AddSingleton<IJobQueue>(new JobQueue(c.Store, [], hub));
        Services.AddSingleton<IShellService>(new NoShell());
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
    public async Task Most_used_tags()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 10);
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids.Take(2).ToList(), ["alpha"], TagOrigin.User, Ct);   // 2
        await tags.AddTagsAsync(ids.Take(9).ToList(), ["zombie"], TagOrigin.User, Ct);  // 9
        await tags.AddTagsAsync(ids.Take(5).ToList(), ["medieval"], TagOrigin.AI, Ct);  // 5
        Use(c);
        Services.GetRequiredService<NavigationManager>().NavigateTo("collections?tab=tags");

        var page = Render<Collections>();
        string[] Order() => page.FindAll(".tag-row").Select(r => r.GetAttribute("data-tag")!).ToArray();
        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll(".tag-row").Count));

        // Sorted by usage (the default): highest count first, with the counts shown.
        Assert.Equal(["zombie", "medieval", "alpha"], Order());
        Assert.Equal(["9", "5", "2"], page.FindAll(".tag-row .tag-count").Select(e => e.TextContent.Trim()).ToArray());

        page.Find(".sort-name").Click();
        Assert.Equal(["alpha", "medieval", "zombie"], Order());

        page.Find(".sort-usage").Click();
        Assert.Equal(["zombie", "medieval", "alpha"], Order());
    }

    [Fact]
    public async Task Collection_list_shows_counts_and_members()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 6, make: i => new Asset { Name = $"a{i}", Type = i < 4 ? AssetType.Model3D : AssetType.Texture });
        var ids = src.Assets.Select(a => a.Id).ToList();
        var tags = new TagService(c.Store);
        await tags.AddTagsAsync(ids, ["forest"], TagOrigin.User, Ct);
        await tags.AddTagsAsync([ids[0]], ["winter"], TagOrigin.User, Ct);
        var forest = await new CollectionService(c.Store).SaveAsync(new Collection { Name = "Summer forest", IncludeTags = ["forest"], ExcludeTags = ["winter"] }, Ct);
        Use(c);

        var page = Render<Collections>(p => p.Add(x => x.Id, forest.Id));
        page.WaitForAssertion(() => Assert.Equal("5", page.Find(".collection-row .collection-count").TextContent.Trim()));
        Assert.Contains("3 Model3D", page.Find(".collection-types").TextContent);
        Assert.Contains("2 Texture", page.Find(".collection-types").TextContent);
        page.WaitForAssertion(() => Assert.Equal("5 assets", page.Find("#collection-total").TextContent.Trim()));
        Assert.Equal(5, page.FindAll("#collection-members [data-id]").Count);
        Assert.DoesNotContain(page.FindAll("#collection-members [data-id]"), e => e.GetAttribute("data-id") == ids[0].ToString());
    }
}
