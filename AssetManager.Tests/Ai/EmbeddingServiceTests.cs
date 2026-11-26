using AssetManager.AI.Enrichment;
using AssetManager.AI.Runtime;
using AssetManager.Core.Domain;
using AssetManager.Core.Settings;
using AssetManager.Data.Search;
using AssetManager.Data.Services;
using AssetManager.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AssetManager.Tests.Ai;

/// <summary>Deterministic 768-d vectors from the text's words, recording every input.</summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            Inputs.Add(text);
            var v = new float[768];
            foreach (var word in text.ToLowerInvariant().Split([' ', '.', ',', '|', ':'], StringSplitOptions.RemoveEmptyEntries))
                v[(int)((uint)word.GetHashCode() % 256)] += 1; // only the first 256 dims matter after truncation
            result.Add(new Embedding<float>(v));
        }
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

[Collection(AiModelCollection.Name)]
public class EmbeddingServiceTests
{
    private static async Task<long> AddAssetAsync(TestCatalog c, string name, string description, long? sessionId = null)
    {
        await using var db = c.Db();
        var source = new Source { DisplayName = "pack", ImportSessionId = sessionId };
        var asset = new Asset { Name = name, Description = description, Type = AssetType.Model3D, ImportSessionId = sessionId };
        source.Assets.Add(asset);
        db.Add(source);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static async Task<bool> DirtyAsync(TestCatalog c, long id)
    {
        await using var db = c.Db();
        return await db.AllAssets.Where(a => a.Id == id).Select(a => a.EmbeddingDirty).SingleAsync();
    }

    /// <summary>Design D16: the model loads on the first query, and the refresh loads it only when there is work.</summary>
    [Fact]
    public async Task Model_loads_when_first_needed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        var generator = new FakeEmbeddingGenerator();
        EmbeddingModel? loaded = null;
        var loads = 0;
        var service = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => loaded, configured: () => true,
            ensureLoaded: _ => { loads++; loaded = new EmbeddingModel(generator, "fake-v1"); return Task.CompletedTask; });

        Assert.True(service.IsAvailable); // offered before it is loaded
        Assert.Equal(0, await service.RefreshDirtyAsync(ct: ct)); // nothing to embed: stays unloaded
        Assert.Equal(0, loads);

