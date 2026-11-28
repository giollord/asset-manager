using AssetManager.AI.Import;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using AssetManager.Processing.Jobs;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

/// <summary>Spec "Imports commit without confirmation", "Cancelling an import" and "Import results".</summary>
public class ImportCommitTests
{
    [Fact]
    public async Task Assets_appear_after_previews_while_ai_still_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        kit.Ai.Pause(); // the AI stages wait at the gate, as if the model were still busy

        var session = await kit.Imports.StartAsync(new ImportRequest([folder]), ct);
        await kit.WaitCommittedAsync(session.Id); // no confirmation

        await using (var db = kit.Catalog.Db())
        {
            var committed = await db.Assets.Include(a => a.Source).ToListAsync(ct); // the catalog view
            Assert.Equal(3, committed.Count);
            Assert.All(committed, a => Assert.Equal(AiStatus.Pending, a.AiStatus));
            Assert.All(committed, a => Assert.Equal(session.Id, a.Source.CommittedFromSessionId));
            foreach (var a in committed)
                Assert.True(await db.Previews.AnyAsync(p => p.AssetId == a.Id, ct), a.Name);
            Assert.Equal(0, await db.AllAssets.CountAsync(a => a.ImportSessionId != null, ct));
            Assert.Equal(0, await db.AllSources.CountAsync(s => s.ImportSessionId != null, ct));
            Assert.Equal(ImportSessionState.Committed, (await db.ImportSessions.SingleAsync(ct)).State);
            Assert.Equal(0, await db.Jobs.CountAsync(j => j.Type == ImportCommitJob.JobType, ct)); // committed inside the import job
        }

        kit.Ai.Resume();
        var import = await kit.Jobs.WaitAsync(session.JobId!.Value, ct);

        Assert.True(import.State == JobState.Completed, import.Error);
        Assert.Equal(("AI item pass", ImportJob.ItemStage, 6), (import.Stage, import.StageIndex, import.StageCount));
        await using (var db = kit.Catalog.Db())
        {
            Assert.All(await db.Assets.ToListAsync(ct), a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
            var forest = await db.Sources.SingleAsync(s => s.DisplayName == "Forest Pack", ct);
            Assert.Equal(("A pack of test assets.", "CC0"), (forest.Description, forest.License.Kind)); // LICENSE.txt was still readable
            Assert.True(await db.StagedSkips.AnyAsync(ct)); // kept for the import results
        }
    }

