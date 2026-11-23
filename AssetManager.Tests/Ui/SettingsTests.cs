using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Core.Settings;
using AssetManager.Data.Services;
using AssetManager.Processing.Settings;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>Spec "Settings" and "Preview settings".</summary>
public class SettingsTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private sealed class BusyHub(bool busy) : IProgressHub
    {
        public IReadOnlyList<JobSnapshot> Active => busy
            ? [new JobSnapshot(1, "import", "Import", JobState.Running, null, 0, 1, 0, 0, null, 0, 0, DateTime.UtcNow, null, null, null, null, null, null)]
            : [];

        public JobSnapshot? Get(long jobId) => null;

        public event Action<JobSnapshot>? Changed { add { } remove { } }

        public event Action<JobSnapshot>? Finished { add { } remove { } }
    }

    [Fact]
    public async Task Change_catalog_location()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 3);
        var settings = new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json"));
        settings.Update(s => s.CatalogPath = c.Path);
        var location = new CatalogLocationService(c.Store, settings, new BusyHub(false));
        var other = Path.Combine(c.Dir.Path, "Other", "assets.amdb");

        // A path that does not exist: the catalog is created there and opened, empty.
        Assert.True(await location.SwitchAsync(other, Ct));
        Assert.True(File.Exists(other));
        Assert.Equal(other, c.Store.Path);
        Assert.Equal(other, settings.Current.CatalogPath);
        await using (var db = c.Db())
            Assert.Equal(0, await db.Assets.CountAsync(Ct));

        // Back to the first catalog: its assets are there again, and the setting follows.
        Assert.False(await location.SwitchAsync(c.Path, Ct));
        Assert.Equal(c.Path, new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")).Current.CatalogPath);
        await using (var db = c.Db())
            Assert.Equal(3, await db.Assets.CountAsync(Ct));

        // A folder gets a new catalog file inside it.
        var folder = Path.Combine(c.Dir.Path, "Folder");
        Directory.CreateDirectory(folder);
        Assert.True(await location.SwitchAsync(folder, Ct));
        Assert.Equal(Path.Combine(folder, "catalog.amdb"), c.Store.Path);
    }

    [Fact]
    public async Task Catalog_switch_is_refused_while_jobs_run()
    {
        await using var c = await TestCatalog.CreateAsync();
        var settings = new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json"));
        var location = new CatalogLocationService(c.Store, settings, new BusyHub(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => location.SwitchAsync(Path.Combine(c.Dir.Path, "b.amdb"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => location.SwitchAsync("relative.amdb", Ct));
        Assert.Equal(c.Path, c.Store.Path);
    }

    [Fact]
    public async Task Preview_form_shows_every_option_validates_and_resets()
    {
        await using var c = await TestCatalog.CreateAsync();
        var previews = new PreviewSettings(c.Store);
        Services.AddMudServices();
        Services.AddSingleton(previews);
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var form = Render<PreviewSettingsForm>();
        var settable = typeof(PreviewOptions).GetProperties().Count(p => p.CanWrite);
        Assert.Equal(settable, form.FindAll("[data-field]").Select(e => e.GetAttribute("data-field")).Distinct().Count());
        Assert.Contains("Default: 384", form.Find(".field-RenderSize").ParentElement!.ParentElement!.TextContent);

        // Out of range: an error, and Save stays disabled.
        form.Find(".field-AudioClipSeconds input").Input("90");
        form.WaitForAssertion(() => Assert.Contains("Must be between 1 and 60", form.Markup));
        Assert.True(form.Find("#preview-save").HasAttribute("disabled"));

        // A valid value saves to the catalog.
        form.Find(".field-AudioClipSeconds input").Input("20");
        form.WaitForAssertion(() => Assert.False(form.Find("#preview-save").HasAttribute("disabled")));
        form.Find("#preview-save").Click();
        form.WaitForAssertion(async () => Assert.Equal(20, (await new PreviewSettings(c.Store).GetAsync(Ct)).AudioClipSeconds));

        // Reset brings back the documented defaults.
        form.Find("#preview-reset").Click();
        form.WaitForAssertion(async () => Assert.Equal(new PreviewOptions().Version(), (await new PreviewSettings(c.Store).GetAsync(Ct)).Version()));
    }

    [Theory]
    [InlineData(".CS, js  .py", new[] { ".cs", ".js", ".py" })]
    [InlineData("meta\n.meta tmp", new[] { ".meta", ".tmp" })]
    public void Extension_lists_are_normalized(string text, string[] expected) =>
        Assert.Equal(expected, ImportSettings.Parse(text));
}
