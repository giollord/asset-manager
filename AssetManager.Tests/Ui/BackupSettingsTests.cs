using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using AssetManager.Data.Jobs;
using AssetManager.Data.Services;
using AssetManager.Shared.Components;
using AssetManager.Shared.Services;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>Settings → Backups (spec "Restore").</summary>
public class BackupSettingsTests : BunitContext
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Restore_asks_first_and_says_jobs_are_cancelled()
    {
        await using var c = await TestCatalog.CreateAsync();
        await TestData.SourceAsync(c, 2);
        var settings = new JsonAppSettingsStore(Path.Combine(c.Dir.Path, "settings.json"));
        settings.Update(s => s.BackupFolder = Path.Combine(c.Dir.Path, "backups"));
        var hub = new ProgressHub();
        var jobs = new JobQueue(c.Store, [], hub);
        var backups = new CatalogBackupService(c.Store, settings, jobs, hub) { Clock = () => new DateTime(2026, 11, 24, 9, 30, 0), FreeSpace = _ => long.MaxValue };
        await backups.BackupAsync(manual: false, Ct);
        await TestData.SourceAsync(c, 3); // changed since the backup
        Services.AddMudServices();
        Services.AddSingleton(c.Store);
        Services.AddSingleton(backups);
        Services.AddSingleton<IAppSettingsStore>(settings);
        Services.AddSingleton<IProgressHub>(hub);
        Services.AddSingleton<IJobQueue>(jobs);
        Services.AddSingleton<IFolderPicker>(new NoPicker());
        Services.AddScoped<UiActions>();
        Services.AddLogging();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();

        var section = Render<BackupSettings>();

        var row = section.Find(".backup-row");
        Assert.Contains("Automatic", row.TextContent);
        Assert.Contains(new DateTime(2026, 11, 24, 9, 30, 0).ToString("g"), row.TextContent);
        Assert.Contains(Path.Combine(c.Dir.Path, "backups"), section.Find("#backup-folder").TextContent);

        section.Find(".backup-restore").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Running jobs (imports, AI enrichment, exports) are cancelled first.", dialogs.Markup));
        Assert.Contains("saved as a manual backup", dialogs.Markup);
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Running jobs", dialogs.Markup));
        await using (var db = c.Db()) Assert.Equal(2, await db.Sources.CountAsync(Ct)); // nothing restored
        Assert.Single(backups.List());

        section.Find(".backup-restore").Click();
        dialogs.WaitForAssertion(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel jobs and restore").Click());
        section.WaitForAssertion(() => Assert.Equal(2, section.FindAll(".backup-row").Count), TimeSpan.FromSeconds(10));
        await using (var db = c.Db()) Assert.Equal(1, await db.Sources.CountAsync(Ct));
        Assert.Contains("Manual", section.FindAll(".backup-row")[0].TextContent);
    }
}