    [Fact]
    public async Task No_ai_import_is_committed_with_results()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder], ["forest"], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var db = kit.Catalog.Db();
        Assert.Equal(["beep", "Coin", "Grass_Albedo"], (await db.Assets.Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        var session = await db.ImportSessions.SingleAsync(ct);
        Assert.Equal(ImportSessionState.Committed, session.State);
        var forest = await db.Sources.SingleAsync(s => s.DisplayName == "Forest Pack", ct);
        var sounds = await db.Sources.SingleAsync(s => s.DisplayName == "Sounds", ct);
        // Round-trips through the JSON column (a fresh context reads it).
        Assert.Equal([new ImportSourceResult(sounds.Id, "Sounds", 1), new ImportSourceResult(forest.Id, "Forest Pack", 2)],
            session.Results!.OrderBy(r => r.Name == "Forest Pack"));
    }

    [Fact]
    public async Task Skipped_items_listed_in_results_and_survive_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var sessionId = await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));

        var results = (await new ImportResultsService(kit.Store).LoadAsync(sessionId, ct))!; // a new service: as after a restart

        var forest = results.Sources.Single(s => s.Name == "Forest Pack");
        Assert.Equal(2, forest.Assets);
        Assert.Equal("script (noted)", forest.Skips.Single(s => s.Path.EndsWith("Player.cs")).Reason);
        Assert.StartsWith("cannot open archive", forest.Skips.Single(s => s.Path.EndsWith("broken.zip")).Reason);
        Assert.Equal(3, results.Assets);
        Assert.Equal([sessionId], (await kit.Imports.OpenSessionsAsync(ct)).Select(s => s.Id)); // shown again until dismissed
    }

    [Fact]
    public async Task Duplicates_are_skipped_and_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));
        kit.Catalog.Dir.Write("Copy/Coin.png", await File.ReadAllBytesAsync(Path.Combine(folder, "Sprites", "Coin.png"), ct));
        kit.Catalog.Dir.Write("Copy/Unique.png", ImportStagingTests.Png(ImageMagick.MagickColors.Red));

        var second = await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Copy")], Depth: AiDepth.None));

        await using (var db = kit.Catalog.Db())
            Assert.Equal(1, await db.Assets.CountAsync(a => a.Name == "Coin", ct)); // not imported twice
        var results = (await kit.Results.LoadAsync(second, ct))!;
        var copy = results.Sources.Single(s => s.Name == "Copy");
        Assert.Equal(1, copy.Assets);
        var skip = copy.Skips.Single();
        Assert.Equal(("Coin.png", SkipKind.Duplicate, ImportCommitter.AlreadyCataloged), (skip.Path, skip.Kind, skip.Reason));
    }

    [Fact]
    public async Task Duplicates_included_are_imported_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));

        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None, IncludeDuplicates: true));

        await using var db = kit.Catalog.Db();
        Assert.Equal(2, await db.Assets.CountAsync(a => a.Name == "Coin", ct));
        Assert.Equal(2, await db.Sources.CountAsync(s => s.DisplayName == "Forest Pack", ct));
    }

    [Fact]
    public async Task Commit_runs_once_and_again_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var sessionId = await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));

        var again = await kit.Committer.CommitAsync(sessionId, new NullProgress(), ImportJob.CommitStage, ImportJob.StageCount, ct);

        Assert.True(again.Committed);
        Assert.Equal(0, again.Assets);
        await using var db = kit.Catalog.Db();
        Assert.Equal(3, await db.Assets.CountAsync(ct));
        Assert.Equal(2, (await db.ImportSessions.SingleAsync(ct)).Results!.Count);
    }

    [Fact]
    public async Task Interrupted_after_previews_commits_on_resume()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], Depth: AiDepth.None), ct);
        await kit.Stager.ScanAndGroupAsync(session.Id, new NullProgress(), ImportJob.StageCount, ct);
        await kit.Stager.ProcessAsync(session.Id, new NullProgress(), ImportJob.StageCount, ct); // as if the app closed here
        await using (var db = kit.Catalog.Db())
            Assert.Equal((ImportSessionState.Scanning, 3, 0), await db.ImportSessions.Select(s => ValueTuple.Create(s.State, s.StagedThrough, db.Assets.Count())).SingleAsync(ct));

        var job = await kit.Jobs.WaitAsync(await kit.Imports.ResumeAsync(session.Id, ct), ct);

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var check = kit.Catalog.Db();
        Assert.Equal(3, await check.Assets.CountAsync(ct));
        Assert.Equal(ImportSessionState.Committed, (await check.ImportSessions.SingleAsync(ct)).State);
    }

    [Fact]
    public async Task Cancel_during_previews_adds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], ["forest"], Depth: AiDepth.None), ct);
        await kit.Stager.ScanAndGroupAsync(session.Id, new NullProgress(), ImportJob.StageCount, ct); // staged, previews not done

        Assert.True(await kit.Imports.CancelAsync(session.Id, ct));

        await using var db = kit.Catalog.Db();
        Assert.Equal(0, await db.AllAssets.CountAsync(ct));
        Assert.Equal(0, await db.AllSources.CountAsync(ct));
        Assert.Equal(0, await db.Tags.CountAsync(ct));
        Assert.Equal(ImportSessionState.Cancelled, (await db.ImportSessions.SingleAsync(ct)).State);
        // The commit loses the race: the session stays cancelled.
        Assert.False((await kit.Committer.CommitAsync(session.Id, new NullProgress(), ImportJob.CommitStage, ImportJob.StageCount, ct)).Committed);
    }

    [Fact]
    public async Task Cancel_during_ai_keeps_the_committed_assets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient());
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        kit.Ai.Pause();
        var session = await kit.Imports.StartAsync(new ImportRequest([folder]), ct);
        await kit.WaitCommittedAsync(session.Id);

        Assert.False(await kit.Imports.CancelAsync(session.Id, ct)); // only the AI stops

        var job = await kit.Jobs.WaitAsync(session.JobId!.Value, ct);
        Assert.Equal(JobState.Cancelled, job.State);
        await using var db = kit.Catalog.Db();
        Assert.Equal(3, await db.Assets.CountAsync(ct));
        Assert.Equal(ImportSessionState.Committed, (await db.ImportSessions.SingleAsync(ct)).State);
        Assert.DoesNotContain(await db.Assets.Select(a => a.AiStatus).ToListAsync(ct), s => s == AiStatus.Enriched);
    }

    [Fact]
    public async Task Dismiss_hides_results_and_a_new_import_clears_old_skips()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var first = await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));
        kit.Catalog.Dir.Write("Other/Unique.png", ImportStagingTests.Png(ImageMagick.MagickColors.Red));

        var second = await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Other")], Depth: AiDepth.None));

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.StagedSkips.CountAsync(s => s.SessionId == first, ct));
            Assert.True(await db.ImportSessions.Where(s => s.Id == first).Select(s => s.ResultsDismissed).SingleAsync(ct));
        }
        Assert.Equal([second], (await kit.Imports.OpenSessionsAsync(ct)).Select(s => s.Id));

        await kit.Imports.DismissAsync(second, ct);
        Assert.Empty(await kit.Imports.OpenSessionsAsync(ct));
    }

    [Fact]
    public async Task Sessions_waiting_for_confirmation_are_committed_at_startup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        // What an earlier version left: staged through previews, "ready for review", its job finished.
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], Depth: AiDepth.None), ct);
        await kit.Stager.ScanAndGroupAsync(session.Id, new NullProgress(), ImportJob.StageCount, ct);
        await kit.Stager.ProcessAsync(session.Id, new NullProgress(), ImportJob.StageCount, ct);
        await using (var db = kit.Catalog.Db())
            await db.ImportSessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.State, ImportSessionState.Ready).SetProperty(s => s.StagedThrough, 5), ct);

        Assert.Equal(1, await kit.Imports.CommitWaitingAsync(ct));

        long jobId;
        await using (var db = kit.Catalog.Db())
            jobId = (await db.ImportSessions.SingleAsync(ct)).JobId!.Value;
        var job = await kit.Jobs.WaitAsync(jobId, ct);
        Assert.True(job.State == JobState.Completed, job.Error);
        await using var check = kit.Catalog.Db();
        Assert.Equal(3, await check.Assets.CountAsync(ct));
        Assert.Equal(ImportSessionState.Committed, (await check.ImportSessions.SingleAsync(ct)).State);
        Assert.Equal(0, await kit.Imports.CommitWaitingAsync(ct));
    }
}

/// <summary>Progress nobody watches (stages run directly in a test).</summary>
internal sealed class NullProgress : IJobProgress
{
    public void SetStage(string name, int index, int count, long total = 0) { }
    public void SetTotal(long total) { }
    public void Advance(long count = 1, string? currentItem = null) { }
    public void SetCurrentItem(string? item) { }
    public void Warn(string itemRef, string? itemName, string reason) { }
    public void Fail(string itemRef, string? itemName, string reason) { }
    public void SetContainer(string? container) { }
    public void SetCount(string label, long value) { }
    public void SetSummary(object summary) { }
}
