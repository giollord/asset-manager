using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Processing;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Import;

public class ImportStagingTests
{
    internal static byte[] Png(MagickColor color, int size = 32)
    {
        using var img = new MagickImage(color, (uint)size, (uint)size);
        return img.ToByteArray(MagickFormat.Png);
    }

    /// <summary>A pack folder: images, scripts, a license, a corrupt zip, an unknown file and a zip with a sound.</summary>
    internal static async Task<string> PackFolderAsync(TempDir dir)
    {
        var root = Path.Combine(dir.Path, "Forest Pack");
        dir.Write("Forest Pack/Textures/Grass_Albedo.png", Png(MagickColors.Green, 64));
        dir.Write("Forest Pack/Sprites/Coin.png", Png(MagickColors.Gold));
        dir.Write("Forest Pack/Scripts/Player.cs", "class Player {}");
        dir.Write("Forest Pack/Scripts/Enemy.cs", "class Enemy {}");
        dir.Write("Forest Pack/LICENSE.txt", "CC0 - free for commercial use.");
        dir.Write("Forest Pack/broken.zip", "this is not a zip file at all"u8.ToArray());
        dir.Write("Forest Pack/notes.xyz", [1, 2, 3]);
        var wav = await AudioHandlerTests.ToneAsync(dir, "beep.wav", 1);
        dir.Write("Forest Pack/Sounds.zip", FixtureBuilder.Zip(new FixtureFile("Sfx/beep.wav", await File.ReadAllBytesAsync(wav))));
        File.Delete(wav);
        return root;
    }

    [Fact]
    public async Task Skipped_items_listed()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder], ["forest"], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var db = kit.Catalog.Db();
        var skips = await db.StagedSkips.Where(s => s.SessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("script (noted)", skips.Single(s => s.Path.EndsWith("Player.cs")).Reason);
        Assert.Equal(SkipKind.Noted, skips.Single(s => s.Path.EndsWith("Enemy.cs")).Kind);
        Assert.StartsWith("cannot open archive", skips.Single(s => s.Path.EndsWith("broken.zip")).Reason);
        Assert.Equal(SkipKind.Document, skips.Single(s => s.Path.EndsWith("LICENSE.txt")).Kind);
        Assert.StartsWith("unsupported file type", skips.Single(s => s.Path.EndsWith("notes.xyz")).Reason);
        Assert.NotNull(skips.Single(s => s.Path.EndsWith("Player.cs")).Location);

        var session = await db.ImportSessions.SingleAsync(s => s.Id == sessionId, TestContext.Current.CancellationToken);
        Assert.Equal((ImportSessionState.Ready, 5), (session.State, session.StagedThrough));

        var staged = await db.AllAssets.Include(a => a.Source).Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Files)
            .Where(a => a.ImportSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken);
        staged = staged.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(["beep", "Coin", "Grass_Albedo"], staged.Select(a => a.Name));
        Assert.Equal([AssetType.Audio, AssetType.Image, AssetType.Texture], staged.Select(a => a.Type));
        Assert.All(staged, a => Assert.Contains(a.Tags, t => t.Tag.Name == "forest" && t.Origin == TagOrigin.Import));
        Assert.All(staged, a => Assert.Equal(AiStatus.NotEnriched, a.AiStatus));
        Assert.All(staged, a => Assert.True(a.PreviewOptionsVersion != 0 && a.PreviewError is null, a.PreviewError));
        Assert.All(staged, a => Assert.NotNull(a.PrimaryFile!.FastHash));

        // The zip became a nested source of the folder; its sound belongs to it.
        var beep = staged.Single(a => a.Name == "beep");
        Assert.Equal((SourceKind.Archive, "Sounds"), (beep.Source.Kind, beep.Source.DisplayName));
        Assert.Equal("Forest Pack", (await db.AllSources.SingleAsync(s => s.Id == beep.Source.ParentSourceId, TestContext.Current.CancellationToken)).DisplayName);
        Assert.EndsWith("Sounds.zip", beep.PrimaryFile!.Location.Path);
        Assert.Equal(["Sfx/beep.wav"], beep.PrimaryFile.Location.Inner);
        Assert.True(await db.Previews.AnyAsync(p => p.AssetId == beep.Id && p.Kind == PreviewKind.Waveform, TestContext.Current.CancellationToken));

        Assert.Equal(6, job.StageCount);
        Assert.Contains("\"assets\":3", job.SummaryJson);
    }

    [Fact]
    public async Task Nothing_committed_before_confirm()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, _) = await kit.ImportAsync(new ImportRequest([folder], ["humble-lowpoly"], Depth: AiDepth.None));

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.Assets.CountAsync(TestContext.Current.CancellationToken));   // the catalog view
            Assert.Equal(0, await db.Sources.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(3, await db.AllAssets.CountAsync(a => a.ImportSessionId == sessionId, TestContext.Current.CancellationToken));
        }

        await kit.Imports.CancelAsync(sessionId, TestContext.Current.CancellationToken);

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.AllAssets.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.AllSources.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.Tags.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.Previews.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await db.StagedSkips.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(ImportSessionState.Cancelled, (await db.ImportSessions.SingleAsync(TestContext.Current.CancellationToken)).State);
        }
    }

    [Fact]
    public async Task Single_files_and_archives_become_their_own_sources()
    {
        await using var kit = await ImportKit.CreateAsync();
        var image = kit.Catalog.Dir.Write("loose/Rock.png", Png(MagickColors.Gray));
        var zip = kit.Catalog.Dir.Write("loose/Icons.zip", FixtureBuilder.Zip(new FixtureFile("ui/heart.png", Png(MagickColors.Red))));

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([image, zip], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var db = kit.Catalog.Db();
        var sources = await db.AllSources.Where(s => s.ImportSessionId == sessionId).OrderBy(s => s.DisplayName).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([(SourceKind.Archive, "Icons"), (SourceKind.File, "Rock.png")], sources.Select(s => (s.Kind, s.DisplayName)));
        Assert.All(sources, s => Assert.Null(s.ParentSourceId));
        Assert.Equal(2, await db.AllAssets.CountAsync(a => a.ImportSessionId == sessionId, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Re_adding_same_folder_marks_duplicates_excluded()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        var ct = TestContext.Current.CancellationToken;

        var (first, _) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None));
        await using (var db = kit.Catalog.Db())
        {
            // Simulate the commit of the first session.
            await db.AllAssets.Where(a => a.ImportSessionId == first).ExecuteUpdateAsync(u => u.SetProperty(a => a.ImportSessionId, (long?)null), ct);
            await db.AllSources.Where(s => s.ImportSessionId == first).ExecuteUpdateAsync(u => u.SetProperty(s => s.ImportSessionId, (long?)null), ct);
        }

        var (second, job) = await kit.ImportAsync(new ImportRequest([folder], Depth: AiDepth.None));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var check = kit.Catalog.Db();
        var staged = await check.AllAssets.Where(a => a.ImportSessionId == second).ToListAsync(ct);
        Assert.Equal(3, staged.Count);
        Assert.All(staged, a => Assert.NotNull(a.DuplicateOfAssetId));
        Assert.All(staged, a => Assert.False(a.Included));
        // Same content: metadata and previews are copied from the original rather than rendered again.
        foreach (var dup in staged)
        {
            var original = await check.Assets.SingleAsync(a => a.Id == dup.DuplicateOfAssetId, ct);
            Assert.Equal((original.Type, original.PreviewOptionsVersion), (dup.Type, dup.PreviewOptionsVersion));
            Assert.Equal(await check.Previews.CountAsync(p => p.AssetId == original.Id, ct), await check.Previews.CountAsync(p => p.AssetId == dup.Id, ct));
        }
        Assert.Contains("\"assets\":3", job.SummaryJson);
    }
}

