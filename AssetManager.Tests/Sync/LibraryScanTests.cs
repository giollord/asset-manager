using System.Text.Json;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Sync;
using AssetManager.Shared.Components;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Sync;

/// <summary>Spec "Library folders" and "When scans run".</summary>
public class LibraryScanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Png(int i) => ImportStagingTests.Png(new MagickColor((byte)(20 + i * 30), 100, 160));

    private static async Task<(LibraryScanSummary Summary, JobSnapshot Job)> ScanAsync(ImportKit kit)
    {
        var job = await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(LibraryScanJob.JobType, "Scan", new LibraryScanPayload(), ct: Ct), Ct);
        Assert.True(job.State == JobState.Completed, job.Error);
        return (JsonSerializer.Deserialize<LibraryScanSummary>(job.SummaryJson!, JobJson.Options)!, job);
    }

    /// <summary>As Settings → Library adds a folder: volume-relative, with the volume remembered.</summary>
    private static async Task AddLibraryFolderAsync(ImportKit kit, string path)
    {
        var volumes = new VolumeRegistry(kit.Processing.Volumes, kit.Store);
        var location = volumes.FromPath(path);
        await volumes.RememberAsync([location.VolumeId], Ct);
        kit.SettingsStore.Update(s => s.LibraryFolders = [.. s.LibraryFolders, location.ToString()]);
    }

    [Fact]
    public async Task New_pack_downloaded()
    {
        await using var kit = await ImportKit.CreateAsync();
        var drive = Path.Combine(kit.Catalog.Dir.Path, "E");
        kit.Processing.Volumes.Mount("CAFE0002", "Assets drive", drive);
        kit.Catalog.Dir.Write("E/Assets/Town/house.png", Png(0));
        kit.Catalog.Dir.Write("E/Assets/Town/Props/well.png", Png(1));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(drive, "Assets", "Town")], Depth: AiDepth.None));
        await AddLibraryFolderAsync(kit, Path.Combine(drive, "Assets"));

        Assert.Empty((await ScanAsync(kit)).Summary.Entries); // the imported pack is not listed

        kit.Catalog.Dir.Write("E/Assets/ForestPack.zip", FixtureBuilder.Zip(new FixtureFile("Trees/oak.png", Png(2)), new FixtureFile("Trees/pine.png", Png(3))));
        kit.Catalog.Dir.Write("E/Assets/Town_v2/house.png", Png(4));        // a new version: same files as Town
        kit.Catalog.Dir.Write("E/Assets/Town_v2/Props/well.png", Png(5));
        kit.Catalog.Dir.Write("E/Assets/__MACOSX/ForestPack.zip", [1, 2]); // ignored folder
        kit.Catalog.Dir.Write("E/Assets/readme.txt", "downloads");          // not a pack

        // The drive comes back under another letter: library folders and sources are stored per volume.
        kit.Processing.Volumes.Detach("CAFE0002");
        var moved = Path.Combine(kit.Catalog.Dir.Path, "F");
        Directory.Move(drive, moved);
        kit.Processing.Volumes.Mount("CAFE0002", "Assets drive", moved);

        var (summary, job) = await ScanAsync(kit);

        Assert.Equal(["ForestPack.zip", "Town_v2"], summary.Entries.Select(e => e.Name));
        var forest = summary.Entries[0];
        Assert.Equal(SourceKind.Archive, forest.Kind);
        Assert.Equal(Path.Combine(moved, "Assets", "ForestPack.zip"), forest.Path);
        Assert.Null(forest.VersionOfSourceId);
        Assert.Equal("Town", summary.Entries[1].VersionOfName);
        Assert.Equal("2 new packs found in the library folders.", JobNotifications.SyncFindings(job));
        await using (var db = kit.Catalog.Db())
            Assert.Equal(1, await db.Sources.CountAsync(Ct)); // scanning imports nothing

        // Imported from the list, it is no longer listed.
        await kit.CatalogAsync(new ImportRequest([forest.Path], Depth: AiDepth.None));
        Assert.Equal(["Town_v2"], (await ScanAsync(kit)).Summary.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task Detached_library_folder_is_reported()
    {
        await using var kit = await ImportKit.CreateAsync();
        var drive = Path.Combine(kit.Catalog.Dir.Path, "E");
        kit.Processing.Volumes.Mount("CAFE0003", "Backup", drive);
        kit.Catalog.Dir.Write("E/Downloads/Pack.zip", FixtureBuilder.Zip(new FixtureFile("a.png", Png(0))));
        await AddLibraryFolderAsync(kit, Path.Combine(drive, "Downloads"));
        kit.Processing.Volumes.Detach("CAFE0003");

        var summary = (await ScanAsync(kit)).Summary;

        Assert.Empty(summary.Entries);
        Assert.Contains("Backup", Assert.Single(summary.Unavailable));
    }

    [Theory]
    [InlineData("Forest Pack v1.2.zip", "ForestPack_2.0")]
    [InlineData("Kit (1.0).unitypackage", "kit")]
    [InlineData("SciFi-Props-3", "SciFi Props")]
    public void Version_numbers_are_ignored_in_names(string a, string b) =>
        Assert.Equal(LibraryScanService.NameKey(a), LibraryScanService.NameKey(b));

    [Fact]
    public void Names_that_differ_beyond_versions_do_not_match() =>
        Assert.NotEqual(LibraryScanService.NameKey("2D Platformer"), LibraryScanService.NameKey("Platformer"));

    /// <summary>Spec "Startup check": two new packs give one notification, and nothing is imported without confirmation.</summary>
    [Fact]
    public async Task Startup_check()
    {
        await using var kit = await ImportKit.CreateAsync();
        var library = Path.Combine(kit.Catalog.Dir.Path, "Library");
        kit.Catalog.Dir.Write("Library/Dungeon.zip", FixtureBuilder.Zip(new FixtureFile("wall.png", Png(0))));
        kit.Catalog.Dir.Write("Library/Space/ship.png", Png(1));
        await AddLibraryFolderAsync(kit, library);

        // Off by default: no scan is queued.
        var off = new SyncStartupTask(kit.SettingsStore, kit.Jobs) { Delay = TimeSpan.Zero };
        await off.RunAsync(Ct);
        await off.Pending;
        await using (var db = kit.Catalog.Db())
            Assert.False(await db.Jobs.AnyAsync(j => j.Type == LibraryScanJob.JobType || j.Type == RescanJob.JobType, Ct));

        kit.SettingsStore.Update(s => s.CheckForChangesAtStartup = true);
        var startup = new SyncStartupTask(kit.SettingsStore, kit.Jobs) { Delay = TimeSpan.Zero };
        await startup.RunAsync(Ct);
        await startup.Pending;

        List<long> ids;
        await using (var db = kit.Catalog.Db())
            ids = await db.Jobs.Where(j => j.Type == LibraryScanJob.JobType || j.Type == RescanJob.JobType).Select(j => j.Id).ToListAsync(Ct);
        Assert.Equal(2, ids.Count);
        var finished = new List<JobSnapshot>();
        foreach (var id in ids) finished.Add(await kit.Jobs.WaitAsync(id, Ct));
        Assert.All(finished, j => Assert.True(j.State == JobState.Completed, j.Error));

        var scan = Assert.Single(finished, j => j.Type == LibraryScanJob.JobType);
        Assert.StartsWith(SyncStartupTask.TitlePrefix, scan.Title);
        Assert.Equal("Startup check: 2 new packs found in the library folders.", JobNotifications.SyncFindings(scan));
        Assert.Null(JobNotifications.SyncFindings(Assert.Single(finished, j => j.Type == RescanJob.JobType))); // no sources, nothing changed
        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(0, await db.AllSources.CountAsync(Ct));
            Assert.Equal(0, await db.ImportSessions.CountAsync(Ct));
        }
    }
}
