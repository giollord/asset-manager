using System.Text.Json;
using AssetManager.Core.Domain;
using AssetManager.Core.Jobs;
using AssetManager.Processing.Import;
using AssetManager.Processing.Sync;
using AssetManager.Tests.Fixtures;
using AssetManager.Tests.Import;
using ImageMagick;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Sync;

/// <summary>Spec "Rescan imported sources", "Rescan follows import rules", "Detached sources skipped".</summary>
public class RescanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Png(int i) => ImportStagingTests.Png(new MagickColor((byte)(20 + i * 30), 100, 160));

    private static async Task<RescanSummary> RescanAsync(ImportKit kit, long[]? ids = null)
    {
        var job = await kit.Jobs.WaitAsync(await kit.Jobs.EnqueueAsync(RescanJob.JobType, "Rescan", new RescanPayload(ids), ct: Ct), Ct);
        Assert.True(job.State == JobState.Completed, job.Error);
        return JsonSerializer.Deserialize<RescanSummary>(job.SummaryJson!, JobJson.Options)!;
    }

    private static async Task<(int Assets, string Snapshot)> CatalogStateAsync(ImportKit kit)
    {
        await using var db = kit.Catalog.Db();
        var files = await db.AssetFiles.OrderBy(f => f.Id).Select(f => f.RelativePath + ":" + f.Size).ToListAsync(Ct);
        return (await db.Assets.CountAsync(Ct), string.Join("|", files));
    }

    [Fact]
    public async Task New_files_in_a_folder_pack()
    {
        await using var kit = await ImportKit.CreateAsync();
        var root = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        for (var i = 0; i < 4; i++) kit.Catalog.Dir.Write($"Pack/Tex/t{i}.png", Png(i));
        await kit.CatalogAsync(new ImportRequest([root], Depth: AiDepth.None));
        var before = await CatalogStateAsync(kit);

        Assert.Equal(RescanStatus.Unchanged, Assert.Single((await RescanAsync(kit)).Results).Status);

        for (var i = 0; i < 6; i++) kit.Catalog.Dir.Write($"Pack/New/n{i}.png", Png(i + 4));
        kit.Catalog.Dir.Write("Pack/New/readme.txt", "notes");         // a source document: the import reads it, not an asset
        kit.Catalog.Dir.Write("Pack/New/Player.cs", "class Player {}"); // a script: noted, not imported
        File.WriteAllBytes(Path.Combine(root, "Tex", "t1.png"), Png(9)); // changed content
        File.Delete(Path.Combine(root, "Tex", "t2.png"));

        var result = Assert.Single((await RescanAsync(kit)).Results);

        Assert.Equal(RescanStatus.Changed, result.Status);
        Assert.Equal(6, result.Added.Count);
        Assert.All(result.Added, a => Assert.StartsWith("New/n", a.RelativePath));
        Assert.Equal("Tex/t1.png", Assert.Single(result.Changed).RelativePath);
        Assert.NotNull(result.Changed[0].AssetId);
        Assert.Equal("Tex/t2.png", Assert.Single(result.Removed).RelativePath);
        Assert.Equal(before, await CatalogStateAsync(kit)); // nothing changes until the user acts

        // A file only touched (same content, newer time) is not reported as changed.
        File.SetLastWriteTimeUtc(Path.Combine(root, "Tex", "t3.png"), DateTime.UtcNow.AddMinutes(5));
        Assert.DoesNotContain((await RescanAsync(kit)).Results[0].Changed, c => c.RelativePath == "Tex/t3.png");
    }

    [Fact]
    public async Task Ignored_folder()
    {
        await using var kit = await ImportKit.CreateAsync();
        var root = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        kit.Catalog.Dir.Write("Pack/a.png", Png(0));
        await kit.CatalogAsync(new ImportRequest([root], Depth: AiDepth.None));

        for (var i = 0; i < 20; i++) kit.Catalog.Dir.Write($"Pack/__MACOSX/img{i}.png", Png(i));

        var result = Assert.Single((await RescanAsync(kit)).Results);
        Assert.Empty(result.Added);
        Assert.Equal(RescanStatus.Unchanged, result.Status);
    }

    [Fact]
    public async Task Detached_drive()
    {
        await using var kit = await ImportKit.CreateAsync();
        var drive = Path.Combine(kit.Catalog.Dir.Path, "E");
        kit.Processing.Volumes.Mount("CAFE0001", "Backup", drive);
        kit.Catalog.Dir.Write("E/Packs/Forest/tree.png", Png(0));
        kit.Catalog.Dir.Write("E/Packs/Forest/rock.png", Png(1));
        kit.Catalog.Dir.Write("Local/Town/house.png", Png(2));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(drive, "Packs", "Forest")], Depth: AiDepth.None));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Local", "Town")], Depth: AiDepth.None));
        kit.Processing.Volumes.Detach("CAFE0001");

        var results = (await RescanAsync(kit)).Results;

        var forest = Assert.Single(results, r => r.Name == "Forest");
        Assert.Equal(RescanStatus.Skipped, forest.Status);
        Assert.Contains("Backup", forest.SkipReason);
        Assert.Empty(forest.Removed);
        Assert.Equal(RescanStatus.Unchanged, Assert.Single(results, r => r.Name == "Town").Status);
    }

    [Fact]
    public async Task Changed_package_and_nested_package_compare_fingerprints()
    {
        await using var kit = await ImportKit.CreateAsync();
        UnityPackageEntry Tex(string n, int i) => new(FixtureBuilder.Guid(n), $"Assets/{n}.png", Png(i));
        var package = kit.Catalog.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage([Tex("a", 0)]));
        kit.Catalog.Dir.Write("Folder/inner.unitypackage", FixtureBuilder.UnityPackage([Tex("b", 1)]));
        kit.Catalog.Dir.Write("Folder/c.png", Png(2));
        await kit.CatalogAsync(new ImportRequest([package], Depth: AiDepth.None));
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Folder")], Depth: AiDepth.None));
        await using (var db = kit.Catalog.Db())
            Assert.All(await db.Sources.Where(s => s.Kind == SourceKind.UnityPackage).ToListAsync(Ct), s => Assert.NotNull(s.Fingerprint));

        var unchanged = (await RescanAsync(kit)).Results;
        Assert.All(unchanged, r => Assert.Equal(RescanStatus.Unchanged, r.Status));

        kit.Catalog.Dir.Write("Kit.unitypackage", FixtureBuilder.UnityPackage([Tex("a", 0), Tex("a2", 3)]));
        kit.Catalog.Dir.Write("Folder/inner.unitypackage", FixtureBuilder.UnityPackage([Tex("b", 4)]));
        var results = (await RescanAsync(kit)).Results;

        var kitResult = Assert.Single(results, r => r.Kind == SourceKind.UnityPackage);
        Assert.True(kitResult.ContainerChanged);
        var folder = Assert.Single(results, r => r.Kind == SourceKind.Folder);
        Assert.Empty(folder.Added); // the nested package is a source of its own, not a new file
        var nested = Assert.Single(folder.Changed);
        Assert.Equal("inner.unitypackage", nested.RelativePath);
        long innerId;
        await using (var db = kit.Catalog.Db())
            innerId = await db.Sources.Where(s => s.ParentSourceId == folder.SourceId).Select(s => s.Id).SingleAsync(Ct);
        Assert.Equal(innerId, nested.SourceId);

        // "Update inner.unitypackage" replaces the nested source in place (spec "Explicit update").
        await kit.CatalogAsync(new ImportRequest([Path.Combine(kit.Catalog.Dir.Path, "Folder", "inner.unitypackage")], Depth: AiDepth.None,
            ReplaceSourceId: nested.SourceId));
        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(3, await db.Sources.CountAsync(Ct));
            var inner = await db.Sources.SingleAsync(s => s.Id == innerId, Ct);
            Assert.Equal(folder.SourceId, inner.ParentSourceId);
            Assert.Equal(1, await db.Assets.CountAsync(a => a.SourceId == innerId, Ct));
        }
    }

    /// <summary>Spec "Import added files": committed into the existing source, carrying its source tags.</summary>
    [Fact]
    public async Task Import_added_files_into_the_source()
    {
        await using var kit = await ImportKit.CreateAsync();
        var root = Path.Combine(kit.Catalog.Dir.Path, "Pack");
        kit.Catalog.Dir.Write("Pack/a.png", Png(0));
        await kit.CatalogAsync(new ImportRequest([root], Depth: AiDepth.None));
        long sourceId;
        await using (var db = kit.Catalog.Db()) sourceId = await db.Sources.Select(s => s.Id).SingleAsync(Ct);
        await new AssetManager.Data.Services.SourceTagService(kit.Store).AddAsync(sourceId, ["forest"], ct: Ct);
        for (var i = 0; i < 6; i++) kit.Catalog.Dir.Write($"Pack/New/n{i}.png", Png(i + 1));

        var added = Assert.Single((await RescanAsync(kit)).Results).Added.Select(a => a.RelativePath).ToList();
        var (session, job) = await kit.ImportAsync(new ImportRequest([root], Depth: AiDepth.None, IntoSourceId: sourceId, OnlyPaths: added));
        Assert.True(job.State == JobState.Completed, job.Error); // committed without confirmation
        var result = Assert.Single((await kit.Results.LoadAsync(session, Ct))!.Sources);
        Assert.Equal((sourceId, "Pack", 6), (result.SourceId, result.Name, result.Assets));

        await using (var db = kit.Catalog.Db())
        {
            Assert.Equal(1, await db.Sources.CountAsync(Ct));
            var assets = await db.Assets.Where(a => a.Name.StartsWith("n")).Include(a => a.Tags).ThenInclude(t => t.Tag).ToListAsync(Ct);
            Assert.Equal(6, assets.Count);
            Assert.All(assets, a => Assert.Equal(sourceId, a.SourceId));
            Assert.All(assets, a => Assert.Contains(a.Tags, t => t.Tag.Name == "forest"));
        }
        Assert.Equal(RescanStatus.Unchanged, (await RescanAsync(kit)).Results[0].Status);
    }
}
