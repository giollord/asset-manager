using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using AssetManager.Processing.Locations;
using AssetManager.Processing.Storage;
using AssetManager.Tests.Data;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Processing;

public class StoredOriginalsTests
{
    private const string Vol = "CAFE0001";

    /// <summary>A catalog with <paramref name="count"/> assets on volume "Backup", each a primary file plus a companion.</summary>
    private static async Task<(TestCatalog c, ProcessingKit kit, List<long> ids)> SeedAsync(int count, int fileBytes = 10)
    {
        var c = await TestCatalog.CreateAsync();
        var kit = new ProcessingKit(c.Store);
        var root = Path.Combine(kit.Dir.Path, "E");
        kit.Volumes.Mount(Vol, "Backup", root);
        await new VolumeRegistry(kit.Volumes, c.Store).RememberAsync([Vol]);
        var src = await TestData.SourceAsync(c, count, s => s.Location = new(Vol, "pack"), i =>
        {
            kit.Dir.Write($@"E\pack\m{i}.fbx", Enumerable.Range(0, fileBytes).Select(b => (byte)(b + i)).ToArray());
            kit.Dir.Write($@"E\pack\m{i}.png", $"tex{i}");
            return new Asset
            {
                Name = $"model{i}",
                Type = AssetType.Model3D,
                Files =
                [
                    new AssetFile { Role = FileRole.Primary, Location = new(Vol, $@"pack\m{i}.fbx"), FileName = $"m{i}.fbx", Extension = ".fbx" },
                    new AssetFile { Role = FileRole.Companion, Location = new(Vol, $@"pack\m{i}.png"), FileName = $"m{i}.png", Extension = ".png" },
                ],
            };
        });
        return (c, kit, src.Assets.Select(a => a.Id).ToList());
    }

    private static StoredOriginalsService Service(TestCatalog c, ProcessingKit kit) => new(c.Store, kit.Resolver) { ChunkSize = 16 };

    [Fact]
    public async Task Store_selected_assets()
    {
        var (c, kit, ids) = await SeedAsync(10, fileBytes: 40);
        await using var _ = c;
        using var __ = kit;
        var selected = ids.Take(8).ToList();

        var result = await Service(c, kit).StoreAsync(selected);

        Assert.Equal(8, result.Stored);
        Assert.Empty(result.Skipped);
        await using var db = c.Db();
        Assert.Equal(8, await db.Assets.CountAsync(a => a.IsStored));
        Assert.Equal(16, await db.StoredFiles.CountAsync());
        Assert.Equal(8 * 3 + 8, await db.StoredFileChunks.CountAsync()); // 40-byte fbx = 3 chunks of 16, png = 1
    }

    [Fact]
    public async Task Use_stored_copy_offline()
    {
        var (c, kit, ids) = await SeedAsync(2, fileBytes: 40);
        await using var _ = c;
        using var __ = kit;
        await Service(c, kit).StoreAsync([ids[0]]);
        kit.Volumes.Detach(Vol);

        var availability = await new AvailabilityService(kit.Volumes, c.Store).GetAsync(ids);
        Assert.True(availability[ids[0]].IsAvailable);
        Assert.True(availability[ids[0]].UsesStoredCopy);
        Assert.False(availability[ids[1]].IsAvailable);

        await using var db = c.Db();
        var files = await db.AssetFiles.Where(f => f.AssetId == ids[0]).OrderBy(f => f.Id).ToListAsync();
        using var primary = await kit.Resolver.ResolveAsync(files[0]);
        Assert.Equal(ResolvedFrom.StoredCopy, primary.From);
        Assert.Equal(Enumerable.Range(0, 40).Select(b => (byte)b).ToArray(), await File.ReadAllBytesAsync(primary.Path));
        using var companion = await kit.Resolver.ResolveAsync(files[1]);
        Assert.Equal("tex0", await File.ReadAllTextAsync(companion.Path));
    }

