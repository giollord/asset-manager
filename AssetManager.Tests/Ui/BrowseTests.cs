using AssetManager.AI.Enrichment;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Data.Jobs;
using AssetManager.Core.Search;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using AssetManager.Shared.Components;
using AssetManager.Shared.Pages;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The Browse page against a real catalog: filters, URL sync, selection and inline audio.</summary>
public class BrowseTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private void Use(TestCatalog c)
    {
        var words = new AssetSearchService(c.Store);
        var index = new InMemoryVectorIndex(c.Store);
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton(words);
        Services.AddSingleton(new CatalogSearch(c.Store, words, new EmbeddingService(c.Store, index, () => null), index));
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, words));
        Services.AddSingleton(new PreviewQueries(c.Store));
        Services.AddScoped<PreviewCache>();
        Services.AddSingleton(new AssetDetailsService(c.Store));
        Services.AddSingleton(new MetadataEditService(c.Store));
        Services.AddSingleton(new TagService(c.Store));
        var jobs = new JobQueue(c.Store, [], new ProgressHub());
        Services.AddSingleton<IJobQueue>(jobs);
        Services.AddSingleton(new DeletionService(c.Store));
        Services.AddSingleton(new AssetManager.Processing.Import.ReimportService(jobs));
        Services.AddSingleton(new AssetManager.AI.Enrichment.ReenrichService(c.Store, jobs, new AssetManager.AI.Runtime.AiRuntime(new AssetManager.Core.Settings.JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")))));
        Services.AddSingleton<IShellService>(Shell);
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        // The grid measures its width in JS; pretend 4 cards fit per row.
        JSInterop.SetupModule("./_content/AssetManager.Shared/js/browse.js").Setup<int>("observeWidth", _ => true).SetResult(4);
    }

    private sealed class RecordingShell : IShellService
    {
        public List<string> Opened { get; } = [];

        public Task OpenAsync(string path, CancellationToken ct = default)
        {
            Opened.Add(path);
            return Task.CompletedTask;
        }

        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private RecordingShell Shell { get; } = new();

    private static async Task SeedAsync(TestCatalog c)
    {
        var src = await TestData.SourceAsync(c, 12, make: i => new Asset { Name = $"a{i:00}", Type = i < 8 ? AssetType.Model3D : AssetType.Audio });
        await using var db = c.Db();
        db.Previews.Add(new PreviewBlob { AssetId = src.Assets[8].Id, Kind = PreviewKind.AudioClip, Mime = "audio/ogg", Bytes = [1, 2, 3] });
        await db.SaveChangesAsync(Ct);
    }

    private IRenderedComponent<Browse> RenderAt(SearchQuery query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("browse?q=" + query.ToToken());
        return Render<Browse>();
    }

    private IReadOnlyList<IRenderedComponent<AssetCard>> Cards(IRenderedComponent<Browse> page) => page.FindComponents<AssetCard>();

    [Fact]
    public async Task Filters_show_the_total_and_follow_the_url()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);

        var page = Render<Browse>();
        page.WaitForAssertion(() => Assert.Equal("12 assets", page.Find("#result-count").TextContent));

        page.Find("[data-type=Audio]").Click();
        page.WaitForAssertion(() => Assert.Equal("4 assets", page.Find("#result-count").TextContent));
        page.WaitForAssertion(() => Assert.All(Cards(page), card => Assert.Equal(AssetType.Audio, card.Instance.Type)));

        var uri = new Uri(Services.GetRequiredService<NavigationManager>().Uri);
        var token = System.Web.HttpUtility.ParseQueryString(uri.Query)["q"];
        Assert.Equal([AssetType.Audio], SearchQuery.FromToken(token).Types);

        // Opening that URL (e.g. a dashboard drill-down) restores the same filter.
        var again = RenderAt(new SearchQuery { Types = [AssetType.Model3D] });
        again.WaitForAssertion(() => Assert.Equal("8 assets", again.Find("#result-count").TextContent));
    }

    [Fact]
    public async Task Click_ctrl_shift_and_select_all()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        void Click(int i, bool ctrl = false, bool shift = false) =>
            page.FindAll(".asset-card-preview")[i].Click(new MouseEventArgs { CtrlKey = ctrl, ShiftKey = shift });
        IEnumerable<string> Selected() => page.Instance.Selected.Select(id => Cards(page).Single(card => card.Instance.AssetId == id).Instance.Name).Order();

        Click(2);
        Click(5, shift: true);
        page.WaitForAssertion(() => Assert.Equal(["a02", "a03", "a04", "a05"], Selected()));
        Click(9, ctrl: true);
        Click(3, ctrl: true);
        page.WaitForAssertion(() => Assert.Equal(["a02", "a04", "a05", "a09"], Selected()));
        Assert.Equal("4 selected", page.Find("#selected-count").TextContent.Trim());

        Click(7); // a plain click selects only that card
        page.WaitForAssertion(() => Assert.Equal(["a07"], Selected()));

        page.Find("#select-all").Click();
        page.WaitForAssertion(() => Assert.Equal(12, page.Instance.Selected.Count));

        // Escape clears; Ctrl+A on the page (reported by browse.js) selects every asset again.
        page.Find("#browse-grid").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(page.Instance.Selected);
        await page.InvokeAsync(() => page.Instance.SelectAllFromKeyboard());
        page.WaitForAssertion(() => Assert.Equal(12, page.Instance.Selected.Count));
    }

    [Fact]
    public async Task Select_all_takes_only_the_text_matches()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Text = "a0", Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.Equal("10 assets", page.Find("#result-count").TextContent));

        page.Find("#select-all").Click();

        page.WaitForAssertion(() => Assert.Equal("10 selected", page.Find("#selected-count").TextContent.Trim()));
        await using var db = c.Db();
        var names = await db.Assets.Where(a => page.Instance.Selected.Contains(a.Id)).Select(a => a.Name).ToListAsync(Ct);
        Assert.All(names, n => Assert.StartsWith("a0", n));
    }

    [Fact]
    public async Task Audio_card_plays_its_preview_clip_inline()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var module = JSInterop.SetupModule("./_content/AssetManager.Shared/js/browse.js");
        module.Setup<bool>("play", _ => true).SetResult(true);
        var page = RenderAt(new SearchQuery { Types = [AssetType.Audio], Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll(".play-audio").Count));

        page.FindAll(".play-audio")[0].Click();

        var play = JSInterop.Invocations["play"].Single();
        Assert.Equal("data:audio/ogg;base64,AQID", play.Arguments[0]);
        page.WaitForAssertion(() => Assert.Equal("Stop", page.FindAll(".play-audio")[0].GetAttribute("title")));
        await page.InvokeAsync(() => page.Instance.PlaybackEnded());
        page.WaitForAssertion(() => Assert.Equal("Play", page.FindAll(".play-audio")[0].GetAttribute("title")));
    }

    [Fact]
    public async Task Details_edit_persists_as_user_edited_and_tags_can_be_added()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));

        page.FindAll(".asset-card-preview")[0].DoubleClick();
        page.WaitForAssertion(() => Assert.Equal("a00", page.Find("#details-name").TextContent));
        page.Find("#details-edit").Click();
        page.Find("textarea#details-edit-description").Change("A crate with iron bands");
        page.Find("#details-edit-save").Click();
        page.WaitForAssertion(() => Assert.Contains("edited", page.Find("#details-description").TextContent));
        // Spec "Selectable text": the name and description can be copied; UI chrome cannot.
        Assert.Contains("selectable", page.Find("#details-name").ClassList);
        Assert.Contains("selectable", page.Find("#details-description").ClassList);

        var tagInput = page.Find("#asset-details .tag-input input");
        tagInput.Input("wooden");
        tagInput.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await using var db = c.Db();
        var asset = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).SingleAsync(a => a.Name == "a00", Ct);
        Assert.Equal("A crate with iron bands", asset.Description);
        Assert.True(asset.UserEdited.HasFlag(EditedFields.Description));
        Assert.Contains(asset.Tags, t => t.Tag.Name == "wooden" && t.Origin == TagOrigin.User);
    }
}
