using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data.Jobs;
using AssetManager.Data.Services;
using AssetManager.Processing.Import;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class BulkActionBarTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private (IRenderedComponent<MudDialogProvider> Dialogs, IRenderedComponent<BulkActionBar> Bar) Render(TestCatalog c, IReadOnlyCollection<long> selection, Action? onDeleted = null)
    {
        var jobs = new JobQueue(c.Store, [], new ProgressHub());
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton(new TagService(c.Store));
        Services.AddSingleton(new MetadataEditService(c.Store));
        Services.AddSingleton(new DeletionService(c.Store));
        Services.AddSingleton<IJobQueue>(jobs);
        Services.AddSingleton(new ReimportService(jobs));
        Services.AddSingleton(new AssetManager.AI.Enrichment.ReenrichService(c.Store, jobs, new AssetManager.AI.Runtime.AiRuntime(new AssetManager.Core.Settings.JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json")))));
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var dialogs = Render<MudDialogProvider>();
        var bar = Render<BulkActionBar>(p => p.Add(x => x.Selection, selection).Add(x => x.OnDeleted, () => onDeleted?.Invoke()));
        return (dialogs, bar);
    }

    private static async Task<int> CountAsync(TestCatalog c)
    {
        await using var db = c.Db();
        return await db.Assets.CountAsync(Ct);
    }

    [Fact]
    public async Task Delete_requires_confirmation()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 20);
        var selection = src.Assets.Take(12).Select(a => a.Id).ToList();
        var deleted = 0;
        var (dialogs, bar) = Render(c, selection, () => deleted++);

        // Delete, then cancel: nothing is removed.
        bar.Find(".bulk-delete button, button.bulk-delete").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Delete 12 assets from the catalog?", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim().Equals("Cancel", StringComparison.OrdinalIgnoreCase)).Click();
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("from the catalog?", dialogs.Markup));
        Assert.Equal(20, await CountAsync(c));
        Assert.Equal(0, deleted);

        // Delete, then confirm: exactly the 12 selected assets are removed.
        bar.Find(".bulk-delete button, button.bulk-delete").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Delete 12 assets from the catalog?", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim().Equals("Delete 12 assets", StringComparison.OrdinalIgnoreCase)).Click();
        bar.WaitForAssertion(() => Assert.Equal(1, deleted));
        Assert.Equal(8, await CountAsync(c));
        await using var db = c.Db();
        Assert.False(await db.Assets.AnyAsync(a => selection.Contains(a.Id), Ct));
    }

    [Fact]
    public async Task Bulk_tag_and_untag_apply_to_the_selection()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 6);
        var selection = src.Assets.Take(4).Select(a => a.Id).ToList();
        var (dialogs, bar) = Render(c, selection);

        bar.Find("button.bulk-tag, .bulk-tag button").Click();
        dialogs.WaitForAssertion(() => dialogs.Find(".tag-dialog-input input"));
        var input = dialogs.Find(".tag-dialog-input input");
        input.Input("Medieval");
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        dialogs.WaitForAssertion(() => Assert.Contains("medieval", dialogs.Find("#tag-dialog-choices").TextContent));
        dialogs.Find("button.tag-dialog-ok").Click();
        bar.WaitForAssertion(async () => Assert.Equal(4, await TaggedAsync("medieval")));

        bar.Find("button.bulk-untag, .bulk-untag button").Click();
        dialogs.WaitForAssertion(() => dialogs.Find(".tag-choice[data-tag='medieval']"));
        dialogs.Find(".tag-choice[data-tag='medieval']").Click();
        dialogs.Find("button.tag-dialog-ok").Click();
        bar.WaitForAssertion(async () => Assert.Equal(0, await TaggedAsync("medieval")));

        async Task<int> TaggedAsync(string tag)
        {
            await using var db = c.Db();
            return await db.AssetTags.CountAsync(t => t.Tag.Name == tag, Ct);
        }
    }

    [Fact]
    public async Task Bulk_favourite_and_rate_apply_to_the_selection()
    {
        await using var c = await TestCatalog.CreateAsync();
        var src = await TestData.SourceAsync(c, 25);
        var selection = src.Assets.Take(20).Select(a => a.Id).ToList();
        var (_, bar) = Render(c, selection);
        var popovers = Render<MudPopoverProvider>();

        // Spec "Bulk favourite".
        bar.Find(".bulk-favorite button, button.bulk-favorite").Click();
        popovers.WaitForElement(".bulk-favorite-add").Click();
        await bar.WaitForAssertionAsync(async () =>
        {
            await using var db = c.Db();
            Assert.Equal(20, await db.Assets.CountAsync(a => a.Favorite, Ct));
        });

        bar.Find(".bulk-rate button, button.bulk-rate").Click();
        popovers.WaitForElement(".bulk-rate-4").Click();
        await bar.WaitForAssertionAsync(async () =>
        {
            await using var db = c.Db();
            Assert.Equal(20, await db.Assets.CountAsync(a => a.Rating == 4 && selection.Contains(a.Id), Ct));
            Assert.Equal(0, await db.Assets.CountAsync(a => a.Rating != 0 && !selection.Contains(a.Id), Ct));
        });
    }
}