    [Fact]
    public async Task Remove_stored_copies_keeps_metadata()
    {
        var (c, kit, ids) = await SeedAsync(3);
        await using var _ = c;
        using var __ = kit;
        await using (var db = c.Db())
        {
            db.Previews.Add(new PreviewBlob { AssetId = ids[0], Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = [1] });
            await db.SaveChangesAsync();
        }
        var svc = Service(c, kit);
        await svc.StoreAsync(ids);

        Assert.Equal(3, await svc.RemoveAsync(ids));

        await using var check = c.Db();
        Assert.Equal(0, await check.StoredFiles.CountAsync());
        Assert.Equal(0, await check.StoredFileChunks.CountAsync());
        Assert.Equal(0, await check.Assets.CountAsync(a => a.IsStored));
        Assert.Equal(6, await check.AssetFiles.CountAsync());
        Assert.Equal(1, await check.Previews.CountAsync());

        kit.Volumes.Detach(Vol);
        var availability = await new AvailabilityService(kit.Volumes, c.Store).GetAsync(ids);
        Assert.All(availability.Values, a => Assert.False(a.IsAvailable));
    }

    [Fact]
    public async Task Store_when_original_unavailable_skips_and_reports()
    {
        var (c, kit, ids) = await SeedAsync(3);
        await using var _ = c;
        using var __ = kit;
        File.Delete(Path.Combine(kit.Dir.Path, @"E\pack\m1.png")); // a companion is missing

        var result = await Service(c, kit).StoreAsync(ids);

        Assert.Equal(2, result.Stored);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(ids[1], skipped.AssetId);
        Assert.Contains("m1.png", skipped.Reason);
        await using var db = c.Db();
        Assert.False((await db.Assets.SingleAsync(a => a.Id == ids[1])).IsStored);
        Assert.Equal(4, await db.StoredFiles.CountAsync()); // no partial copy of the skipped asset
    }

    [Fact]
    public async Task Space_reclaimed_after_removal()
    {
        var (c, kit, ids) = await SeedAsync(4, fileBytes: 2_000_000);
        await using var _ = c;
        using var __ = kit;
        var svc = new StoredOriginalsService(c.Store, kit.Resolver);
        await svc.StoreAsync(ids);
        await using (var db = c.Db()) await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        var full = new FileInfo(c.Path).Length;
        Assert.True(full > 8_000_000, $"catalog should hold the copies ({full} bytes)");

        await svc.RemoveAsync(ids);
        var afterRemove = new FileInfo(c.Path).Length;
        Assert.True(afterRemove < 1_000_000, $"incremental vacuum should shrink the file ({afterRemove} bytes)");

        await svc.CompactAsync();
        Assert.True(new FileInfo(c.Path).Length <= afterRemove);
    }

    [Fact]
    public async Task Detached_volume_marks_unavailable_while_metadata_still_works()
    {
        var (c, kit, ids) = await SeedAsync(3);
        await using var _ = c;
        using var __ = kit;
        await TestData.TagAsync(c, ids[0], "barrel");
        kit.Volumes.Detach(Vol);

        var availability = await new AvailabilityService(kit.Volumes, c.Store).GetAsync(ids);
        Assert.All(availability.Values, a =>
        {
            Assert.False(a.IsAvailable);
            Assert.Contains("drive \"Backup\"", a.Missing);
        });

        // Search, tags and metadata do not need the originals.
        await using var db = c.Db();
        var hits = await db.Database.SqlQueryRaw<long>(
            "SELECT rowid AS Value FROM asset_fts WHERE asset_fts MATCH {0}", SearchIndexer.BuildMatch("model0")!).ToListAsync();
        Assert.Equal([ids[0]], hits);
        var a0 = await db.Assets.Include(a => a.Tags).ThenInclude(t => t.Tag).Include(a => a.Files).SingleAsync(a => a.Id == ids[0]);
        Assert.Equal("barrel", Assert.Single(a0.Tags).Tag.Name);
        Assert.Equal(2, a0.Files.Count);

        // Actions needing the original report the missing drive.
        var ex = await Assert.ThrowsAsync<OriginalUnavailableException>(() => kit.Resolver.ResolveAsync(a0.Files[0]));
        Assert.Contains("\"Backup\"", ex.Message);

        // Reconnected under another letter: available again without re-import.
        var f = Path.Combine(kit.Dir.Path, "F");
        Directory.Move(Path.Combine(kit.Dir.Path, "E"), f);
        kit.Volumes.Mount(Vol, "Backup", f);
        availability = await new AvailabilityService(kit.Volumes, c.Store).GetAsync(ids);
        Assert.All(availability.Values, a => Assert.True(a.IsAvailable));
    }
}
