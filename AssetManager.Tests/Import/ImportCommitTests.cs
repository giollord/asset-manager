using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

public class ImportCommitTests
{
    [Fact]
    public async Task Confirm_before_ai_finishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        kit.Ai.Pause(); // the AI stages wait at the gate, as if the model were still busy

        var session = await kit.Imports.StartAsync(new ImportRequest([folder]), ct);
        await kit.WaitReadyAsync(session.Id);
        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(session.Id, ct), ct);

        Assert.True(commit.State == JobState.Completed, commit.Error);
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
            Assert.True(await db.StagedSkips.AnyAsync(ct)); // kept until the pack pass has read the documents
            Assert.Equal(ImportSessionState.Committed, (await db.ImportSessions.SingleAsync(ct)).State);
        }

        kit.Ai.Resume();
        var import = await kit.Jobs.WaitAsync(session.JobId!.Value, ct);

        Assert.True(import.State == JobState.Completed, import.Error);
        await using (var db = kit.Catalog.Db())
        {
            Assert.All(await db.Assets.ToListAsync(ct), a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
            var forest = await db.Sources.SingleAsync(s => s.DisplayName == "Forest Pack", ct);
            Assert.Equal(("A pack of test assets.", "CC0"), (forest.Description, forest.License.Kind)); // LICENSE.txt was still readable
            Assert.Equal(0, await db.StagedSkips.CountAsync(ct));
        }
    }

    [Fact]
    public async Task Excluded_items_are_not_committed_and_empty_sources_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);
        var (sessionId, _) = await kit.ImportAsync(new ImportRequest([folder], ["forest"], Depth: AiDepth.None));
        await using (var db = kit.Catalog.Db())
            await db.AllAssets.Where(a => a.Name == "beep").ExecuteUpdateAsync(u => u.SetProperty(a => a.Included, false), ct);

        var commit = await kit.Jobs.WaitAsync(await kit.Imports.ConfirmAsync(sessionId, ct), ct);

        Assert.True(commit.State == JobState.Completed, commit.Error);
        await using var check = kit.Catalog.Db();
        Assert.Equal(["Coin", "Grass_Albedo"], (await check.Assets.Select(a => a.Name).ToListAsync(ct)).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["Forest Pack"], await check.Sources.Select(s => s.DisplayName).ToListAsync(ct)); // "Sounds" held only the excluded item
        Assert.Equal(2, await check.Assets.CountAsync(a => a.Tags.Any(t => t.Tag.Name == "forest"), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Imports.ConfirmAsync(sessionId, ct));
    }
}
