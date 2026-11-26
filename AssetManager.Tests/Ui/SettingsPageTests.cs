using AngleSharp.Dom;
using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Shared;
using AssetManager.Shared.Components;
using AssetManager.Shared.Pages;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace AssetManager.Tests.Ui;

/// <summary>
/// The Settings page on the real service graph (specs "Settings section navigation", "Settings search" and
/// "Dark and light theme"; design D8).
/// </summary>
public class SettingsPageTests : BunitContext, IAsyncLifetime
{
    private TestCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        _catalog = await TestCatalog.CreateAsync();
        Services.AddLogging();
        Services.AddSingleton(_catalog.Store);
        Services.AddSingleton(new AppDataLocation(_catalog.Dir.Path, DataMode.Portable));
        Services.AddSingleton<IFolderPicker, NoPlatform>();
        Services.AddSingleton<IFilePicker, NoPlatform>();
        Services.AddSingleton<IClipboardFileService, NoPlatform>();
        Services.AddSingleton<IShellService, NoPlatform>();
        Services.AddAssetManager(o => o.SettingsPath = Path.Combine(_catalog.Dir.Path, "settings.json"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _catalog?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class NoPlatform : IFolderPicker, IFilePicker, IClipboardFileService, IShellService
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    private IRenderedComponent<Settings> RenderPage()
    {
        Render<MudBlazor.MudPopoverProvider>();
        var page = Render<Settings>();
        page.WaitForAssertion(() => Assert.Equal(SettingsSection.All.Count, page.FindAll(".settings-section").Count));
        return page;
    }

    private static string[] ShownSections(IRenderedComponent<Settings> page) =>
        page.FindAll(".settings-section:not([hidden])").Select(e => e.GetAttribute("data-section")!).ToArray();

    private static string[] ListedSections(IRenderedComponent<Settings> page) =>
        page.FindAll(".settings-section-link").Select(e => e.GetAttribute("data-section")!).ToArray();

    private static void Search(IRenderedComponent<Settings> page, string text) => page.Find("input#settings-search").Input(text);

    [Fact]
    public void Section_list_shows_every_section_in_page_order()
    {
        var page = RenderPage();
        string[] all = ["appearance", "catalog", "backups", "ai-model", "import", "library", "previews", "maintenance", "about"];
        Assert.Equal(all, SettingsSection.All.Select(s => s.Id).ToArray());
        Assert.Equal(all, ListedSections(page));
        Assert.Equal(all, ShownSections(page));
        Assert.Equal(SettingsSection.All.Select(s => s.Title).ToArray(), page.FindAll(".settings-section-link").Select(e => e.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void Find_a_setting()
    {
        var page = RenderPage();
        Search(page, "CACHE");
        page.WaitForAssertion(() => Assert.Equal(["import"], ShownSections(page)));
        Assert.Equal(["import"], ListedSections(page));
        Assert.Empty(page.FindAll("#settings-no-match"));
    }

    [Fact]
    public void No_match_then_clear_search()
    {
        var page = RenderPage();
        Search(page, "no such setting");
        page.WaitForAssertion(() => Assert.Empty(ShownSections(page)));
        Assert.Empty(ListedSections(page));
        Assert.Contains("No settings match", page.Find("#settings-no-match").TextContent);

        Search(page, "");
        page.WaitForAssertion(() => Assert.Equal(SettingsSection.All.Count, ShownSections(page).Length));
        Assert.Equal(SettingsSection.All.Count, ListedSections(page).Length);
        Assert.Empty(page.FindAll("#settings-no-match"));
    }

    [Fact]
    public void Jump_to_a_section_scrolls_it_into_view_and_marks_it_current()
    {
        var module = JSInterop.SetupModule("./_content/AssetManager.Shared/js/layout.js");
        module.SetupVoid("scrollIntoView", "settings-about");
        var page = RenderPage();

        page.Find(".settings-section-link[data-section='about']").Click();

        Assert.Equal("settings-about", module.VerifyInvoke("scrollIntoView").Arguments.Single());
        page.WaitForAssertion(() => Assert.Equal(["about"],
            page.FindAll(".settings-section-link.mud-selected-item").Select(e => e.GetAttribute("data-section")).ToArray()));
    }

    /// <summary>
    /// Drift guard (design D8): every field label a section renders is one of its search terms, so a setting added to a
    /// section later cannot be missed by the search.
    /// </summary>
    [Fact]
    public void Every_rendered_field_label_is_a_search_term()
    {
        var page = RenderPage();
        foreach (var section in SettingsSection.All)
        {
            var root = page.Find($"#settings-{section.Id}");
            var labels = Labels(root).ToList();
            Assert.NotEmpty(labels);
            var missing = labels.Where(l => !section.SearchTerms.Contains(l)).ToList();
            Assert.True(missing.Count == 0, $"Section '{section.Id}' is missing search terms: {string.Join(", ", missing)}");
            Assert.Contains(section.Title, section.SearchTerms);
        }
    }

    /// <summary>
    /// The section title and group headings (MudText renders h6 and subtitles as h6), field and option labels, and the
    /// row names of its tables.
    /// </summary>
    private static IEnumerable<string> Labels(IElement root) =>
        root.QuerySelectorAll("h6, label, .mud-simple-table tr > td:first-child")
            .Select(e => e.TextContent.Trim())
            .Where(t => t.Length > 0)
            .Distinct();

    [Fact]
    public void Catalog_path_and_model_descriptions_are_selectable()
    {
        var page = RenderPage();
        Assert.Equal(_catalog.Path, page.Find("#catalog-path").TextContent);
        Assert.Contains("selectable", page.Find("#catalog-path").ClassList);
        Assert.Contains("selectable", page.Find("#models-intro").ClassList);
        var description = page.FindAll(".pkg-description").First();
        Assert.NotNull(description.Closest(".selectable"));
    }

    [Fact]
    public void Theme_in_settings_persists_and_shows_as_selected()
    {
        var page = RenderPage();
        var store = Services.GetRequiredService<IAppSettingsStore>();
        Assert.Equal(ThemeMode.System, store.Current.Theme);

        page.Find("#appearance-theme .theme-light").Click();

        Assert.Equal(ThemeMode.Light, store.Current.Theme);
        Assert.Equal(ThemeMode.Light, new JsonAppSettingsStore(Path.Combine(_catalog.Dir.Path, "settings.json")).Current.Theme);
        page.WaitForAssertion(() => Assert.Contains("mud-toggle-item-selected", page.Find("#appearance-theme .theme-light").ClassList));
        Assert.DoesNotContain("mud-toggle-item-selected", page.Find("#appearance-theme .theme-dark").ClassList);
    }
}
