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
        Services.AddSingleton(ImageQueries);
        Services.AddSingleton(new CatalogSearch(c.Store, words, new EmbeddingService(c.Store, index, () => null), index, ImageQueries));
        Services.AddSingleton(new ImageQueryService(new EmbeddingService(c.Store, index, () => null), ImageQueries));
        Services.AddSingleton<ICatalogStatsService>(new CatalogStatsService(c.Store, words));
        Services.AddSingleton(new PreviewQueries(c.Store));
        Services.AddSingleton(new AssetManager.Processing.Settings.PreviewSettings(c.Store));
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
        Services.AddSingleton<IDragOutService>(new NoDragOut());
        Services.AddSingleton(new DragOutState());
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

    private sealed class NoDragOut : IDragOutService
    {
        public bool IsSupported => false;
    }

    private ImageQueryCache ImageQueries { get; } = new();

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
        await page.InvokeAsync(() => page.Instance.OnShortcut("Escape"));
        page.WaitForAssertion(() => Assert.Empty(page.Instance.Selected));
        await page.InvokeAsync(() => page.Instance.SelectAllFromKeyboard());
        page.WaitForAssertion(() => Assert.Equal(12, page.Instance.Selected.Count));
    }

    /// <summary>Spec "Click on the name", "Ctrl-click on the type line" (design D16): the whole card is the click target.</summary>
    [Fact]
    public async Task Clicks_anywhere_on_a_card_select_it()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        IEnumerable<string> Selected() => page.Instance.Selected.Select(id => Cards(page).Single(card => card.Instance.AssetId == id).Instance.Name).Order();

        page.FindAll(".asset-card-preview")[1].Click();
        page.WaitForAssertion(() => Assert.Equal(["a01"], Selected()));
        page.FindAll(".asset-card-preview")[2].Click(new MouseEventArgs { CtrlKey = true });
        page.WaitForAssertion(() => Assert.Equal(["a01", "a02"], Selected()));

        // A click on the name selects only that card, as a click on the preview does.
        page.FindAll(".asset-card-name")[4].Click();
        page.WaitForAssertion(() => Assert.Equal(["a04"], Selected()));

        // Ctrl-click on the type line adds to the selection.
        page.FindAll(".asset-card-preview")[1].Click(new MouseEventArgs { CtrlKey = true });
        page.WaitForAssertion(() => Assert.Equal(["a01", "a04"], Selected()));
        page.FindAll(".asset-card-type")[6].Click(new MouseEventArgs { CtrlKey = true });
        page.WaitForAssertion(() => Assert.Equal(["a01", "a04", "a06"], Selected()));

        // The checkbox toggles that card without also making it the only selected one.
        page.FindAll(".asset-select input")[8].Change(true);
        page.WaitForAssertion(() => Assert.Equal(["a01", "a04", "a06", "a08"], Selected()));
        page.FindAll(".asset-select input")[4].Change(false);
        page.WaitForAssertion(() => Assert.Equal(["a01", "a06", "a08"], Selected()));
        // The checkbox's click stops at the checkbox: bUnit finds no click handler on its way up to the card.
        Assert.Throws<MissingEventHandlerException>(() => page.FindAll(".asset-select input")[8].Click());
        Assert.Equal(["a01", "a06", "a08"], Selected());

        // A double click on the name opens the details.
        page.FindAll(".asset-card-name")[3].DoubleClick();
        page.WaitForAssertion(() => Assert.Equal("a03", page.Find("#details-name").TextContent));
        Assert.Contains("selectable", page.Find("#details-name").ClassList); // the drawer's name stays copyable

        // Spec "Card text not selectable": nothing on a card opts into text selection.
        Assert.Empty(page.FindAll(".asset-card .selectable"));
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

    [Fact]
    public async Task Details_set_favourite_rating_and_notes()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));

        page.FindAll(".asset-card-preview")[0].DoubleClick();
        page.WaitForAssertion(() => Assert.Equal("a00", page.Find("#details-name").TextContent));
        page.Find("#details-favorite").Click();
        page.FindAll("#details-rating .mud-rating-item")[3].Click(); // the 4th star
        page.Find("textarea#details-notes").Input("use for level 2 rocks");
        page.WaitForElement("#details-notes-save").Click();

        await page.WaitForAssertionAsync(async () =>
        {
            await using var db = c.Db();
            var a = await db.Assets.SingleAsync(x => x.Name == "a00", Ct);
            Assert.Equal((true, 4, "use for level 2 rocks"), (a.Favorite, a.Rating, a.Notes));
        });
        // The card shows the favourite and the rating.
        page.WaitForAssertion(() =>
        {
            var card = Cards(page).Single(x => x.Instance.Name == "a00");
            Assert.True(card.Instance.Favorite);
            Assert.Equal(4, card.Instance.Rating);
        });
    }

    /// <summary>Spec "Fix cell size": a detected 32×32 grid changed to 32×48 is used by the overlay, frame count and viewer, and kept.</summary>
    [Fact]
    public async Task Sprite_grid_fix_cell_size()
    {
        await using var c = await TestCatalog.CreateAsync();
        var detected = SpriteSheetMetadata.FromGrid(new SpriteGrid { CellW = 32, CellH = 32, Columns = 8, Rows = 3 }, null, GridDetectedBy.Algorithm, 0.9);
        var src = await TestData.SourceAsync(c, 1, make: _ => new Asset
        {
            Name = "walk", Type = AssetType.SpriteSheet, Image = new ImageMetadata { Width = 256, Height = 96, IsPixelArt = true }, SpriteSheet = detected,
        });
        await using (var db = c.Db())
        {
            using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Orange, 256, 96);
            db.Previews.Add(new PreviewBlob { AssetId = src.Assets[0].Id, Kind = PreviewKind.Thumbnail, Mime = "image/png", Bytes = img.ToByteArray(ImageMagick.MagickFormat.Png) });
            await db.SaveChangesAsync(Ct);
        }
        Use(c);
        var viewer = JSInterop.SetupModule("./_content/AssetManager.Shared/js/spriteViewer.js");
        var page = RenderAt(new SearchQuery());
        page.WaitForAssertion(() => Assert.Single(Cards(page)));
        page.Find(".asset-card-preview").DoubleClick();
        page.WaitForAssertion(() => Assert.Contains("24 frames of 32 × 32 (8 × 3)", page.Find("#sprite-summary").TextContent));
        page.WaitForAssertion(() => Assert.Equal(24, ((IEnumerable<SpriteFrame>)viewer.Invocations["overlay"].Last().Arguments[2]!).Count()));

        page.Find("input#sprite-cell-h").Change("48");
        // Live: two rows of 48 now fit; the overlay shows 16 frames of 32×48.
        page.WaitForAssertion(() =>
        {
            Assert.Equal("2", page.Find("input#sprite-rows").GetAttribute("value"));
            Assert.Equal("16", page.Find("input#sprite-frame-count").GetAttribute("value"));
            var frames = ((IEnumerable<SpriteFrame>)viewer.Invocations["overlay"].Last().Arguments[2]!).ToList();
            Assert.Equal(16, frames.Count);
            Assert.All(frames, f => Assert.Equal((32, 48), (f.W, f.H)));
        });
        page.Find("#sprite-grid-save").Click();

        Asset saved = null!;
        await page.WaitForAssertionAsync(async () =>
        {
            await using var db = c.Db();
            saved = await db.Assets.SingleAsync(Ct);
            Assert.Equal((32, 48, 8, 2), (saved.SpriteSheet!.Grid!.CellW, saved.SpriteSheet.Grid.CellH, saved.SpriteSheet.Grid.Columns, saved.SpriteSheet.Grid.Rows));
        });
        Assert.Equal((16, GridDetectedBy.User), (saved.SpriteSheet!.FrameCount, saved.SpriteSheet.DetectedBy));
        Assert.True(saved.UserEdited.HasFlag(EditedFields.Grid));

        // The viewer plays row 2 with 32×48 frames.
        page.WaitForAssertion(() => Assert.Contains("16 frames of 32 × 48", page.Find("#sprite-summary").TextContent));
        page.Find("#sprite-play").Click();
        page.WaitForAssertion(() =>
        {
            var played = ((IEnumerable<SpriteFrame>)viewer.Invocations["play"].Last().Arguments[2]!).ToList();
            Assert.Equal(8, played.Count);
            Assert.All(played, f => Assert.Equal((0, 32, 48), (f.Y, f.W, f.H)));
        });

        // Re-detection and re-enrichment keep the user's grid.
        AssetManager.Processing.Sprites.SpriteSheetClassifier.Decide(saved, null, AssetManager.Processing.Sprites.GridResult.None);
        ItemPass.Apply(new ItemOutcome(saved, new ItemResult { Index = 1, Description = "Walk.", Type = AssetType.Image }, AiInputMode.Visual));
        Assert.Equal((AssetType.SpriteSheet, 48), (saved.Type, saved.SpriteSheet!.Grid!.CellH));
    }

    /// <summary>Design D18: a model's details show its renders as a turntable, not an image with a strip of thumbnails.</summary>
    [Fact]
    public async Task Details_of_a_model_show_the_turntable()
    {
        await using var c = await TestCatalog.CreateAsync();
        using var kit = new AssetManager.Tests.Processing.ProcessingKit(c.Store);
        await ModelTurntableUiTests.DetachedModelAsync(c, name: "boulder");
        await TestData.SourceAsync(c, 1, make: _ => new Asset { Name = "photo", Type = AssetType.Image });
        Use(c);
        var previews = new AssetManager.Processing.Handlers.PreviewService(c.Store, kit.Resolver,
            new AssetManager.Processing.Handlers.AssetProcessor(new AssetManager.Processing.Handlers.HandlerRegistry([new AssetManager.Processing.Handlers.FallbackHandler()])),
            new AssetManager.Processing.Settings.PreviewSettings(c.Store));
        Services.AddSingleton(previews);
        Services.AddSingleton(new AssetManager.Processing.Handlers.ModelTextureService(c.Store, kit.Resolver, previews,
            new AssetManager.Processing.Handlers.TextureMatcher(c.Store, kit.Resolver, new AssetManager.Processing.Settings.PreviewSettings(c.Store))));
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.Equal(2, Cards(page).Count));

        page.FindAll(".asset-card-name").Single(n => n.TextContent == "boulder").DoubleClick();

        page.WaitForAssertion(() => Assert.Equal(24, page.FindAll("#details-turntable .turntable-view").Count));
        Assert.Empty(page.FindAll("#details-views"));
        Assert.Empty(page.FindAll("#details-preview"));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("#details-textures #textures-none"))); // its textures (none here)

        // "3D view" with the drive detached falls back to the turntable with a note.
        page.Find("#details-3d").Click();
        page.WaitForAssertion(() => Assert.Contains("Original not available", page.Find(".viewer-fallback-note").TextContent));
        Assert.Equal(24, page.FindAll("#viewer-turntable .turntable-view").Count);
    }

    // ---- card context menu (spec "Asset card context menu", "Context menu scope", design D17) ----

    private static AngleSharp.Dom.IElement CardBody(IRenderedComponent<Browse> page, string name) =>
        page.FindAll(".asset-card").Single(card => card.QuerySelector(".asset-card-name")!.TextContent == name).QuerySelector(".asset-card-body")!;

    /// <summary>Scenario "Find similar from the menu"; the cards no longer have a find-similar button.</summary>
    [Fact]
    public async Task Find_similar_from_the_menu()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var index = new InMemoryVectorIndex(c.Store);
        long rock;
        await using (var db = c.Db())
        {
            rock = await db.Assets.Where(a => a.Name == "a03").Select(a => a.Id).SingleAsync(Ct);
            await db.Assets.ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Enriched), Ct);
            var v = new float[256];
            v[0] = 1;
            foreach (var id in await db.Assets.Select(a => a.Id).ToListAsync(Ct)) await index.UpsertAsync(id, v, "one", Ct);
        }
        var embeddings = new EmbeddingService(c.Store, index, () => new EmbeddingModel(new OneVector(), "one"));
        Services.AddSingleton(new CatalogSearch(c.Store, new AssetSearchService(c.Store), embeddings, index, ImageQueries));
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        Assert.Empty(page.FindAll(".find-similar"));

        CardBody(page, "a03").ContextMenu(new MouseEventArgs { ClientX = 300, ClientY = 240 });

        page.WaitForAssertion(() => Assert.Single(page.FindAll("#asset-menu")));
        var items = page.FindAll("#asset-menu .asset-menu-item");
        Assert.Equal(["Open details", "Find similar", "Copy name"], items.Select(i => i.TextContent.Trim()));
        Assert.All(items, i => Assert.NotNull(i.QuerySelector("svg"))); // every item has an icon
        Assert.Contains("left: min(300px", page.Find("#asset-menu").GetAttribute("style"));
        Assert.False(page.Find(".asset-menu-similar").HasAttribute("disabled"));

        page.Find(".asset-menu-similar").Click();

        page.WaitForAssertion(() => Assert.Contains("Similar to a03", page.Find("#similar-chip").TextContent));
        Assert.Equal(rock, SearchQuery.FromToken(System.Web.HttpUtility.ParseQueryString(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query)["q"]).SimilarTo);
        Assert.Empty(page.FindAll("#asset-menu"));
    }

    /// <summary>Scenario "Not enriched yet".</summary>
    [Fact]
    public async Task Find_similar_needs_enrichment()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));

        CardBody(page, "a01").ContextMenu(new MouseEventArgs { ClientX = 10, ClientY = 10 });

        page.WaitForAssertion(() => Assert.True(page.Find(".asset-menu-similar").HasAttribute("disabled")));
        Assert.Contains("Needs AI enrichment first", page.Find(".asset-menu-similar").TextContent);

        // Esc closes the menu; so does a click outside it.
        page.Find("#asset-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#asset-menu")));
        CardBody(page, "a01").ContextMenu();
        page.WaitForAssertion(() => page.Find(".asset-menu-backdrop").Click());
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#asset-menu")));
    }

    /// <summary>Scenario "Menu on an unselected card", and "Copy a name from the grid".</summary>
    [Fact]
    public async Task Menu_on_an_unselected_card_keeps_the_selection()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var module = JSInterop.SetupModule("./_content/AssetManager.Shared/js/browse.js");
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        IEnumerable<string> Selected() => page.Instance.Selected.Select(id => Cards(page).Single(card => card.Instance.AssetId == id).Instance.Name).Order();
        page.FindAll(".asset-card-preview")[1].Click();
        page.WaitForAssertion(() => Assert.Equal(["a01"], Selected()));
        page.FindAll(".asset-card-preview")[2].Click(new MouseEventArgs { CtrlKey = true });
        page.WaitForAssertion(() => Assert.Equal(["a01", "a02"], Selected()));

        CardBody(page, "a05").ContextMenu();
        page.WaitForAssertion(() => page.Find(".asset-menu-copy").Click());

        page.WaitForAssertion(() => Assert.Equal("a05", module.Invocations["copyText"].Single().Arguments[0]));
        Assert.Equal(["a01", "a02"], Selected());

        CardBody(page, "a05").ContextMenu();
        page.WaitForAssertion(() => page.Find(".asset-menu-open").Click());

        page.WaitForAssertion(() => Assert.Equal("a05", page.Find("#details-name").TextContent));
        Assert.Equal(["a01", "a02"], Selected());
    }

    /// <summary>Scenario "Keyboard": the menu key and Shift+F10 open the focused card's menu with its first item focused.</summary>
    [Fact]
    public async Task Keyboard_opens_the_menu_of_the_focused_card()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var module = JSInterop.SetupModule("./_content/AssetManager.Shared/js/browse.js");
        module.Setup<double[]?>("cardPoint", _ => true).SetResult([40, 64]);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        await page.InvokeAsync(() => page.Instance.OnShortcut("ArrowRight"));
        await page.InvokeAsync(() => page.Instance.OnShortcut("ArrowRight"));
        var focused = page.Instance.Selected.Single();

        await page.InvokeAsync(() => page.Instance.OnShortcut("Shift+F10"));

        page.WaitForAssertion(() => Assert.Equal(focused, page.Instance.Menu!.AssetId));
        Assert.Contains("left: min(40px", page.Find("#asset-menu").GetAttribute("style"));
        page.WaitForAssertion(() => Assert.Equal(0, module.Invocations["focusMenu"].Last().Arguments[0]));
        page.Find("#asset-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.False(page.Instance.Menu!.IsOpen));

        await page.InvokeAsync(() => page.Instance.OnShortcut("ContextMenu"));
        page.WaitForAssertion(() => Assert.Equal(focused, page.Instance.Menu!.AssetId));
        Assert.Equal([focused], page.Instance.Selected);
    }

    /// <summary>Every text embeds to the same unit vector (enough to rank a one-asset catalog).</summary>
    private sealed class OneVector : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
        {
            var v = new float[256];
            v[0] = 1;
            return Task.FromResult(new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>(
                values.Select(_ => new Microsoft.Extensions.AI.Embedding<float>(v))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task Image_query_chip_remove_and_expired()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        ImageQueries.Add(new ImageQuery("q1", new float[256], [], "A red mushroom.", ["mushroom"], "data:image/webp;base64,AAAA"));
        var page = RenderAt(new SearchQuery { ImageQueryId = "q1" });

        page.WaitForAssertion(() => Assert.Contains("Like this image", page.Find("#image-query-chip").TextContent));
        Assert.Equal("data:image/webp;base64,AAAA", page.Find("#image-query-chip img").GetAttribute("src"));
        Assert.Contains("needs the AI model", page.Find("#image-search-unavailable").TextContent); // no model in this setup

        page.Find("#image-query-chip .mud-chip-close-button").Click();
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#image-query-chip")));
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12)); // back to the plain search

        // After a restart the id is no longer in memory.
        var expired = RenderAt(new SearchQuery { ImageQueryId = "from-before-restart" });
        expired.WaitForAssertion(() => Assert.Contains("expired", expired.Find("#image-query-expired").TextContent));
        expired.WaitForAssertion(() => Assert.Contains("expired", expired.Find("#browse-error").TextContent));
    }

    [Fact]
    public async Task Dropped_image_searches_by_it()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 1, make: _ => new Asset { Name = "mushroom", Type = AssetType.Image });
        Use(c);
        var index = new InMemoryVectorIndex(c.Store);
        var v = new float[256];
        v[0] = 1;
        await index.UpsertAsync(src.Assets[0].Id, v, "one", Ct);
        var embeddings = new EmbeddingService(c.Store, index, () => new EmbeddingModel(new OneVector(), "one"));
        Services.AddSingleton(new CatalogSearch(c.Store, new AssetSearchService(c.Store), embeddings, index, ImageQueries));
        var chat = new AssetManager.Tests.Ai.FakeChatClient(
            """{"items":[{"index":1,"description":"A red mushroom.","type":"image","tags":["mushroom"],"colors":["red"],"style":[],"is_game_asset":true}]}""");
        Services.AddSingleton(new ImageQueryService(embeddings, ImageQueries) { Chat = () => chat, Modalities = () => new AssetManager.AI.Runtime.AiModalities(true, false) });
        var page = RenderAt(new SearchQuery());
        page.WaitForAssertion(() => Assert.Contains("Drop or paste an image", page.Find("#image-drop-zone").TextContent));
        using var img = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 32, 32);

        await page.InvokeAsync(() => page.Instance.OnImageDropped(Convert.ToBase64String(img.ToByteArray(ImageMagick.MagickFormat.Png))));

        page.WaitForAssertion(() => Assert.Single(page.FindAll("#image-query-chip")));
        page.WaitForAssertion(() => Assert.Equal(["mushroom"], Cards(page).Select(x => x.Instance.Name)));
        Assert.Single(chat.Requests);
        Assert.Contains("?q=", Services.GetRequiredService<NavigationManager>().Uri); // the query id is in the URL token
    }

    [Fact]
    public async Task Grid_uncertain_filter()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 4, make: i => new Asset { Name = $"g{i}", Type = AssetType.Image, GridUncertain = i == 2 });
        Use(c);
        var page = RenderAt(new SearchQuery());
        page.WaitForAssertion(() => Assert.Equal(4, Cards(page).Count));

        page.Find("input#grid-uncertain-filter").Change(true);

        page.WaitForAssertion(() => Assert.Equal(["g2"], Cards(page).Select(x => x.Instance.Name)));
    }

    [Fact]
    public async Task Sprite_sheet_toggle_off_makes_an_image()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 1, make: _ => new Asset
        {
            Name = "tiles", Type = AssetType.SpriteSheet, Image = new ImageMetadata { Width = 64, Height = 64 },
            SpriteSheet = SpriteSheetMetadata.FromGrid(new SpriteGrid { CellW = 32, CellH = 32, Columns = 2, Rows = 2 }, null, GridDetectedBy.Algorithm),
        });
        Use(c);
        var page = RenderAt(new SearchQuery());
        page.WaitForAssertion(() => Assert.Single(Cards(page)));
        page.Find(".asset-card-preview").DoubleClick();
        page.WaitForElement("input#sprite-is-sheet").Change(false);

        await page.WaitForAssertionAsync(async () =>
        {
            await using var db = c.Db();
            var a = await db.Assets.SingleAsync(Ct);
            Assert.Equal((AssetType.Image, (SpriteSheetMetadata?)null), (a.Type, a.SpriteSheet));
            Assert.True(a.UserEdited.HasFlag(EditedFields.Grid));
        });
    }

    [Fact]
    public async Task Favourites_with_a_minimum_rating()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 6, make: i => new Asset
        {
            Name = $"r{i}", Favorite = i % 2 == 0, Rating = i, // favourites: r0 (0★), r2 (2★), r4 (4★)
            Notes = i == 5 ? "for the swamp level" : null,
        });
        Use(c);
        Render<MudBlazor.MudPopoverProvider>();
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.Equal("6 assets", page.Find("#result-count").TextContent));

        page.Find("input#favorites-filter, #favorites-filter input").Change(true);
        page.WaitForAssertion(() => Assert.Equal("3 assets", page.Find("#result-count").TextContent));
        await page.InvokeAsync(() => page.FindComponents<MudBlazor.MudSelect<int>>().Single().Instance.ValueChanged.InvokeAsync(4));
        page.WaitForAssertion(() => Assert.Equal("1 asset", page.Find("#result-count").TextContent));
        page.WaitForAssertion(() => Assert.Equal(["r4"], Cards(page).Select(x => x.Instance.Name)));
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query)["q"];
        Assert.Equal((true, 4), (SearchQuery.FromToken(token).FavoritesOnly, SearchQuery.FromToken(token).MinRating));

        var notes = RenderAt(new SearchQuery { HasNotes = true });
        notes.WaitForAssertion(() => Assert.Equal("1 asset", notes.Find("#result-count").TextContent));
    }

    [Fact]
    public async Task Keyboard_moves_focus_rates_favourites_opens_and_deletes()
    {
        await using var c = await TestCatalog.CreateAsync();
        await SeedAsync(c);
        Use(c);
        var dialogs = Render<MudBlazor.MudDialogProvider>();
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.True(Cards(page).Count >= 12));
        Task Key(string key) => page.InvokeAsync(() => page.Instance.OnShortcut(key));
        string Focused() => Cards(page).Single(x => x.Instance.Focused).Instance.Name;
        async Task<Asset> AssetAsync(string name)
        {
            await using var db = c.Db();
            return await db.Assets.SingleAsync(a => a.Name == name, Ct);
        }

        // Arrows move the focus (4 cards per row) and select the focused card; Shift extends.
        await Key("ArrowRight");
        page.WaitForAssertion(() => Assert.Equal("a00", Focused()));
        await Key("ArrowRight");
        await Key("ArrowDown");
        page.WaitForAssertion(() => Assert.Equal("a05", Focused()));
        Assert.Single(page.Instance.Selected);
        await Key("Shift+ArrowRight");
        page.WaitForAssertion(() => Assert.Equal(2, page.Instance.Selected.Count));
        await Key("ArrowLeft");

        // Spec "Rate with the keyboard" and F for favourite.
        await Key("4");
        await page.WaitForAssertionAsync(async () => Assert.Equal(4, (await AssetAsync("a05")).Rating));
        await Key("f");
        await page.WaitForAssertionAsync(async () => Assert.True((await AssetAsync("a05")).Favorite));
        await Key("f");
        await page.WaitForAssertionAsync(async () => Assert.False((await AssetAsync("a05")).Favorite));

        // Enter opens the details, Esc closes them.
        await Key("Enter");
        page.WaitForAssertion(() => Assert.Equal("a05", page.Find("#details-name").TextContent));
        await Key("Escape");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#details-name")));
        Assert.Single(page.Instance.Selected); // the first Esc only closed the details

        // Delete asks first; cancelling keeps the asset.
        var delete = Key("Delete"); // completes when the confirmation closes
        dialogs.WaitForAssertion(() => Assert.Contains("Delete 1 asset from the catalog?", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim().Equals("Cancel", StringComparison.OrdinalIgnoreCase)).Click();
        await delete;
        await using (var db = c.Db()) Assert.Equal(12, await db.Assets.CountAsync(Ct));

        // ? lists the same shortcuts the handler uses.
        _ = Key("?");
        dialogs.WaitForAssertion(() => Assert.Contains("Keyboard shortcuts", dialogs.Markup));
        foreach (var s in BrowseShortcuts.All) Assert.Contains(s.Description, dialogs.Markup);
    }

    [Fact]
    public async Task Details_show_source_tags_with_their_origin()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 2, make: i => new Asset { Name = $"s{i}" });
        await new SourceTagService(c.Store).AddAsync(src.Id, ["pixel art"], ct: Ct);
        Use(c);
        var page = RenderAt(new SearchQuery { Sort = SearchSort.Name });
        page.WaitForAssertion(() => Assert.Equal(2, Cards(page).Count));

        page.FindAll(".asset-card-preview")[0].DoubleClick();
        page.WaitForAssertion(() =>
        {
            var chip = page.Find("#asset-details .mud-chip[data-origin=Source]");
            Assert.Contains("pixel art", chip.TextContent);
            Assert.Contains("From the source", chip.GetAttribute("title"));
        });
    }
}
