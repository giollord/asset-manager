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
        Assert.Equal((ImportSessionState.Committed, 5), (session.State, session.StagedThrough));

        var staged = await db.AllAssets.Include(a => a.Source).Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Files)
            .Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken);
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

    /// <summary>
    /// Records, during the scan stage, each current item with the container and done count in effect, every advance and
    /// total, and the last value of each count.
    /// </summary>
    private sealed class RecordingProgress : IJobProgress
    {
        public string? Container;
        public List<(string? Item, string? Container, long Done)> Items { get; } = [];
        public List<string?> Containers { get; } = [];
        public Dictionary<string, long> Counts { get; } = [];
        public List<string> CountOrder { get; } = [];
        public string? Stage;
        public long Total, Done;

        /// <summary>The scan's advances and totals in order: ("total", n) or ("advance", n).</summary>
        public List<(string Kind, long Value)> ScanEvents { get; } = [];

        private bool Scanning => Stage == "Scanning";

        public void SetStage(string name, int index, int count, long total = 0)
        {
            Stage = name;
            if (Scanning) ScanEvents.Add(("total", Total = total));
        }

        public void SetTotal(long total) { if (Scanning) ScanEvents.Add(("total", Total = total)); }

        public void Advance(long count = 1, string? currentItem = null)
        {
            if (!Scanning) return;
            Done += count;
            ScanEvents.Add(("advance", count));
            if (currentItem is not null) Items.Add((currentItem, Container, Done));
        }

        public void SetCurrentItem(string? item) { if (Scanning) Items.Add((item, Container, Done)); }
        public void Warn(string itemRef, string? itemName, string reason) { }
        public void Fail(string itemRef, string? itemName, string reason) { }
        public void SetSummary(object summary) { }
        public void SetContainer(string? container)
        {
            Container = container;
            Containers.Add(container);
            if (Scanning && container is not null) Opened.Add((container, Done));
        }

        /// <summary>Each container named during the scan with the done count at that moment.</summary>
        public List<(string Container, long Done)> Opened { get; } = [];
        public void SetCount(string label, long value) { if (Counts.TryAdd(label, value)) CountOrder.Add(label); else Counts[label] = value; }
    }

    [Fact]
    public async Task Scan_reports_the_archive_and_what_it_found()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], Depth: AiDepth.None), ct);

        var progress = new RecordingProgress();
        await kit.Stager.ScanAndGroupAsync(session.Id, progress, 6, ct);

        // Spec "Current archive shown while working inside it": the zip's file carries the zip, plain files none.
        Assert.Equal("Sounds.zip", progress.Items.Single(a => a.Item == "Sfx/beep.wav").Container);
        Assert.All(progress.Items.Where(a => a.Item is not null && a.Item != "Sfx/beep.wav"), a => Assert.Null(a.Container));
        // Opening an archive clears the last file, which is not inside it.
        Assert.Contains((null, "Sounds.zip", 5L), progress.Items);
        Assert.Contains("broken.zip", progress.Containers); // named while it was being opened
        Assert.Null(progress.Container);                   // cleared once the scan ended

        // Spec "Import shows what it found so far": 7 files (the unreadable zip is not a file), 3 of a supported type,
        // grouped into 3 assets.
        Assert.Equal([ImportStager.FilesFoundCount, ImportStager.ImportableCount, ImportStager.AssetsCount], progress.CountOrder);
        Assert.Equal((7L, 3L, 3L), (progress.Counts["files found"], progress.Counts["importable"], progress.Counts["assets"]));
        await using (var db = kit.Catalog.Db())
            Assert.Equal(3, await db.AllAssets.CountAsync(a => a.ImportSessionId == session.Id, ct));

        // Resumed past grouping: the asset count comes back from the staged assets.
        var resumed = new RecordingProgress();
        await kit.Stager.ScanAndGroupAsync(session.Id, resumed, 6, ct);
        Assert.Equal(3L, resumed.Counts["assets"]);
        Assert.False(resumed.Counts.ContainsKey("files found"));
    }

    /// <summary>Runs the scan and grouping of a new session and returns what it reported.</summary>
    private static async Task<RecordingProgress> ScanAsync(ImportKit kit, ImportRequest request)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await kit.Stager.CreateSessionAsync(request, ct);
        var progress = new RecordingProgress();
        await kit.Stager.ScanAndGroupAsync(session.Id, progress, 6, ct);
        return progress;
    }

    /// <summary>Spec "Import scan counts files processed out of total": the total is set before the scan advances.</summary>
    private static void AssertTotalFirst(RecordingProgress progress, long total)
    {
        var firstAdvance = progress.ScanEvents.FindIndex(e => e.Kind == "advance");
        Assert.True(firstAdvance > 0, string.Join(" ", progress.ScanEvents));
        Assert.Equal(("total", total), progress.ScanEvents[firstAdvance - 1]);
        Assert.DoesNotContain(progress.ScanEvents.Skip(firstAdvance), e => e.Kind == "total");
        Assert.All(progress.ScanEvents.Where(e => e.Kind == "advance"), e => Assert.True(e.Value > 0));
        Assert.Equal(total, progress.Done);
    }

    [Fact]
    public async Task Scan_counts_top_level_files_out_of_the_total()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        kit.Catalog.Dir.Write("Forest Pack/Old/stone.png", Png(MagickColors.Gray));

        var progress = await ScanAsync(kit, new ImportRequest([folder], Depth: AiDepth.None, IgnoredFolders: ["Old"]));

        // 8 top-level items: 6 plain files and 2 zips (the unreadable one too); the ignored folder is left out.
        AssertTotalFirst(progress, 8);
        Assert.Equal("Listing files...", progress.Items[0].Item);
        // Sorted, Sounds.zip is the 6th item: 5 done while its file is scanned, and the zip counts only once the walk
        // has moved on to the next file.
        Assert.Equal(5, progress.Items.Single(i => i.Item == "Sfx/beep.wav").Done);
        // The file before an archive is done as soon as the archive starts to open, not after its extraction.
        Assert.Equal([("broken.zip", 0L), ("Sounds.zip", 5L)], progress.Opened.Distinct());
        Assert.Equal(6, progress.Items.Single(i => i.Item == @"Sprites\Coin.png").Done);
        Assert.Equal(7L, progress.Counts[ImportStager.FilesFoundCount]); // the zip's file is still found
    }

    [Fact]
    public async Task Rescan_total_covers_only_the_added_files()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);

        var progress = await ScanAsync(kit, new ImportRequest([folder], Depth: AiDepth.None, OnlyPaths: ["Sounds.zip", "Sprites/Coin.png"]));

        AssertTotalFirst(progress, 2); // the added archive is one file
        Assert.Equal(2L, progress.Counts[ImportStager.ImportableCount]);
    }

    [Fact]
    public async Task Several_inputs_share_one_total_from_the_start()
    {
        await using var kit = await ImportKit.CreateAsync();
        var dir = kit.Catalog.Dir;
        dir.Write("A/one.png", Png(MagickColors.Red));
        dir.Write("A/two.png", Png(MagickColors.Blue));
        dir.Write("B/three.png", Png(MagickColors.Green));
        dir.Write("B/sub/four.png", Png(MagickColors.Yellow));
        dir.Write("B/five.txt", "notes");

        var progress = await ScanAsync(kit, new ImportRequest([Path.Combine(dir.Path, "A"), Path.Combine(dir.Path, "B")], Depth: AiDepth.None));

        AssertTotalFirst(progress, 5);
    }

    [Fact]
    public async Task Single_archive_input_is_one_file()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);

        var progress = await ScanAsync(kit, new ImportRequest([Path.Combine(folder, "Sounds.zip")], Depth: AiDepth.None));

        AssertTotalFirst(progress, 1);
        Assert.Equal(0, progress.Items.Single(i => i.Item == "Sfx/beep.wav").Done);
    }

    [Fact]
    public void Container_label_uses_file_names_outermost_first()
    {
        Assert.Equal("Packs.zip → Forest.unitypackage",
            ImportStager.ContainerLabel(new Location("1A2B", @"Downloads\Packs.zip", ["Unity/Forest.unitypackage"])));
        Assert.Equal("Forest.zip", ImportStager.ContainerLabel(new Location(null, @"E:\Packs\Forest.zip")));
    }

    [Fact]
    public async Task Nothing_in_the_catalog_before_the_commit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        var session = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], ["humble-lowpoly"], Depth: AiDepth.None), ct);
        await kit.Stager.ScanAndGroupAsync(session.Id, new NullProgress(), 6, ct);
        await kit.Stager.ProcessAsync(session.Id, new NullProgress(), 6, ct); // previews done, the commit not yet

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.Assets.CountAsync(ct));   // the catalog view
            Assert.Equal(0, await db.Sources.CountAsync(ct));
            Assert.Equal(3, await db.AllAssets.CountAsync(a => a.ImportSessionId == session.Id, ct));
        }

        await kit.Imports.CancelAsync(session.Id, ct);

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.AllAssets.CountAsync(ct));
            Assert.Equal(0, await db.AllSources.CountAsync(ct));
            Assert.Equal(0, await db.Tags.CountAsync(ct));
            Assert.Equal(0, await db.Previews.CountAsync(ct));
            Assert.Equal(0, await db.StagedSkips.CountAsync(ct));
            Assert.Equal(ImportSessionState.Cancelled, (await db.ImportSessions.SingleAsync(ct)).State);
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
        var sources = await db.AllSources.Where(s => s.CommittedFromSessionId == sessionId).OrderBy(s => s.DisplayName).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([(SourceKind.Archive, "Icons"), (SourceKind.File, "Rock.png")], sources.Select(s => (s.Kind, s.DisplayName)));
        Assert.All(sources, s => Assert.Null(s.ParentSourceId));
        Assert.Equal(2, await db.AllAssets.CountAsync(a => a.Source.CommittedFromSessionId == sessionId, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Re_adding_same_folder_marks_duplicates_excluded()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        var ct = TestContext.Current.CancellationToken;
        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));
        var second = await kit.Stager.CreateSessionAsync(new ImportRequest([folder], Depth: AiDepth.None), ct);

        await kit.Stager.ScanAndGroupAsync(second.Id, new NullProgress(), 6, ct);
        await kit.Stager.ProcessAsync(second.Id, new NullProgress(), 6, ct);

        await using var check = kit.Catalog.Db();
        var staged = await check.AllAssets.Where(a => a.ImportSessionId == second.Id).ToListAsync(ct);
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
    }

    /// <summary>Spec scenario "Re-adding same folder": nothing is added, and the results list every item as cataloged.</summary>
    [Fact]
    public async Task Re_adding_same_folder_adds_nothing()
    {
        await using var kit = await ImportKit.CreateAsync();
        var folder = await PackFolderAsync(kit.Catalog.Dir);
        var ct = TestContext.Current.CancellationToken;
        await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));

        var second = await kit.CatalogAsync(new ImportRequest([folder], Depth: AiDepth.None));

        await using var db = kit.Catalog.Db();
        Assert.Equal(3, await db.Assets.CountAsync(ct));
        Assert.Equal(["Forest Pack"], await db.Sources.Where(s => s.ParentSourceId == null).Select(s => s.DisplayName).ToListAsync(ct));
        var duplicates = (await kit.Results.LoadAsync(second, ct))!.Sources.SelectMany(s => s.Skips).Where(s => s.Kind == SkipKind.Duplicate).ToList();
        Assert.Equal(3, duplicates.Count);
        Assert.Equal(0, (await kit.Results.LoadAsync(second, ct))!.Assets);
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
        var forest = await db.AllSources.SingleAsync(s => s.CommittedFromSessionId == sessionId && s.DisplayName == "Forest Pack", TestContext.Current.CancellationToken);
        Assert.Equal("A pack of test assets.", forest.Description);
        Assert.Equal(("CC0", "LICENSE.txt"), (forest.License.Kind, forest.License.Evidence));
        Assert.StartsWith("Includes 2 code/project files", forest.Notes);

        var assets = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).Where(a => a.Source.CommittedFromSessionId == sessionId)
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

    /// <summary>
    /// Regression: the pack pass loaded assets without their tags, so a pack tag the asset already had (a shared tag
    /// of the import, or an AI tag copied from an identical cataloged asset) was added again and the save failed
    /// with "UNIQUE constraint failed: AssetTags.AssetId, AssetTags.TagId".
    /// </summary>
    [Fact]
    public async Task Pack_tag_the_assets_already_have_is_not_added_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync(new SchemaAwareChatClient());
        var folder = await ImportStagingTests.PackFolderAsync(kit.Catalog.Dir);

        var (sessionId, job) = await kit.ImportAsync(new ImportRequest([folder], SharedTags: ["pack-tag"], Depth: AiDepth.PackOnly));

        Assert.True(job.State == JobState.Completed, job.Error);
        await using var db = kit.Catalog.Db();
        var assets = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(ct);
        Assert.NotEmpty(assets);
        Assert.All(assets, a => Assert.Single(a.Tags, t => t.Tag.Name == "pack-tag"));
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
        var assets = await db.AllAssets.Include(a => a.Tags).ThenInclude(t => t.Tag).Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(ct);
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
            var ids = await db.AllAssets.Where(a => a.Source.CommittedFromSessionId == sessionId).OrderBy(a => a.Id).Select(a => a.Id).ToListAsync(ct);
            await db.AllAssets.Where(a => a.Id == ids[0]).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Failed), ct);
            await db.AllAssets.Where(a => ids.Skip(1).Contains(a.Id)).ExecuteUpdateAsync(u => u.SetProperty(a => a.AiStatus, AiStatus.Pending), ct);
        }
        Volatile.Write(ref available, false); // the app just restarted: the model is still loading
        _ = Task.Delay(300, ct).ContinueWith(_ => Volatile.Write(ref available, true), ct);

        var resumed = await kit.Jobs.WaitAsync(await kit.Imports.ResumeAsync(sessionId, ct), ct);

        Assert.True(resumed.State == JobState.Completed && resumed.Warnings == 0, resumed.Error);
        await using var check = kit.Catalog.Db();
        Assert.All(await check.AllAssets.Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(ct), a => Assert.Equal(AiStatus.Enriched, a.AiStatus));
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
        Assert.All(await db.AllAssets.Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken),
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
        Assert.All(await db.AllAssets.Where(a => a.Source.CommittedFromSessionId == sessionId).ToListAsync(TestContext.Current.CancellationToken),
            a => Assert.Equal(AiStatus.NotEnriched, a.AiStatus));
        Assert.Contains("not enriched", (await kit.Jobs.ItemErrorsAsync(job.Id, TestContext.Current.CancellationToken)).Single().Reason);
    }

    [Fact]
    public async Task Detected_pixel_art_gets_the_style_and_an_ai_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await ImportKit.CreateAsync();
        using var art = AssetManager.Tests.Processing.ImageHandlerTests.PixelArt(16, 4, 10);
        var file = kit.Catalog.Dir.Write("Sprites/hero.png", art.ToByteArray(MagickFormat.Png));
        kit.Catalog.Dir.Write("Sprites/sky.png", ImportStagingTests.Png(MagickColors.SkyBlue, 300)); // flat colour: no pixel grid to measure

        await kit.CatalogAsync(new ImportRequest([Path.GetDirectoryName(file)!], Depth: AiDepth.None));

        await using var db = kit.Catalog.Db();
        var hero = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).SingleAsync(a => a.Name == "hero", ct);
        Assert.Equal((true, 4), (hero.Image!.IsPixelArt, hero.Image.PixelScale));
        Assert.Contains("pixel art", hero.Style);
        Assert.Contains(hero.Tags, t => t.Tag.Name == "pixel art" && t.Origin == TagOrigin.AI);
    }
}
