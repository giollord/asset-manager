using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Data.Jobs;
using AssetManager.Shared.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class JobUiTests : BunitContext
{
    private readonly ProgressHub _hub = new();

    private sealed class NoQueue : IJobQueue
    {
        public List<long> Retried { get; } = [];
        public Task<long> EnqueueAsync(string type, string title, object payload, long? parentJobId = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task CancelAsync(long jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long?> RetryFailedAsync(long jobId, CancellationToken ct = default) { Retried.Add(jobId); return Task.FromResult<long?>(99); }
        public Task<JobSnapshot> WaitAsync(long jobId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly NoQueue _queue = new();

    public JobUiTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IProgressHub>(_hub);
        Services.AddSingleton<IJobQueue>(_queue);
        Services.AddScoped<AssetManager.Shared.Services.UiActions>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static JobSnapshot Job(long id, JobState state, int errors = 0, string? stage = null, int stageIndex = 0, int stageCount = 1,
        long done = 0, long total = 0, string? summary = null) =>
        new(id, "import", "Import Forest", state, stage, stageIndex, stageCount, done, total, "Assets/Tree_07.fbx", 0, errors,
            DateTime.UtcNow.AddMinutes(-3), DateTime.UtcNow.AddMinutes(-3), state is JobState.Pending or JobState.Running ? null : DateTime.UtcNow,
            state == JobState.Running ? 95 : null, null, summary, null);

    [Fact]
    public void Notification_on_completion()
    {
        var snackbars = Render<MudBlazor.MudSnackbarProvider>();
        Render<JobNotifications>();

        _hub.Finish(Job(42, JobState.Completed, errors: 3));

        snackbars.WaitForAssertion(() => Assert.Contains("Import Forest finished with 3 errors.", snackbars.Markup));
        var details = snackbars.FindAll("button").Single(b => b.TextContent.Contains("Details"));
        details.Click();
        Assert.EndsWith("/jobs?job=42", Services.GetRequiredService<NavigationManager>().Uri);

        Assert.Equal(("Import Forest finished.", MudBlazor.Severity.Success), JobNotifications.Describe(Job(1, JobState.Completed)));
        Assert.Equal(MudBlazor.Severity.Info, JobNotifications.Describe(Job(1, JobState.Cancelled)).Severity);
    }

    [Fact]
    public void Progress_view_shows_stage_counts_item_eta_and_retry()
    {
        var running = Render<JobProgressView>(p => p.Add(x => x.Job, Job(7, JobState.Running, stage: "AI item pass", stageIndex: 4, stageCount: 6, done: 40, total: 120)));

        var stage = running.Find(".job-stage").TextContent;
        Assert.Contains("Stage 5/6:", stage);
        Assert.Contains("AI item pass", stage);
        Assert.Contains("40 / 120", stage);
        Assert.Contains("Assets/Tree_07.fbx", running.Find(".job-item").TextContent);
        Assert.Contains("1 min 35 s left", running.Find(".job-times").TextContent);
        Assert.Contains("Elapsed 3 min", running.Find(".job-times").TextContent);

        var finished = Render<JobProgressView>(p => p.Add(x => x.Job, Job(8, JobState.Completed, errors: 7, summary: """{"updated":3,"added":1,"removed":0}""")));
        Assert.Contains("3 updated, 1 added, 0 removed", finished.Find(".job-times").TextContent);
        Assert.Contains("7 errors", finished.Find(".job-times").TextContent);
        finished.Find(".retry-failed").Click();
        Assert.Equal([8L], _queue.Retried);

        // Spec "Selectable text": the current item and the error message can be copied.
        Assert.Contains("selectable", running.Find(".job-item").ClassList);
        var failed = Render<JobProgressView>(p => p.Add(x => x.Job, Job(9, JobState.Failed) with { Error = "Disk full" }));
        Assert.Equal("Disk full", failed.Find(".job-error").TextContent);
        Assert.Contains("selectable", failed.Find(".job-error").ClassList);
    }

    [Fact]
    public void Indicator_shows_active_count_and_disappears_when_done()
    {
        var popovers = Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<JobsIndicator>();
        // Spec "Idle app bar": no indicator and no jobs button while nothing runs.
        Assert.Empty(cut.Markup.Trim());

        _hub.Publish(Job(1, JobState.Running, stage: "Scan", done: 1, total: 4));
        _hub.Publish(Job(2, JobState.Pending));
        cut.WaitForAssertion(() => Assert.Equal("2 jobs · 25%", cut.Find("#jobs-indicator").TextContent.Trim()));

        // Hovering opens the quick view, which names the file being processed.
        _hub.Publish(Job(1, JobState.Running, stage: "Scan", done: 1, total: 4) with { CurrentItem = "PNG/ship.png" });
        cut.Find("#jobs-indicator").Closest("div.d-inline-block")!.TriggerEvent("onmouseenter", new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        popovers.WaitForAssertion(() => Assert.Contains("PNG/ship.png", popovers.Find("#jobs-quick-view").TextContent));

        // Clicking the chip opens the Jobs page.
        cut.Find("#jobs-indicator").Click();
        Assert.EndsWith("/jobs", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);

        _hub.Finish(Job(1, JobState.Completed));
        _hub.Finish(Job(2, JobState.Completed));
        cut.WaitForAssertion(() => Assert.Empty(cut.Markup.Trim()));
    }
}