public class ImportEnrichmentTests
{
    [Fact]
    public async Task Ai_stages_enrich_staged_assets_and_the_source()
    {
        var chat = new SchemaAwareChatClient();
        await using var kit = await ImportKit.CreateAsync(chat);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder], Instruction: "These are forest props."));

        Assert.True(job.State == JobState.Completed, job.Error);
        Assert.True(job.Errors == 0, string.Join("; ", (await kit.Jobs.ItemErrorsAsync(job.Id, TestContext.Current.CancellationToken)).Select(e => $"{e.ItemName}: {e.Reason}")));
        await using var db = kit.Catalog.Db();
        var forest = await db.AllSources.SingleAsync(s => s.ImportSessionId == sessionId && s.DisplayName == "Forest Pack", TestContext.Current.CancellationToken);
        Assert.Equal("A pack of test assets.", forest.Description);
        Assert.Equal(("CC0", "LICENSE.txt"), (forest.License.Kind, forest.License.Evidence));
        Assert.StartsWith("Includes 2 code/project files", forest.Notes);

        var assets = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).Where(a => a.ImportSessionId == sessionId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.All(assets, a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
        var coin = assets.Single(a => a.Name == "Coin");
        Assert.Equal(("Described Coin.", AiInputMode.Visual), (coin.Description, coin.AiInputMode));
        Assert.Contains(coin.Tags, t => t.Tag.Name == "pack-tag" && t.Origin == TagOrigin.Import); // pack default
        Assert.Contains(coin.Tags, t => t.Tag.Name == "ai-tag" && t.Origin == TagOrigin.AI);       // item tag
        Assert.Equal(AiInputMode.Spectrogram, assets.Single(a => a.Name == "beep").AiInputMode);
        Assert.Contains(chat.Prompts, p => p.Contains("Instruction from the user (follow it): These are forest props."));
        Assert.Contains(chat.Prompts, p => p.Contains("=== LICENSE.txt ==="));
        Assert.Equal(5, (await db.ImportSessions.SingleAsync(TestContext.Current.CancellationToken)).StagedThrough);
    }

    [Fact]
    public async Task Item_pass_runs_requests_in_parallel()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new SchemaAwareChatClient { Delay = TimeSpan.FromMilliseconds(150) };
        await using var kit = await ImportKit.CreateAsync(chat, parallelism: 2);
        for (var i = 0; i < 20; i++) kit.Catalog.Dir.Write($"Icons/icon{i:00}.png", ImportStagingTests.Png(new MagickColor((byte)(i * 12), 80, 160)));

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Icons")]));

        Assert.True(job.State == JobState.Completed && job.Warnings == 0, job.Error);
        Assert.Equal(2, chat.MaxConcurrent); // 3 sheets of up to 8 items, two at a time
        await using var db = kit.Catalog.Db();
        var assets = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).Where(a => a.ImportSessionId == sessionId).ToListAsync(ct);
        Assert.Equal(20, assets.Count);
        Assert.All(assets, a => Assert.Equal((AiStatus.Enriched, $"Described {a.Name}."), (a.AiStatus, a.Description)));
        Assert.All(assets, a => Assert.Contains(a.Tags, t => t.Tag.Name == "ai-tag" && t.Origin == TagOrigin.AI));
        Assert.Equal(1, await db.Tags.CountAsync(t => t.Name == "ai-tag", ct)); // shared new tag created once
    }

    [Fact]
    public async Task Resume_after_the_pack_pass_waits_for_the_model_and_retries_items()
    {
        // Regression: a resumed job skipped the finished pack pass without waiting for the model, saw none yet, and
        // marked every pending asset "not enriched", so the item pass had nothing left to do.
        var ct = TestContext.Current.CancellationToken;
        var available = true;
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient(), chatAvailable: () => Volatile.Read(ref available));
        var (sessionId, first) = await kit.ImportAsync(new ImportRequest([await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir)]));
        Assert.True(first.State == JobState.Completed, first.Error);
        await using (var db = kit.Catalog.Db())
        {
            // As after a failure in the item pass: the pack pass is done, items are pending or failed.
            await db.ImportSessions.ExecuteUpdateAsync(u => u.SetProperty(s => s.StagedThrough, 4), ct);
            var ids = await db.AllAssets.Where(a => a.ImportSessionId == sessionId).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
            await db.AllAssets.Where(a => a.Id == ids[0]).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Failed), ct);
            await db.AllAssets.Where(a => ids.Skip(1).Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Pending), ct);
        }
        Volatile.Write(ref available, false); // the app just restarted: the model is still loading
        _ = Task.Delay(300, ct).ContinueWith(_ => Volatile.Write(ref available, true), ct);

        var resumed = await kit.Jobs.WaitAsync(await kit.Imports.ResumeAsync(sessionId, ct), ct);

        Assert.True(resumed.State == JobState.Completed && resumed.Warnings == 0, resumed.Error);
        await using var check = kit.Catalog.Db();
        Assert.All(await check.AllAssets.Where(a => a.ImportSessionId == sessionId).ToListAsync(ct), a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
    }

    [Fact]
    public async Task Ai_stages_wait_for_a_configured_model_that_is_still_starting()
    {
        // A job resumed at app start runs before the AI runtime: a configured model must not count as "not installed".
        var checks = 0; // the model comes up on the third look; without waiting the first look finds none
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient(), chatAvailable: () => Interlocked.Increment(ref checks) >= 3);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder]));

        Assert.True(job.State == JobState.Completed && job.Warnings == 0, job.Error);
        await using var db = kit.Catalog.Db();
        Assert.All(await db.AllAssets.Where(a => a.ImportSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken),
            a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
    }

    [Fact]
    public async Task No_model_installed_marks_assets_not_enriched()
    {
        await using var kit = await ImportKit.CreateAsync(chat: null);
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder]));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal(1, job.Warnings);
        await using var db = kit.Catalog.Db();
        Assert.All(await db.AllAssets.Where(a => a.ImportSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken),
            a => Assert.Equal(AiStatus.NotEnriched, a.AiStatus));
        Assert.Contains("not enriched", (await kit.Jobs.ItemErrorsAsync(job.Id, TestContext.Current.CancellationToken)).Single().Reason);
    }
}
