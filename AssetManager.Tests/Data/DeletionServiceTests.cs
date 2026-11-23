using AssetManager.Core.Domain;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetManager.Tests.Data;

public class DeletionServiceTests
{
    private static async Task<(TestCatalog c, Source src, string file)> SeedAsync(int count)
    {
        var c = await TestCatalog.CreateAsync();
        var file = c.Dir.Write("pack/original.png", [1, 2, 3, 4]);
        var src = await TestData.SourceAsync(c, count, make: i => new Asset
        {
            Name = $"a{i}",
            IsStored = true,
            Files = [new AssetFile { Location = new(null, file), FileName = "original.png", Extension = ".png", Size = 4,
                Stored = new StoredFile { Size = 4, ChunkCount = 1 } }],
        });
        await using (var db = c.Db())
        {
            foreach (var a in src.Assets)
            {
                db.Previews.Add(new PreviewBlob { AssetId = a.Id, Kind = PreviewKind.Thumbnail, Mime = "image/webp", Bytes = [9] });
                db.Embeddings.Add(new AssetEmbedding { AssetId = a.Id, Vector = new byte[16], Model = "t" });
                db.StoredFileChunks.Add(new StoredFileChunk { StoredFileId = a.Files[0].Stored!.Id, Seq = 0, Bytes = [1, 2, 3, 4] });
            }
            await db.SaveChangesAsync();
        }
        foreach (var a in src.Assets) await TestData.TagAsync(c, a.Id, "doomed", $"only{a.Name}");
        return (c, src, file);
    }

    [Fact]
    public async Task Delete_selected_assets_cascades_and_keeps_originals()
    {
        var (c, src, file) = await SeedAsync(20);
        await using var _ = c;
        var selected = src.Assets.Take(12).Select(a => a.Id).ToList();

        var removed = await new DeletionService(c.Store).DeleteAssetsAsync(selected);

        Assert.Equal(12, removed);
        await using var db = c.Db();
        Assert.Equal(8, await db.Assets.CountAsync());
        Assert.Equal(8, await db.AssetFiles.CountAsync());
        Assert.Equal(8, await db.Previews.CountAsync());
        Assert.Equal(8, await db.Embeddings.CountAsync());
        Assert.Equal(8, await db.StoredFiles.CountAsync());
        Assert.Equal(8, await db.StoredFileChunks.CountAsync());
        Assert.Equal(16, await db.AssetTags.CountAsync());
        Assert.Equal(9, await db.Tags.CountAsync()); // "doomed" + 8 per-asset tags; orphans removed
        Assert.Empty(await db.Database.SqlQueryRaw<long>(
            "SELECT rowid AS Value FROM asset_fts WHERE asset_fts MATCH {0}", SearchIndexer.BuildMatch("a0")!).ToListAsync());
        Assert.True(File.Exists(file));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Delete_source_removes_nested_sources_and_assets()
    {
        var (c, src, file) = await SeedAsync(3);
        await using var _ = c;
        long nestedId;
        await using (var db = c.Db())
        {
            var nested = new Source { DisplayName = "inner.unitypackage", ParentSourceId = src.Id, Assets = [new Asset { Name = "inner" }] };
            db.Add(nested);
            await db.SaveChangesAsync();
            nestedId = nested.Id;
        }
        var svc = new DeletionService(c.Store);
        Assert.Equal(4, await svc.CountSourceAssetsAsync([src.Id]));

        Assert.Equal(4, await svc.DeleteSourcesAsync([src.Id]));

        await using var check = c.Db();
        Assert.Equal(0, await check.Sources.CountAsync());
        Assert.Equal(0, await check.Assets.CountAsync());
        Assert.Equal(0, await check.StoredFileChunks.CountAsync());
        Assert.Equal(0, await check.Tags.CountAsync());
        Assert.True(File.Exists(file));
    }
}