        Assert.NotNull(await service.EmbedQueryAsync("crate", ct));
        Assert.Equal(1, loads);
        Assert.NotNull(await service.EmbedQueryAsync("barrel", ct));
        Assert.Equal(1, loads); // already running
    }

    [Fact]
    public async Task Dirty_assets_load_the_model_for_the_refresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var c = await TestCatalog.CreateAsync();
        var generator = new FakeEmbeddingGenerator();
        EmbeddingModel? loaded = null;
        var service = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => loaded, configured: () => true,
            ensureLoaded: _ => { loaded = new EmbeddingModel(generator, "fake-v1"); return Task.CompletedTask; });
        var id = await AddAssetAsync(c, "Crate", "A wooden crate.");

        Assert.Equal(1, await service.RefreshDirtyAsync(ct: ct));
        Assert.NotNull(loaded);
        Assert.False(await DirtyAsync(c, id));
    }

    [Fact]
    public async Task Embedding_refresh_after_description_edit()
    {
        await using var c = await TestCatalog.CreateAsync();
        var generator = new FakeEmbeddingGenerator();
        var service = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => new EmbeddingModel(generator, "fake-v1"));
        var id = await AddAssetAsync(c, "Crate", "A wooden crate.");
        await new TagService(c.Store).AddTagsAsync([id], ["prop"], TagOrigin.AI, TestContext.Current.CancellationToken);

        Assert.Equal(1, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        Assert.False(await DirtyAsync(c, id));
        Assert.Equal("title: Crate | text: Model3D. A wooden crate. Tags: prop.", Assert.Single(generator.Inputs));
        Assert.Equal(0, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken)); // nothing left to do

        await new MetadataEditService(c.Store).EditAssetAsync(id, new AssetUpdate { Description = "A metal barrel." }, TestContext.Current.CancellationToken);
        Assert.True(await DirtyAsync(c, id));
        Assert.Equal(1, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        Assert.Contains("A metal barrel.", generator.Inputs[^1]);
        Assert.False(await DirtyAsync(c, id));

        await using var db = c.Db();
        var stored = await db.Embeddings.SingleAsync(e => e.AssetId == id, TestContext.Current.CancellationToken);
        Assert.Equal((256 * 4, "fake-v1"), (stored.Vector.Length, stored.Model));
    }

    [Fact]
    public async Task Tags_renames_and_tracked_edits_mark_assets_dirty()
    {
        await using var c = await TestCatalog.CreateAsync();
        var service = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => new EmbeddingModel(new FakeEmbeddingGenerator(), "fake-v1"));
        var id = await AddAssetAsync(c, "Crate", "A crate.");
        var other = await AddAssetAsync(c, "Rock", "A rock.");
        await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken);

        await new TagService(c.Store).AddTagsAsync([id], ["wood"], TagOrigin.User, TestContext.Current.CancellationToken);
        Assert.True(await DirtyAsync(c, id));
        Assert.False(await DirtyAsync(c, other));
        await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken);

        await using (var db = c.Db())
        {
            var tag = await db.Tags.SingleAsync(t => t.Name == "wood", TestContext.Current.CancellationToken);
            tag.Name = "timber";
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(await DirtyAsync(c, id));
        await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken);

        await using (var db = c.Db())
        {
            var asset = await db.Assets.SingleAsync(a => a.Id == other, TestContext.Current.CancellationToken);
            asset.IsStored = true; // not embedded text: stays clean
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.False(await DirtyAsync(c, other));
            asset.Type = AssetType.Texture;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        Assert.True(await DirtyAsync(c, other));
    }

    [Fact]
    public async Task Staged_assets_wait_and_a_new_model_reembeds_everything()
    {
        await using var c = await TestCatalog.CreateAsync();
        var id = await AddAssetAsync(c, "Crate", "A crate.");
        long sessionId;
        await using (var db = c.Db())
        {
            var session = new ImportSession { CreatedAt = DateTime.UtcNow };
            db.Add(session);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            sessionId = session.Id;
        }
        var staged = await AddAssetAsync(c, "Staged", "Not committed yet.", sessionId);
        var current = new EmbeddingModel(new FakeEmbeddingGenerator(), "fake-v1");
        var service = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => current);

        Assert.Equal(1, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        Assert.True(await DirtyAsync(c, staged));

        current = new EmbeddingModel(new FakeEmbeddingGenerator(), "fake-v2");
        Assert.Equal(1, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        await using var read = c.Db();
        Assert.Equal("fake-v2", (await read.Embeddings.SingleAsync(e => e.AssetId == id, TestContext.Current.CancellationToken)).Model);

        var none = new EmbeddingService(c.Store, new InMemoryVectorIndex(c.Store), () => null);
        Assert.False(none.IsAvailable);
        Assert.Equal(0, await none.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        Assert.Null(await none.EmbedQueryAsync("crate", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Real_embedding_model_finds_the_semantically_closest_asset()
    {
        var path = AiModelFactAttribute.Embed;
        Assert.SkipWhen(path is null, "Set ASSETMANAGER_AI_EMBED to an embedding GGUF to run.");
        await using var c = await TestCatalog.CreateAsync();
        var crate = await AddAssetAsync(c, "Kiste_01", "A wooden box with metal corners, used as a prop in warehouses.");
        await AddAssetAsync(c, "Laser_Shot", "A short sci-fi laser gun sound.");
        await AddAssetAsync(c, "Oak_Tree", "A tall stylized oak tree with a thick trunk.");
        await using var host = new LlamaServerHost("embedding");
        await host.StartAsync(new LlamaServerOptions(path!, null, AiAcceleration.Auto, true, 2048), TestContext.Current.CancellationToken);
        Assert.True(host.State == AiServerState.Ready, host.Error);
        var generator = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("x"), new OpenAI.OpenAIClientOptions { Endpoint = new Uri(host.Endpoint!, "v1") })
            .GetEmbeddingClient("local").AsIEmbeddingGenerator();
        var index = new InMemoryVectorIndex(c.Store);
        var service = new EmbeddingService(c.Store, index, () => new EmbeddingModel(generator, Path.GetFileName(path!)));

        Assert.Equal(3, await service.RefreshDirtyAsync(ct: TestContext.Current.CancellationToken));
        var hits = await index.SearchAsync(await service.EmbedQueryAsync("crate", TestContext.Current.CancellationToken), 3, TestContext.Current.CancellationToken);

        Assert.Equal(crate, hits[0].AssetId);
    }
}
