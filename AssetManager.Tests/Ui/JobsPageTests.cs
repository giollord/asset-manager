using System.Data.Common;
using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data;
using AssetManager.Data.Jobs;
using AssetManager.Tests.Data;
using Bunit;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

/// <summary>The Jobs page against a real catalog whose AI status query can be held open.</summary>
public class JobsPageTests : BunitContext
{
    /// <summary>Holds every query on the assets' AI status until <see cref="Release"/>.</summary>
    private sealed class AiStatusGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held;

        public void Release() => _open.TrySetResult();

        private async Task WaitAsync(DbCommand command, CancellationToken ct)
        {
            if (!command.CommandText.Contains("\"AiStatus\"")) return;
            Interlocked.Increment(ref Held);
            await _open.Task.WaitAsync(ct);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await WaitAsync(command, ct);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken ct = default)
        {
            await WaitAsync(command, ct);
            return result;
        }
    }

    [Fact]
    public async Task Job_clock_ticks_while_the_ai_status_is_slow()
    {
        await using var catalog = await TestCatalog.CreateAsync();
        var gate = new AiStatusGate();
        var store = new CatalogStore(interceptors: [gate]);
        await store.OpenAsync(catalog.Path, Xunit.TestContext.Current.CancellationToken);
        var hub = new ProgressHub();
        await using var queue = new JobQueue(store, [], hub);
        var settings = new AssetManager.Core.Settings.JsonAppSettingsStore(Path.Combine(catalog.Dir.Path, "settings.json"));
        var ai = new AiQueue(store, new AiRuntime(settings), queue, hub);

        Services.AddMudServices();
        Services.AddSingleton<IProgressHub>(hub);
        Services.AddSingleton(queue);
        Services.AddSingleton<IJobQueue>(queue);
        Services.AddSingleton(ai);
        Services.AddSingleton(store);
        Services.AddScoped<AssetManager.Shared.Services.UiActions>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        try
        {
            hub.Publish(new JobSnapshot(5, "import", "Import Forest", JobState.Running, "Scanning", 0, 6, 0, 0, "big.zip", 0, 0,
                DateTime.UtcNow.AddSeconds(-58.5), DateTime.UtcNow.AddSeconds(-58.5), null, null, null, null, null));
            var page = Render<AssetManager.Shared.Pages.Jobs>();

            page.WaitForAssertion(() => Assert.Contains("Elapsed 58 s", page.Find("#job-5 .job-times").TextContent));
            page.WaitForAssertion(() => Assert.True(gate.Held > 0));
            // The AI status query is still held, and no job event arrives, yet the clock moves on.
            page.WaitForAssertion(() => Assert.Contains("Elapsed 1 min", page.Find("#job-5 .job-times").TextContent), TimeSpan.FromSeconds(5));
            Assert.Null(page.FindAll("#ai-pending").SingleOrDefault());
        }
        finally
        {
            gate.Release();
        }
    }
}
